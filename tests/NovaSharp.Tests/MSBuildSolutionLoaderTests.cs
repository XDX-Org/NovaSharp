using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using Xunit;

namespace NovaSharp.Tests;

public sealed class MSBuildSolutionLoaderTests
{
    [Fact]
    public async Task RepositorySolution_LoadsSdkProjectKindsReferencesAndTargetContexts()
    {
        var root = FindRepositoryRoot();
        var progress = new RecordingProgress();
        var loader = new MSBuildSolutionLoader();

        await using var loaded = await loader.LoadAsync(
            Path.Combine(root, "NovaSharp.slnx"),
            progress,
            TestContext.Current.CancellationToken);

        var projects = loaded.Solution.Projects.ToArray();
        Assert.Contains(projects, project => project.Name == "NovaSharp");
        Assert.Contains(projects, project => project.Name.Contains("App", StringComparison.Ordinal));
        Assert.Contains(projects, project => project.Name.Contains("Library", StringComparison.Ordinal));
        Assert.Contains(projects, project => project.Name.Contains("Web", StringComparison.Ordinal));

        var app = projects.Single(project => project.FilePath!.EndsWith("App.csproj", StringComparison.Ordinal));
        Assert.Single(app.ProjectReferences);
        Assert.Contains("PHASE6_APP", ((CSharpParseOptions) app.ParseOptions!).PreprocessorSymbolNames);
        Assert.Equal(NullableContextOptions.Enable, ((CSharpCompilationOptions) app.CompilationOptions!).NullableContextOptions);

        var sharedPath = Path.Combine(root, "tests", "fixtures", "phase-06", "Shared.cs");
        var paths = new WorkspacePaths();
        Assert.True(projects.SelectMany(static project => project.Documents).Count(document =>
            document.FilePath is not null && paths.IsSamePath(document.FilePath, sharedPath)) >= 2);
        Assert.Contains(progress.Items, item => item.TargetFramework == "net10.0");
        Assert.Contains(progress.Items, item => item.TargetFramework == "netstandard2.0");

        var novaSharp = projects.Single(project => project.Name == "NovaSharp");
        Assert.Contains(novaSharp.MetadataReferences, reference =>
            reference.Display?.Contains("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotEmpty(novaSharp.AnalyzerReferences);
        Assert.InRange(loaded.RawBuildLog.Count, 1, 2_000);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NovaSharp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the NovaSharp repository root.");
    }

    private sealed class RecordingProgress : IProgress<ProjectLoadStatusSnapshot>
    {
        private readonly Lock _gate = new();
        private readonly List<ProjectLoadStatusSnapshot> _items = [];

        public IReadOnlyList<ProjectLoadStatusSnapshot> Items
        {
            get
            {
                lock (_gate)
                {
                    return [.. _items];
                }
            }
        }

        public void Report(ProjectLoadStatusSnapshot value)
        {
            lock (_gate)
            {
                _items.Add(value);
            }
        }
    }
}
