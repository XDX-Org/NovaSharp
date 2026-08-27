using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.LanguageServices;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using Xunit;

namespace NovaSharp.Tests;

public sealed class CSharpLanguageServiceIntegrationTests : IAsyncDisposable
{
    private readonly BoundedWorkQueue _solutionWork = new(capacity: 8, workerCount: 2);
    private readonly WorkspacePaths _paths = new();
    private readonly SolutionWorkspaceService _solutions;
    private readonly CSharpLanguageService _language;

    public CSharpLanguageServiceIntegrationTests()
    {
        var log = new BoundedWorkbenchLog();
        _solutions = new(
            _paths,
            new MSBuildSolutionLoader(),
            _solutionWork,
            new DiagnosticStore(),
            new NotificationService(log),
            log);
        _language = new(_solutions, capacity: 8, workerCount: 2);
    }

    [Fact]
    public async Task SdkFixture_RespectsReferencesUsingsAccessibilityNullableAndLinkedContext()
    {
        var root = FindRepositoryRoot();
        var fixture = Path.Combine(root, "tests", "fixtures", "phase-06");
        await _solutions.OpenAsync(
            Path.Combine(fixture, "Representative.slnx"),
            TestContext.Current.CancellationToken);

        var appPath = Path.Combine(fixture, "App", "Program.cs");
        var appUri = _paths.ToDocumentUri(appPath);
        var appDiskText = await File.ReadAllTextAsync(appPath, TestContext.Current.CancellationToken);
        const string appSource = "using Representative; Class1? value = null; value?.ToS; Hidden";
        await SetReplicaAsync(appUri, appPath, appSource, 21);
        Assert.Equal(appDiskText, await File.ReadAllTextAsync(appPath, TestContext.Current.CancellationToken));
        var appContext = _solutions.GetDocumentContexts(appUri).Single(context => context.IsActive);
        var completion = await _language.GetCompletionsAsync(Request(
            "reference",
            appUri,
            appContext.ProjectId.Id.ToString(),
            21,
            appSource.IndexOf("ToS", StringComparison.Ordinal) + 3), TestContext.Current.CancellationToken);
        var inaccessible = await _language.GetCompletionsAsync(Request(
            "accessibility",
            appUri,
            appContext.ProjectId.Id.ToString(),
            21,
            appSource.Length), TestContext.Current.CancellationToken);
        var hover = await _language.GetHoverAsync(Request(
            "nullable",
            appUri,
            appContext.ProjectId.Id.ToString(),
            21,
            appSource.LastIndexOf("value", StringComparison.Ordinal) + 1), TestContext.Current.CancellationToken);

        Assert.Contains(completion!.Items, item => item.Label == "ToString");
        Assert.DoesNotContain(inaccessible!.Items, item => item.Label == "HiddenClass");
        Assert.Contains("?", hover!.Signature, StringComparison.Ordinal);

        var sharedPath = Path.Combine(fixture, "Shared.cs");
        var sharedUri = _paths.ToDocumentUri(sharedPath);
        var sharedDiskText = await File.ReadAllTextAsync(sharedPath, TestContext.Current.CancellationToken);
        const string sharedSource = "internal static class Shared {\n#if PHASE6_APP\npublic static string Active => \"\";\n#else\npublic static int Active => 0;\n#endif\nstatic void M() { _ = Active.Len; } }";
        await SetReplicaAsync(sharedUri, sharedPath, sharedSource, 22);
        Assert.Equal(sharedDiskText, await File.ReadAllTextAsync(sharedPath, TestContext.Current.CancellationToken));
        var contexts = _solutions.GetDocumentContexts(sharedUri);
        var app = contexts.Single(context => context.ProjectName.Contains("App", StringComparison.Ordinal));
        var library = contexts.First(context => context.ProjectName.Contains("Library", StringComparison.Ordinal));
        var position = sharedSource.IndexOf("Len", StringComparison.Ordinal) + 3;

        await _solutions.SetActiveContextAsync(sharedUri, app.ProjectId, TestContext.Current.CancellationToken);
        var appCompletion = await _language.GetCompletionsAsync(
            Request("linked-app", sharedUri, app.ProjectId.Id.ToString(), 22, position),
            TestContext.Current.CancellationToken);
        var activePosition = sharedSource.LastIndexOf("Active", StringComparison.Ordinal) + 1;
        var appHover = await _language.GetHoverAsync(
            Request("linked-app-hover", sharedUri, app.ProjectId.Id.ToString(), 22, activePosition),
            TestContext.Current.CancellationToken);
        await _solutions.SetActiveContextAsync(sharedUri, library.ProjectId, TestContext.Current.CancellationToken);
        var libraryHover = await _language.GetHoverAsync(
            Request("linked-library-hover", sharedUri, library.ProjectId.Id.ToString(), 22, activePosition),
            TestContext.Current.CancellationToken);

        Assert.Contains(appCompletion!.Items, item => item.Label == "Length");
        Assert.Contains("string", appHover!.Signature, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("int", libraryHover!.Signature, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SetReplicaAsync(Uri uri, string path, string source, long sequence)
    {
        _solutions.QueueReplica(new(uri, path, new DocumentReplica(source, sequence, sequence), sequence));
        await _solutions.WaitForReplicaAsync(uri, sequence, TestContext.Current.CancellationToken);
    }

    private LanguageRequest Request(string id, Uri uri, string projectId, long sequence, int position) => new(
        id,
        uri.AbsoluteUri,
        projectId,
        _solutions.Snapshot.SourceVersion,
        sequence,
        position,
        IsExplicit: true);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NovaSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the NovaSharp repository root.");
    }

    public async ValueTask DisposeAsync()
    {
        await _language.DisposeAsync();
        await _solutions.DisposeAsync();
        await _solutionWork.DisposeAsync();
    }
}
