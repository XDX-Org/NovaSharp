using NovaSharp.Async;
using NovaSharp.Commands;
using NovaSharp.Configuration;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.LanguageServices;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using NovaSharp.Verification;
using NovaSharp.Workspace;

namespace NovaSharp;

/// <summary>
/// The application-lifetime services the workbench is built from.
/// </summary>
/// <remarks>
/// Still a small explicit composition root rather than a container, and now at the size where that is a deliberate
/// choice rather than an absent one: the ordering below is the dependency graph, and a container would hide the fact
/// that the log has no dependencies, that notifications and commands both need it, and that everything else needs
/// those. It is also what keeps disposal order visible in <see cref="Shutdown"/>.
/// </remarks>
internal static class Workbench
{
    /// <summary>How long shutdown waits before giving up and letting the process exit.</summary>
    private static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(10);

    private static readonly BoundedWorkQueue BackgroundWork = new(capacity: 32, workerCount: 2);
    private static readonly BoundedWorkQueue SolutionWork = new(capacity: 4, workerCount: 1);
    private static readonly IDocumentFileStore Files = new DocumentFileStore();
    private static readonly DocumentTextCodec Codec = new();
    private static readonly IApplicationPaths ApplicationPaths = new ApplicationPaths();

    /// <summary>Where NovaSharp records what it did. Bounded, and redacted by the callers that write to it.</summary>
    internal static IWorkbenchLog Log { get; } = new BoundedWorkbenchLog();

    /// <summary>Where NovaSharp tells the user something.</summary>
    internal static INotificationService Notifications { get; } = new NotificationService(Log);

    internal static DiagnosticStore Diagnostics { get; } = new();

    /// <summary>The one place a command identifier turns into behaviour.</summary>
    internal static ICommandRegistry Commands { get; } = new CommandRegistry(Log);

    /// <summary>The path and document-identity seam. Nothing above this layer inspects the host operating system.</summary>
    internal static IWorkspacePaths Paths { get; } = new WorkspacePaths();

    /// <summary>Settings in their user and workspace scopes.</summary>
    internal static ConfigurationService Configuration { get; } =
        new(ApplicationPaths, Files, BackgroundWork);

    internal static WorkspacePersistenceService WorkspacePersistence { get; } =
        new(ApplicationPaths, Files, BackgroundWork);

    private static SolutionWarmCache SolutionWarmCache { get; } =
        new(ApplicationPaths, Paths, Files, BackgroundWork);

    internal static WorkspaceExplorerService Explorer { get; } = CreateExplorer();

    internal static SolutionWorkspaceService Solutions { get; } = CreateSolutions();

    internal static CSharpLanguageService CSharp { get; } = new(Solutions);

    internal static SolutionDiscovery SolutionDiscovery { get; } = new(Paths, BackgroundWork);

    /// <summary>Reads documents off the UI thread, through the bounded background queue.</summary>
    internal static DocumentLoader Loader { get; } = new(Paths, Files, Codec, BackgroundWork);

    /// <summary>Writes documents off the UI thread, through the same queue.</summary>
    internal static DocumentSaver Saver { get; } = new(Paths, Files, Codec, BackgroundWork);

    /// <summary>
    /// The URI-keyed document collection owned by the mounted editor workbench.
    /// </summary>
    /// <remarks>
    /// Held here because window close and Explorer relocation callbacks run outside the editor component.
    /// </remarks>
    internal static DocumentRegistry? Documents { get; set; }

    internal static DocumentSession? ActiveDocument => Documents?.ActiveDocument;

    /// <summary>Creates the session for one editor, wiring it to the shared services.</summary>
    internal static DocumentSession CreateSession(IEditorHost host)
    {
        var session = new DocumentSession(host,
            Loader,
            Saver,
            Files,
            new FileSystemDocumentWatcher(),
            BackgroundWork,
            Notifications,
            static () => Configuration.Current.Settings);
        session.ReplicaChanged += Solutions.QueueReplica;
        session.ReplicaClosed += closed => Solutions.RemoveReplica(closed.DocumentUri, closed.WasDirty);
        return session;
    }

    internal static DocumentRegistry CreateDocumentRegistry(IEditorHost host) =>
        new(host, Paths, WorkspacePersistence, () => CreateSession(host), Notifications, () => Explorer.Snapshot.RootPath);

    private static WorkspaceExplorerService CreateExplorer()
    {
        var watcher = new FileSystemWorkspaceWatcher(Paths);
        return new WorkspaceExplorerService(
            Paths,
            new WorkspaceFileSystem(Paths, BackgroundWork),
            watcher,
            WorkspacePersistence,
            Notifications);
    }

    private static SolutionWorkspaceService CreateSolutions()
    {
        var service = new SolutionWorkspaceService(
            Paths,
            new MSBuildSolutionLoader(),
            SolutionWork,
            Diagnostics,
            Notifications,
            Log,
            warmCache: NativeSmokeTest.IsEnabled ? null : SolutionWarmCache,
            workspaceRoot: static () => Explorer.Snapshot.RootPath);
        Explorer.FilesChanged += service.ObserveWorkspaceChanges;
        return service;
    }

    /// <summary>Reads both settings scopes and reports anything that had to be ignored.</summary>
    /// <remarks>
    /// Problems are raised as one notification rather than one each: a file with five bad keys is one file to go and
    /// fix, and five notices for it would bury everything else.
    /// </remarks>
    internal static async Task LoadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        SettingsResolution resolution;
        try
        {
            resolution = await Configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Write(LogLevel.Error, "configuration", "Settings could not be read; the defaults are in force.", exception);
            Notifications.Raise(
                NotificationIds.SettingsProblem,
                NotificationSeverity.Warning,
                $"Your settings could not be read, so NovaSharp is using its defaults: {exception.Message}");
            return;
        }

        if (resolution.IsClean)
        {
            Notifications.Dismiss(NotificationIds.SettingsProblem);
            return;
        }

        Notifications.Raise(
            NotificationIds.SettingsProblem,
            NotificationSeverity.Warning,
            "Some settings could not be used: " + string.Join(" ", resolution.Problems.Select(problem => problem.Message)));
    }

    /// <summary>Runs <paramref name="work"/> on the bounded background queue.</summary>
    /// <remarks>
    /// For the work that belongs to a component rather than to a service — computing what a menu will show, and
    /// nothing that outlives the click that started it.
    /// </remarks>
    internal static Task<T> RunInBackgroundAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default) =>
        BackgroundWork.EnqueueAsync(work, cancellationToken);

    /// <summary>Cancels background work and waits for the owned workers, with a deadline.</summary>
    /// <remarks>
    /// Blocking, by necessity. The entry point must stay synchronous to keep its single-threaded apartment, so there
    /// is no caller left to await. This runs after the message loop has returned, on a thread with no synchronisation
    /// context and no work that re-enters it, which is what makes blocking here safe rather than merely expedient.
    /// </remarks>
    internal static void Shutdown()
    {
        Explorer.FilesChanged -= Solutions.ObserveWorkspaceChanges;
        var documents = Documents;
        Documents = null;
        try
        {
            if (!ShutdownAsync(documents).Wait(ShutdownDeadline))
            {
                Log.Write(LogLevel.Warning, "shutdown", "Workbench cleanup exceeded its deadline; process exit will continue.");
            }
        }
        catch (Exception exception)
        {
            Log.Write(LogLevel.Warning, "shutdown", "Workbench cleanup completed with an error; process exit will continue.", exception);
        }
    }

    private static async Task ShutdownAsync(DocumentRegistry? documents)
    {
        await Task.Yield();
        if (documents is not null)
        {
            await documents.DisposeAsync().ConfigureAwait(false);
        }
        await CSharp.DisposeAsync().ConfigureAwait(false);
        await Solutions.DisposeAsync().ConfigureAwait(false);
        await Explorer.DisposeAsync().ConfigureAwait(false);
        await SolutionWork.DisposeAsync().ConfigureAwait(false);
        await BackgroundWork.DisposeAsync().ConfigureAwait(false);
    }
}
