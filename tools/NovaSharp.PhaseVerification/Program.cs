using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Text;

const long idleMemoryLimitBytes = 400L * 1024 * 1024;
const int processDeadlineSeconds = 45;

var options = Options.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

var profileDirectory = Directory.CreateTempSubdirectory("novasharp-phase-profile-").FullName;
NativeResult provisioning;
NativeResult cold;
NativeResult warm;
NativeResult[] warmSamples;
NativeResult large;
try
{
    // Browser-profile creation is installation state, not repeatable process startup. Provision it once, retain that
    // measurement separately, then apply the cold/warm product budgets to launches sharing the resulting profile.
    provisioning = await RunAsync("provisioning", options, profileDirectory);
    cold = await RunAsync("cold", options, profileDirectory);
    warmSamples = new NativeResult[3];
    for (var index = 0; index < warmSamples.Length; index++)
    {
        warmSamples[index] = await RunAsync($"warm-{index + 1}", options, profileDirectory);
    }

    warm = warmSamples.OrderBy(result => result.InteractiveEditorMilliseconds).ElementAt(warmSamples.Length / 2);
    var largeSource = Path.Combine(Path.GetTempPath(), $"novasharp-large-{Guid.NewGuid():N}.cs");
    try
    {
        await File.WriteAllTextAsync(largeSource, CreateLargeDocument());
        large = await RunAsync("large", options, profileDirectory, largeSource);
    }
    finally
    {
        File.Delete(largeSource);
    }
}
finally
{
    await DeleteDirectoryAsync(profileDirectory);
}

var managed = await RunManagedPerformanceAsync(options);
var failures = new List<string>();

Check(provisioning.Success, "browser profile provisioning", provisioning.Error);
Check(cold.Success, "cold native smoke", cold.Error);
Check(warmSamples.All(result => result.Success), "warm native smoke",
    string.Join("; ", warmSamples.Where(result => !result.Success).Select(result => result.Error)));
Check(cold.InteractiveEditorMilliseconds <= options.ColdStartLimitMilliseconds,
    $"cold startup <= {options.ColdStartLimitMilliseconds} ms", $"{cold.InteractiveEditorMilliseconds} ms");
Check(warm.InteractiveEditorMilliseconds <= options.WarmStartLimitMilliseconds,
    $"warm startup median <= {options.WarmStartLimitMilliseconds} ms", $"{warm.InteractiveEditorMilliseconds} ms");
Check(warm.WorkingSetBytes <= idleMemoryLimitBytes,
    $"idle working set <= {idleMemoryLimitBytes / 1024 / 1024} MB", $"{warm.WorkingSetBytes / 1024 / 1024} MB");
Check(large.Success, "10 MB native smoke", large.Error);
Check(large.WorkingSetBytes - large.BaselineWorkingSetBytes <= 60L * 1024 * 1024,
    "10 MB document adds <= 60 MB working set",
    $"{Math.Max(0, large.WorkingSetBytes - large.BaselineWorkingSetBytes) / 1024 / 1024} MB");
Check(managed.ReplicationP95Milliseconds <= 50,
    "managed replication p95 <= 50 ms", $"{managed.ReplicationP95Milliseconds:F2} ms");
Check(managed.ReplicationP99Milliseconds <= 150,
    "managed replication p99 <= 150 ms", $"{managed.ReplicationP99Milliseconds:F2} ms");
Check(managed.MaximumReplicationQueueDepth <= managed.ReplicationCapacity * 0.25,
    "managed replication queue <= 25% capacity", $"{managed.MaximumReplicationQueueDepth}/{managed.ReplicationCapacity}");
Check(managed.SaveBarrierP95Milliseconds <= 120,
    "1 MB save barrier p95 <= 120 ms", $"{managed.SaveBarrierP95Milliseconds:F2} ms");
Check(managed.SaveP95Milliseconds <= options.SaveLimitMilliseconds,
    $"1 MB save p95 <= {options.SaveLimitMilliseconds} ms", $"{managed.SaveP95Milliseconds:F2} ms");

var record = new VerificationRecord(
    options.FixtureName,
    Environment.ProcessorCount,
    Environment.Version.ToString(),
    options.ColdStartLimitMilliseconds,
    options.WarmStartLimitMilliseconds,
    options.SaveLimitMilliseconds,
    provisioning,
    cold,
    warm,
    warmSamples,
    large,
    managed,
    failures);
var recordPath = Path.Combine(options.OutputDirectory, "phase-01-02-native.json");
await File.WriteAllTextAsync(
    recordPath,
    JsonSerializer.Serialize(record, Serialization.JsonOptions),
    CancellationToken.None);

Console.WriteLine($"Verification record: {recordPath}");
return failures.Count == 0 ? 0 : 1;

void Check(bool condition, string name, string? detail)
{
    Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {name}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" — {detail}")}");
    if (!condition)
    {
        failures.Add(string.IsNullOrWhiteSpace(detail) ? name : $"{name}: {detail}");
    }
}

static async Task<NativeResult> RunAsync(
    string label,
    Options options,
    string profilePath,
    string? sourcePath = null)
{
    var resultPath = Path.Combine(options.OutputDirectory, $"{label}-native.json");
    var start = new ProcessStartInfo(options.ApplicationPath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    start.ArgumentList.Add("--phase-smoke-source");
    start.ArgumentList.Add(sourcePath ?? options.SourcePath);
    start.ArgumentList.Add("--phase-smoke-result");
    start.ArgumentList.Add(resultPath);
    start.ArgumentList.Add("--phase-smoke-profile");
    start.ArgumentList.Add(profilePath);

    using var process = Process.Start(start) ?? throw new InvalidOperationException("The NovaSharp process did not start.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(processDeadlineSeconds));

    try
    {
        await process.WaitForExitAsync(deadline.Token);
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw new TimeoutException($"NovaSharp did not finish its {label} native smoke test in {processDeadlineSeconds} seconds.");
    }

    var output = await standardOutput;
    var error = await standardError;
    if (!string.IsNullOrWhiteSpace(output))
    {
        Console.WriteLine(output.TrimEnd());
    }

    if (!string.IsNullOrWhiteSpace(error))
    {
        Console.Error.WriteLine(error.TrimEnd());
    }

    if (!File.Exists(resultPath))
    {
        throw new InvalidOperationException($"NovaSharp exited with {process.ExitCode} without writing {resultPath}.");
    }

    await using var stream = File.OpenRead(resultPath);
    return await JsonSerializer.DeserializeAsync<NativeResult>(stream, Serialization.JsonOptions)
        ?? throw new InvalidDataException($"{resultPath} did not contain a smoke result.");
}

static async Task DeleteDirectoryAsync(string path)
{
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException &&
            deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100);
        }
    }
}

static async Task<ManagedPerformanceRecord> RunManagedPerformanceAsync(Options options)
{
    const int replicationCapacity = 256;
    var replicationSamples = new double[1_000];
    var replica = new DocumentReplica(string.Empty, sequence: 1, alternativeSequence: 1);
    await using (var pump = new DocumentReplicationPump(
        replica,
        _ => Task.FromResult(replica.Snapshot()),
        replicationCapacity))
    {
        var maximumQueueDepth = 0;
        for (var index = 0; index < replicationSamples.Length; index++)
        {
            var sequence = index + 2L;
            var started = Stopwatch.GetTimestamp();
            if (!pump.TryEnqueue(new TextEditBatch(
                options.SourcePath,
                sequence - 1,
                sequence,
                sequence,
                EditOrigins.User,
                [new TextEdit(index, index, "x")])))
            {
                throw new InvalidOperationException("The managed replication benchmark overflowed.");
            }

            maximumQueueDepth = Math.Max(maximumQueueDepth, pump.QueueDepth);
            await pump.WaitForSequenceAsync(sequence, CancellationToken.None);
            replicationSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var barrierSamples = new double[20];
        var barrierReplica = new DocumentReplica(new string('x', 1024 * 1024), sequence: 1, alternativeSequence: 1);
        await using (var barrierPump = new DocumentReplicationPump(
            barrierReplica,
            _ => Task.FromResult(barrierReplica.Snapshot()),
            replicationCapacity))
        {
            for (var index = 0; index < barrierSamples.Length; index++)
            {
                var sequence = index + 2L;
                var started = Stopwatch.GetTimestamp();
                if (!barrierPump.TryEnqueue(new TextEditBatch(
                    options.SourcePath,
                    sequence - 1,
                    sequence,
                    sequence,
                    EditOrigins.User,
                    [new TextEdit(barrierReplica.Length, barrierReplica.Length, "x")])))
                {
                    throw new InvalidOperationException("The save-barrier benchmark overflowed.");
                }

                await barrierPump.WaitForSequenceAsync(sequence, CancellationToken.None);
                barrierSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }

        var benchmarkDirectory = Path.Combine(Path.GetTempPath(), $"novasharp-phase-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(benchmarkDirectory);
        try
        {
            var target = Path.Combine(benchmarkDirectory, "one-megabyte.cs");
            var text = new string('x', 1024 * 1024 - 1) + "\n";
            await File.WriteAllTextAsync(target, text);
            var store = new DocumentFileStore();
            var paths = new WorkspacePaths();
            await using var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);
            var saver = new DocumentSaver(paths, store, new DocumentTextCodec(), queue);
            var record = new DocumentRecord(
                paths.ToDocumentUri(target),
                target,
                Path.GetFileName(target),
                TextEncodings.Utf8,
                LineEndingStyle.Lf,
                LineEndingsWereMixed: false,
                DecodedWithFallback: false,
                store.GetState(target),
                SavedSequence: 1);
            var snapshot = new DocumentSnapshot(text, Sequence: 2, AlternativeSequence: 2);
            var saveSamples = new double[20];
            for (var index = 0; index < saveSamples.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var result = await saver.SaveAsync(record, snapshot, cancellationToken: CancellationToken.None);
                saveSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (result.Status != DocumentSaveStatus.Saved)
                {
                    throw new InvalidOperationException($"The managed save benchmark failed: {result.Message}");
                }

                record = result.Record;
            }

            return new ManagedPerformanceRecord(
                Percentile(replicationSamples, 0.95),
                Percentile(replicationSamples, 0.99),
                replicationCapacity,
                maximumQueueDepth,
                Percentile(barrierSamples, 0.95),
                Percentile(saveSamples, 0.95));
        }
        finally
        {
            Directory.Delete(benchmarkDirectory, recursive: true);
        }
    }
}

static string CreateLargeDocument()
{
    const int length = 10 * 1024 * 1024;
    const string line = "// 0123456789abcdef0123456789abcdef0123456789abcdef0123456789ab\n";
    var text = new StringBuilder(length);
    while (text.Length + line.Length <= length)
    {
        text.Append(line);
    }

    var remaining = length - text.Length;
    if (remaining > 0)
    {
        text.Append('/', remaining - 1).Append('\n');
    }

    return text.ToString();
}

static double Percentile(double[] values, double percentile)
{
    Array.Sort(values);
    return values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * percentile) - 1)];
}

internal sealed record Options(
    string ApplicationPath,
    string SourcePath,
    string OutputDirectory,
    string FixtureName,
    int ColdStartLimitMilliseconds,
    int WarmStartLimitMilliseconds,
    int SaveLimitMilliseconds)
{
    internal static Options Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must be supplied as --name value pairs.");
            }

            values.Add(arguments[index], arguments[index + 1]);
        }

        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing {name}.");

        int PositiveInteger(string name, int defaultValue)
        {
            if (!values.TryGetValue(name, out var value))
            {
                return defaultValue;
            }

            return int.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{name} must be a positive integer.");
        }

        return new Options(
            Path.GetFullPath(Required("--application")),
            Path.GetFullPath(Required("--source")),
            Path.GetFullPath(Required("--output")),
            Required("--fixture-name"),
            PositiveInteger("--cold-start-limit", 2_500),
            PositiveInteger("--warm-start-limit", 1_600),
            PositiveInteger("--save-limit", 250));
    }
}

internal sealed record EditorInfo(
    string MonacoVersion,
    bool DedicatedWorker,
    int ModelCount,
    int ExternalRequestCount,
    int DocumentLength,
    int ReplicationCapacity,
    int ReplicationQueueDepth,
    int ReplicationMaximumQueueDepth,
    int ReplicationOverflowCount);

internal sealed record NativeResult(
    bool Success,
    string RuntimeIdentifier,
    string OsDescription,
    string Architecture,
    long InteractiveEditorMilliseconds,
    long BaselineWorkingSetBytes,
    long WorkingSetBytes,
    EditorInfo? Editor,
    int DocumentLength,
    string? Error);

internal sealed record VerificationRecord(
    string FixtureName,
    int ProcessorCount,
    string DotNetRuntime,
    int ColdStartLimitMilliseconds,
    int WarmStartLimitMilliseconds,
    int SaveLimitMilliseconds,
    NativeResult Provisioning,
    NativeResult Cold,
    NativeResult Warm,
    IReadOnlyList<NativeResult> WarmSamples,
    NativeResult LargeDocument,
    ManagedPerformanceRecord ManagedPerformance,
    IReadOnlyList<string> Failures);

internal sealed record ManagedPerformanceRecord(
    double ReplicationP95Milliseconds,
    double ReplicationP99Milliseconds,
    int ReplicationCapacity,
    int MaximumReplicationQueueDepth,
    double SaveBarrierP95Milliseconds,
    double SaveP95Milliseconds);

internal static class Serialization
{
    internal static JsonSerializerOptions JsonOptions { get; } =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
