using System.Text.Json;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using Xunit;

namespace NovaSharp.Tests;

public sealed class SolutionWarmCacheTests : IAsyncDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("novasharp-warm-workspace").FullName;
    private readonly string _state = Directory.CreateTempSubdirectory("novasharp-warm-state").FullName;
    private readonly BoundedWorkQueue _background = new(8, 1);
    private readonly WorkspacePaths _paths = new();
    private readonly DocumentFileStore _files = new();

    private SolutionWarmCache Create() =>
        new(new CacheApplicationPaths(_state), _paths, _files, _background);

    [Fact]
    public async Task SaveAndLoad_RoundTripsPortableDisplayMetadataAcrossInstances()
    {
        var snapshot = await CreateSnapshotAsync();
        await Create().SaveAsync(_workspace, snapshot, TestContext.Current.CancellationToken);

        var restored = await Create().LoadAsync(_workspace, TestContext.Current.CancellationToken);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Path, restored.Path);
        Assert.Equal(snapshot.Projects[0].Path, restored.Projects[0].Path);
        Assert.Equal(snapshot.Projects[0].Documents[0].Path, restored.Projects[0].Documents[0].Path);
        Assert.Equal(
            _paths.ToDocumentUri(snapshot.Projects[0].Documents[0].Path).AbsoluteUri,
            restored.Projects[0].Documents[0].DocumentUri);

        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Create().FilePath, TestContext.Current.CancellationToken));
        var projectPath = json.RootElement.GetProperty("projects")[0].GetProperty("path");
        Assert.True(projectPath.GetProperty("workspaceRelative").GetBoolean());
        Assert.Equal("src/App.csproj", projectPath.GetProperty("value").GetString());
    }

    [Fact]
    public async Task LoadAsync_RejectsAChangedEvaluationInput()
    {
        var snapshot = await CreateSnapshotAsync();
        var cache = Create();
        await cache.SaveAsync(_workspace, snapshot, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(snapshot.Projects[0].Path, "\n<!-- changed -->", TestContext.Current.CancellationToken);

        var restored = await cache.LoadAsync(_workspace, TestContext.Current.CancellationToken);

        Assert.Null(restored);
    }

    [Fact]
    public async Task LoadAsync_RejectsAnotherWorkspaceAndCorruptData()
    {
        var snapshot = await CreateSnapshotAsync();
        var cache = Create();
        await cache.SaveAsync(_workspace, snapshot, TestContext.Current.CancellationToken);
        var other = Directory.CreateDirectory(Path.Combine(_workspace, "other")).FullName;

        Assert.Null(await cache.LoadAsync(other, TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(cache.FilePath, "{ broken", TestContext.Current.CancellationToken);
        Assert.Null(await cache.LoadAsync(_workspace, TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(
            cache.FilePath,
            """{"schemaVersion":1,"workspaceUri":"file:///broken","solutionPath":null,"name":null,"projects":null,"inputs":null}""",
            TestContext.Current.CancellationToken);
        Assert.Null(await cache.LoadAsync(_workspace, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExternalSolution_IsNeitherRestoredForNorRetainedByAWorkspace()
    {
        var cache = Create();
        await cache.SaveAsync(_workspace, await CreateSnapshotAsync(), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(cache.FilePath));

        var externalSolution = Path.Combine(_state, "External.slnx");
        await File.WriteAllTextAsync(externalSolution, "<Solution />", TestContext.Current.CancellationToken);
        var state = _files.GetState(externalSolution);
        var stale = new
        {
            schemaVersion = SolutionWarmCache.CurrentSchemaVersion,
            workspaceUri = _paths.ToDocumentUri(_workspace).AbsoluteUri,
            solutionPath = new { value = externalSolution, workspaceRelative = false },
            name = "External.slnx",
            projects = Array.Empty<object>(),
            inputs = new[]
            {
                new
                {
                    path = new { value = externalSolution, workspaceRelative = false },
                    exists = true,
                    length = state.Length,
                    lastWriteTimeUtcTicks = state.LastWriteTimeUtc.UtcTicks,
                },
            },
        };
        await File.WriteAllTextAsync(
            cache.FilePath,
            JsonSerializer.Serialize(stale, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            TestContext.Current.CancellationToken);

        Assert.Null(await cache.LoadAsync(_workspace, TestContext.Current.CancellationToken));

        var externalSnapshot = (await CreateSnapshotAsync()) with { Path = externalSolution, Name = "External.slnx" };
        await cache.SaveAsync(_workspace, externalSnapshot, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(cache.FilePath));
    }

    [Fact]
    public async Task ClearAsync_RemovesOnlyTheDisposableCacheFile()
    {
        var cache = Create();
        await cache.SaveAsync(_workspace, await CreateSnapshotAsync(), TestContext.Current.CancellationToken);
        var unrelated = Path.Combine(_state, "settings.json");
        await File.WriteAllTextAsync(unrelated, "{}", TestContext.Current.CancellationToken);

        await cache.ClearAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(cache.FilePath));
        Assert.True(File.Exists(unrelated));
    }

    private async Task<SolutionWorkspaceSnapshot> CreateSnapshotAsync()
    {
        var solution = Path.Combine(_workspace, "Workspace.slnx");
        var project = Path.Combine(_workspace, "src", "App.csproj");
        var source = Path.Combine(_workspace, "src", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(solution, "<Solution />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(source, "class Program;", TestContext.Current.CancellationToken);
        var document = new SolutionDocumentSnapshot(
            "document",
            "Program.cs",
            source,
            _paths.ToDocumentUri(source).AbsoluteUri);
        var context = new ProjectContextSnapshot(
            "project",
            "App",
            project,
            "net10.0",
            true,
            [document],
            [],
            ["TRACE"],
            "13.0",
            "Enable");
        return new SolutionWorkspaceSnapshot(
            SolutionLoadState.Ready,
            solution,
            "Workspace.slnx",
            [context]);
    }

    public async ValueTask DisposeAsync()
    {
        await _background.DisposeAsync();
        Directory.Delete(_workspace, recursive: true);
        Directory.Delete(_state, recursive: true);
    }

    private sealed class CacheApplicationPaths(string directory) : IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = directory;
    }
}
