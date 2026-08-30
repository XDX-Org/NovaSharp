using Microsoft.Build.Framework;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace NovaSharp.Solutions;

public interface ISolutionLoader
{
    Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken);
}

public sealed class LoadedSolutionWorkspace : IAsyncDisposable
{
    public LoadedSolutionWorkspace(
        Microsoft.CodeAnalysis.Workspace workspace,
        Solution solution,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<string>? rawBuildLog = null)
    {
        Workspace = workspace;
        Solution = solution;
        Diagnostics = diagnostics ?? [];
        RawBuildLog = rawBuildLog ?? [];
    }

    public Microsoft.CodeAnalysis.Workspace Workspace { get; }
    public Solution Solution { get; set; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<string> RawBuildLog { get; }

    public ValueTask DisposeAsync()
    {
        Workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class MSBuildSolutionLoader : ISolutionLoader
{
    private static readonly Lock RegistrationGate = new();

    public async Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(progress);

        var extension = Path.GetExtension(path);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("NovaSharp loads SDK-style .sln, .slnx, and .csproj inputs.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        RegisterMSBuild();
        return await LoadRegisteredAsync(path, extension, progress, cancellationToken).ConfigureAwait(false);
    }

    private static void RegisterMSBuild()
    {
        lock (RegistrationGate)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private static async Task<LoadedSolutionWorkspace> LoadRegisteredAsync(
        string path,
        string extension,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var rawLog = new BoundedMSBuildLogger();
        rawLog.Record($"Loading {path}");
        var workspace = MSBuildWorkspace.Create();
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.SkipUnrecognizedProjects = true;

        var workspaceDiagnostics = new List<string>();
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            lock (workspaceDiagnostics)
            {
                if (workspaceDiagnostics.Count < 1_000)
                {
                    workspaceDiagnostics.Add(args.Diagnostic.Message);
                }
            }
        });

        var roslynProgress = new Progress<ProjectLoadProgress>(item => progress.Report(new ProjectLoadStatusSnapshot(
            item.FilePath,
            item.Operation.ToString(),
            item.TargetFramework,
            item.ElapsedTime)));

        try
        {
            Solution solution;
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await workspace.OpenProjectAsync(path, rawLog, roslynProgress, cancellationToken).ConfigureAwait(false);
                solution = project.Solution;
            }
            else
            {
                solution = await workspace.OpenSolutionAsync(path, rawLog, roslynProgress, cancellationToken).ConfigureAwait(false);
            }
            rawLog.Record($"Loaded {solution.ProjectIds.Count} project contexts.");

            lock (workspaceDiagnostics)
            {
                return new LoadedSolutionWorkspace(workspace, solution, [.. workspaceDiagnostics], rawLog.Entries);
            }
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
        finally
        {
            rawLog.Shutdown();
        }
    }
}

internal sealed class BoundedMSBuildLogger : ILogger
{
    private readonly Lock _gate = new();
    private readonly Queue<string> _entries = new();
    private IEventSource? _source;

    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Normal;
    public string? Parameters { get; set; }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Initialize(IEventSource eventSource)
    {
        _source = eventSource;
        eventSource.AnyEventRaised += OnEvent;
    }

    public void Shutdown()
    {
        if (_source is { } source)
        {
            source.AnyEventRaised -= OnEvent;
            _source = null;
        }
    }

    private void OnEvent(object sender, BuildEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Message))
        {
            Record(args.Message);
        }
    }

    public void Record(string message)
    {
        lock (_gate)
        {
            _entries.Enqueue(message);
            while (_entries.Count > 2_000)
            {
                _entries.Dequeue();
            }
        }
    }
}
