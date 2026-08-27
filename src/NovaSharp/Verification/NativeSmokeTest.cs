using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaSharp.Editing;
using NovaSharp.Solutions;

namespace NovaSharp.Verification;

internal sealed record NativeSmokeTestOptions(string SourcePath, string SolutionPath, string ResultPath, string ProfilePath);

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
    bool SolutionLoaded,
    int ProjectContexts,
    string? Error);

/// <summary>Drives the packaged application without dialogs for the phase 1–2 native-host gate.</summary>
internal static class NativeSmokeTest
{
    private const string SourceOption = "--phase-smoke-source";
    private const string ResultOption = "--phase-smoke-result";
    private const string ProfileOption = "--phase-smoke-profile";
    private const string SolutionOption = "--phase-smoke-solution";

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
        string? solution = null;
        var remaining = new List<string>(args.Length);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is not SourceOption and not ResultOption and not ProfileOption and not SolutionOption)
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
            else if (argument == ProfileOption)
            {
                profile = args[index];
            }
            else
            {
                solution = args[index];
            }
        }

        if (source is null || result is null || profile is null || solution is null)
        {
            if (source is not null || result is not null || profile is not null || solution is not null)
            {
                throw new ArgumentException(
                    $"{SourceOption}, {SolutionOption}, {ResultOption}, and {ProfileOption} must be supplied together.",
                    nameof(args));
            }
        }

        if (source is not null && solution is not null && result is not null && profile is not null)
        {
            _options = new NativeSmokeTestOptions(
                Path.GetFullPath(source),
                Path.GetFullPath(solution),
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
            var workingSetBytes = process.WorkingSet64;
            await Workbench.Solutions.OpenAsync(options.SolutionPath, cancellationToken).ConfigureAwait(false);
            var solution = Workbench.Solutions.Snapshot;
            result = new NativeSmokeTestResult(
                Success: session.Status.IsOpen &&
                    replica is not null &&
                    editor.DocumentLength == replica.Length &&
                    editor.DedicatedWorker &&
                    editor.ModelCount == 1 &&
                    editor.ExternalRequestCount == 0 &&
                    solution.State == SolutionLoadState.Ready &&
                    solution.Projects.Count >= 6,
                RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture,
                interactiveEditorMilliseconds,
                baselineWorkingSetBytes,
                workingSetBytes,
                editor,
                editor.DocumentLength,
                solution.State == SolutionLoadState.Ready,
                solution.Projects.Count,
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
                SolutionLoaded: false,
                ProjectContexts: 0,
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
