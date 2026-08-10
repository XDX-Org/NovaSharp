using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaSharp;

public enum BuildOperation { Restore, Build, Rebuild, Clean, Run }
public enum BuildTaskState { Queued, Running, Succeeded, Failed, Canceled }
public enum OutputStream { System, StandardOutput, StandardError }

public sealed record BuildRequest(string ProjectPath, BuildOperation Operation, string Configuration = "Debug",
    string? TargetFramework = null, string? LaunchProfile = null, IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null, string? WorkingDirectory = null);

public sealed record OutputEntry(long Sequence, DateTime TimestampUtc, OutputStream Stream, string Text,
    string? FilePath = null, int Line = 0, int Column = 0);

public sealed record BuildTask(Guid Id, BuildRequest Request, BuildTaskState State, DateTime QueuedUtc,
    DateTime? StartedUtc = null, TimeSpan? Duration = null, int? ExitCode = null, string? Error = null);

internal sealed class OutputChannel(int maxEntries = 10_000, int maxBytes = 4 * 1024 * 1024)
{
    private readonly object _gate = new();
    private readonly Queue<(OutputEntry Entry, int Bytes)> _entries = new();
    private long _sequence;
    private int _bytes;

    internal event Action? Changed;
    internal IReadOnlyList<OutputEntry> Entries { get { lock (_gate) return _entries.Select(item => item.Entry).ToArray(); } }

    internal void Add(OutputStream stream, string text, string? filePath = null, int line = 0, int column = 0)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var entry = new OutputEntry(Interlocked.Increment(ref _sequence), DateTime.UtcNow, stream, text,
            filePath, line, column);
        lock (_gate)
        {
            _entries.Enqueue((entry, bytes));
            _bytes += bytes;
            while (_entries.Count > maxEntries || _bytes > maxBytes)
            {
                var removed = _entries.Dequeue();
                _bytes -= removed.Bytes;
            }
        }
        Changed?.Invoke();
    }

    internal void Clear() { lock (_gate) { _entries.Clear(); _bytes = 0; } Changed?.Invoke(); }

    internal async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        var lines = Entries.Select(entry => $"[{entry.TimestampUtc:O}] [{entry.Stream}] {entry.Text}");
        await File.WriteAllLinesAsync(path, lines, cancellationToken);
    }
}

internal sealed partial class BuildRunService : IAsyncDisposable
{
    private static readonly string[] InheritedEnvironment =
        ["PATH", "DOTNET_ROOT", "DOTNET_HOST_PATH", "NUGET_PACKAGES", "HOME", "USERPROFILE", "TMP", "TEMP", "TMPDIR",
            "SystemRoot", "windir", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)"];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _processGate = new();
    private readonly LanguageDiagnosticStore _diagnostics;
    private readonly List<BuildTask> _tasks = [];
    private Process? _activeProcess;
    private CancellationTokenSource? _activeCancellation;
    private long _diagnosticVersion;

    internal BuildRunService(LanguageDiagnosticStore diagnostics, OutputChannel? output = null)
    {
        _diagnostics = diagnostics;
        Output = output ?? new();
    }

    internal OutputChannel Output { get; }
    internal IReadOnlyList<BuildTask> Tasks { get { lock (_tasks) return _tasks.ToArray(); } }
    internal BuildTask? ActiveTask { get { lock (_tasks) return _tasks.LastOrDefault(task => task.State == BuildTaskState.Running); } }
    internal event Action? Changed;

    internal async Task<BuildTask> ExecuteAsync(BuildRequest request, CancellationToken cancellationToken = default)
    {
        var task = new BuildTask(Guid.NewGuid(), request with { ProjectPath = Path.GetFullPath(request.ProjectPath) },
            BuildTaskState.Queued, DateTime.UtcNow);
        lock (_tasks) _tasks.Add(task);
        Changed?.Invoke();
        var entered = false;
        try
        {
            Validate(task.Request);
            await _operationGate.WaitAsync(cancellationToken);
            entered = true;
            Output.Clear();
            var started = DateTime.UtcNow;
            task = Update(task with { State = BuildTaskState.Running, StartedUtc = started });
            if (request.Operation is not BuildOperation.Run) _diagnostics.Clear(LanguageDiagnosticSource.Build);
            var result = await RunProcessAsync(task.Request, cancellationToken);
            task = Update(task with { State = result == 0 ? BuildTaskState.Succeeded : BuildTaskState.Failed,
                Duration = DateTime.UtcNow - started, ExitCode = result });
        }
        catch (OperationCanceledException)
        {
            task = Update(task with { State = BuildTaskState.Canceled,
                Duration = task.StartedUtc is { } start ? DateTime.UtcNow - start : TimeSpan.Zero });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            Output.Add(OutputStream.System, exception.Message);
            task = Update(task with { State = BuildTaskState.Failed,
                Duration = task.StartedUtc is { } start ? DateTime.UtcNow - start : TimeSpan.Zero, Error = exception.Message });
        }
        finally { if (entered) _operationGate.Release(); }
        return task;
    }

    internal void Stop()
    {
        lock (_processGate)
        {
            _activeCancellation?.Cancel();
            Kill(_activeProcess);
        }
    }

    internal Task<BuildTask>? RestartAsync(CancellationToken cancellationToken = default)
    {
        var request = ActiveTask?.Request;
        if (request?.Operation != BuildOperation.Run) return null;
        Stop();
        return ExecuteAsync(request, cancellationToken);
    }

    internal async Task<bool> SendInputAsync(string text, CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_processGate) process = _activeProcess;
        if (process is null || process.HasExited) return false;
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        return true;
    }

    private async Task<int> RunProcessAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var arguments = CreateArguments(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet", WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.ProjectPath)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var name in InheritedEnvironment)
            if (Environment.GetEnvironmentVariable(name) is { } value) startInfo.Environment[name] = value;
        if (request.Environment is not null)
            foreach (var pair in request.Environment) startInfo.Environment[pair.Key] = pair.Value;

        Output.Add(OutputStream.System, $"dotnet {string.Join(' ', RedactArguments(arguments))}");
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("The .NET process could not be started.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_processGate) { _activeProcess = process; _activeCancellation = linked; }
        using var registration = linked.Token.Register(() => Kill(process));
        var diagnostics = new List<LanguageDiagnostic>();
        try
        {
            var stdout = ReadLinesAsync(process.StandardOutput, OutputStream.StandardOutput, request, diagnostics);
            var stderr = ReadLinesAsync(process.StandardError, OutputStream.StandardError, request, diagnostics);
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                Kill(process);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
                throw;
            }
            await Task.WhenAll(stdout, stderr);
            PublishDiagnostics(diagnostics);
            return process.ExitCode;
        }
        finally
        {
            lock (_processGate) { if (ReferenceEquals(_activeProcess, process)) { _activeProcess = null; _activeCancellation = null; } }
        }
    }

    private async Task ReadLinesAsync(StreamReader reader, OutputStream stream, BuildRequest request,
        List<LanguageDiagnostic> diagnostics)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (TryParseDiagnostic(line, request, out var diagnostic))
            {
                lock (diagnostics) diagnostics.Add(diagnostic);
                Output.Add(stream, line, diagnostic.DocumentPath, diagnostic.StartLine, diagnostic.StartColumn);
            }
            else Output.Add(stream, line);
        }
    }

    private void PublishDiagnostics(IReadOnlyList<LanguageDiagnostic> diagnostics)
    {
        var version = Interlocked.Increment(ref _diagnosticVersion);
        foreach (var group in diagnostics.GroupBy(item => item.DocumentPath))
            _diagnostics.Replace(group.Key, version, LanguageDiagnosticSource.Build,
                group.DistinctBy(item => (item.Id, item.Range, item.Message, item.ProjectName)).ToArray());
    }

    private static IReadOnlyList<string> CreateArguments(BuildRequest request)
    {
        var args = new List<string>();
        switch (request.Operation)
        {
            case BuildOperation.Restore: args.Add("restore"); break;
            case BuildOperation.Clean: args.Add("clean"); break;
            case BuildOperation.Build: args.Add("build"); break;
            case BuildOperation.Rebuild: args.AddRange(["build", "-t:Rebuild"]); break;
            case BuildOperation.Run: args.AddRange(["run", "--project"]); break;
        }
        args.Add(request.ProjectPath);
        if (request.Operation != BuildOperation.Restore)
            args.AddRange(["--configuration", request.Configuration]);
        if (!string.IsNullOrWhiteSpace(request.TargetFramework)) args.AddRange(["--framework", request.TargetFramework]);
        if (request.Operation == BuildOperation.Run && !string.IsNullOrWhiteSpace(request.LaunchProfile))
            args.AddRange(["--launch-profile", request.LaunchProfile]);
        if (request.Operation == BuildOperation.Run && request.Arguments is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(request.Arguments);
        }
        return args;
    }

    private static void Validate(BuildRequest request)
    {
        if (!File.Exists(request.ProjectPath) || !Path.GetExtension(request.ProjectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A valid .csproj startup project is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Configuration) || request.Configuration.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("The build configuration is invalid.", nameof(request));
    }

    private BuildTask Update(BuildTask updated)
    {
        lock (_tasks)
        {
            var index = _tasks.FindIndex(item => item.Id == updated.Id);
            if (index >= 0) _tasks[index] = updated;
        }
        Changed?.Invoke();
        return updated;
    }

    private static IEnumerable<string> RedactArguments(IReadOnlyList<string> arguments)
    {
        var redactNext = false;
        foreach (var argument in arguments)
        {
            if (redactNext) { yield return "[redacted]"; redactNext = false; continue; }
            var equals = argument.IndexOf('=');
            if (equals > 0 && SecretName().IsMatch(argument[..equals]))
            {
                yield return argument[..(equals + 1)] + "[redacted]";
                continue;
            }
            yield return argument;
            redactNext = argument.StartsWith('-') && SecretName().IsMatch(argument);
        }
    }

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool TryParseDiagnostic(string line, BuildRequest request, out LanguageDiagnostic diagnostic)
    {
        var match = DiagnosticLine().Match(line);
        if (!match.Success) { diagnostic = null!; return false; }
        var path = match.Groups["path"].Value;
        if (!Path.IsPathRooted(path)) path = Path.GetFullPath(path, Path.GetDirectoryName(request.ProjectPath)!);
        else if (OperatingSystem.IsMacOS()) path = NormalizeMacPath(path, request.ProjectPath);
        var startLine = Math.Max(0, int.Parse(match.Groups["line"].Value) - 1);
        var startColumn = Math.Max(0, int.Parse(match.Groups["column"].Value) - 1);
        var endLine = match.Groups["endLine"].Success ? int.Parse(match.Groups["endLine"].Value) - 1 : startLine;
        var endColumn = match.Groups["endColumn"].Success ? int.Parse(match.Groups["endColumn"].Value) - 1 : startColumn + 1;
        var severity = match.Groups["severity"].Value == "error" ? LanguageDiagnosticSeverity.Error : LanguageDiagnosticSeverity.Warning;
        var range = RangeFromLocation(path, startLine, startColumn, endLine, endColumn);
        diagnostic = new(match.Groups["code"].Value, LanguageDiagnosticSource.Build, severity,
            match.Groups["message"].Value.Trim(), path, range, startLine, startColumn,
            Path.GetFileNameWithoutExtension(request.ProjectPath), endLine, endColumn);
        return true;
    }

    private static string NormalizeMacPath(string path, string projectPath)
    {
        const string privatePrefix = "/private";
        var projectUsesPrivatePrefix = projectPath.StartsWith(privatePrefix + '/', StringComparison.Ordinal);
        if (path.StartsWith(privatePrefix + '/', StringComparison.Ordinal) && !projectUsesPrivatePrefix)
        {
            var alias = path[privatePrefix.Length..];
            if (File.Exists(alias)) return alias;
        }
        else if (!path.StartsWith(privatePrefix + '/', StringComparison.Ordinal) && projectUsesPrivatePrefix)
        {
            var alias = privatePrefix + path;
            if (File.Exists(alias)) return alias;
        }
        return path;
    }

    private static TextRange RangeFromLocation(string path, int startLine, int startColumn, int endLine, int endColumn)
    {
        try
        {
            var text = File.ReadAllText(path);
            var starts = new List<int> { 0 };
            for (var index = 0; index < text.Length; index++)
                if (text[index] == '\n') starts.Add(index + 1);
            var start = starts[Math.Min(startLine, starts.Count - 1)] + startColumn;
            var end = starts[Math.Min(endLine, starts.Count - 1)] + endColumn;
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            return new(start, Math.Max(1, end - start));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return new(0, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _operationGate.WaitAsync();
        _operationGate.Release();
        _operationGate.Dispose();
    }

    [GeneratedRegex("^(?<path>.+?)\\((?<line>\\d+),(?<column>\\d+)(?:,(?<endLine>\\d+),(?<endColumn>\\d+))?\\):\\s*(?<severity>error|warning)\\s+(?<code>[^: ]+)\\s*:\\s*(?<message>.*?)(?:\\s+\\[[^]]+\\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticLine();
    [GeneratedRegex("(?i)(password|passwd|token|secret|api[-_]?key|connection[-_]?string)")]
    private static partial Regex SecretName();
}
