using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaSharp.Editing;

namespace NovaSharp.Verification;

internal sealed record NativeSmokeTestOptions(string SourcePath, string ResultPath, string ProfilePath);

internal sealed record NativeSmokeTestResult(
    bool Success,
    string RuntimeIdentifier,
    string OsDescription,
    Architecture Architecture,
    long InteractiveEditorMilliseconds,
    long BaselineWorkingSetBytes,
    long WorkingSetBytes,
    EditorRuntimeInfo? Editor,
    int DocumentLength,
    string? Error);

/// <summary>Drives the packaged application without dialogs for the phase 1–2 native-host gate.</summary>
internal static class NativeSmokeTest
{
    private const string SourceOption = "--phase-smoke-source";
    private const string ResultOption = "--phase-smoke-result";
    private const string ProfileOption = "--phase-smoke-profile";

    private static readonly Stopwatch Startup = Stopwatch.StartNew();
    private static NativeSmokeTestOptions? _options;
    private static int _started;

    internal static bool IsEnabled => _options is not null;

    internal static string? ProfilePath => _options?.ProfilePath;

    internal static string[] Configure(string[] args)
    {
        string? source = null;
        string? result = null;
        string? profile = null;
        var remaining = new List<string>(args.Length);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is not SourceOption and not ResultOption and not ProfileOption)
            {
                remaining.Add(argument);
                continue;
            }

            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{argument} requires a path.", nameof(args));
            }

            if (argument == SourceOption)
            {
                source = args[index];
            }
            else if (argument == ResultOption)
            {
                result = args[index];
            }
            else
            {
                profile = args[index];
            }
        }

        if (source is null || result is null || profile is null)
        {
            if (source is not null || result is not null || profile is not null)
            {
                throw new ArgumentException(
                    $"{SourceOption}, {ResultOption}, and {ProfileOption} must be supplied together.",
                    nameof(args));
            }
        }

        if (source is not null && result is not null && profile is not null)
        {
            _options = new NativeSmokeTestOptions(
                Path.GetFullPath(source),
                Path.GetFullPath(result),
                Path.GetFullPath(profile));
        }

        return [.. remaining];
    }

    internal static async Task RunAsync(
        DocumentSession session,
        IEditorHost host,
        CancellationToken cancellationToken = default)
    {
        var options = _options;
        if (options is null || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        NativeSmokeTestResult result;
        try
        {
            CompactHeap();
            using var process = Process.GetCurrentProcess();
            var baselineWorkingSetBytes = process.WorkingSet64;
            await session.OpenAsync(options.SourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var interactiveEditorMilliseconds = Startup.ElapsedMilliseconds;
            var editor = await host.GetRuntimeInfoAsync(cancellationToken).ConfigureAwait(false);
            var replica = session.Replica;

            // The budget is steady-state memory. Opening a document necessarily creates short-lived decode and interop
            // buffers; compact them before reading the resident set so the result describes the open editor.
            CompactHeap();

            process.Refresh();
            result = new NativeSmokeTestResult(
                Success: session.Status.IsOpen &&
                    replica is not null &&
                    editor.DocumentLength == replica.Length &&
                    editor.DedicatedWorker &&
                    editor.ModelCount == 1 &&
                    editor.ExternalRequestCount == 0,
                RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture,
                interactiveEditorMilliseconds,
                baselineWorkingSetBytes,
                process.WorkingSet64,
                editor,
                editor.DocumentLength,
                Error: null);
        }
        catch (Exception exception)
        {
            result = new NativeSmokeTestResult(
                Success: false,
                RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture,
                Startup.ElapsedMilliseconds,
                BaselineWorkingSetBytes: 0,
                WorkingSetBytes: 0,
                Editor: null,
                DocumentLength: 0,
                Error: exception.ToString());
        }

        await File.WriteAllTextAsync(
            options.ResultPath,
            JsonSerializer.Serialize(result, CreateJsonOptions()),
            cancellationToken).ConfigureAwait(false);

        Environment.ExitCode = result.Success ? 0 : 1;
        Program.App.MainWindow.Invoke(Program.App.MainWindow.Close);
    }

    private static void CompactHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
