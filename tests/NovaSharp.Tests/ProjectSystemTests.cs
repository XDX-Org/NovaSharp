using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;

namespace NovaSharp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectSystemTests
{
    [TestMethod]
    public void DiagnosticStoreReplacesProducerContextVersionAtomically()
    {
        var store = new DiagnosticStore();
        store.Replace(DiagnosticSource.MsBuild, "fixture", 1,
            [(NotificationSeverity.Warning, "first"), (NotificationSeverity.Error, "second")]);
        store.Replace(DiagnosticSource.MsBuild, "fixture", 2, [(NotificationSeverity.Warning, "new")]);

        Assert.HasCount(1, store.Entries);
        Assert.AreEqual(2, store.Entries[0].Version);
        Assert.AreEqual("new", store.Entries[0].Message);
    }

    [TestMethod]
    public async Task EditorSnapshotsAreVersionedForEveryMutation()
    {
        using var fixture = new ProjectFixture();
        using var document = new EditorDocumentState();
        await document.OpenAsync(fixture.SharedFile);
        var snapshots = new List<EditorSnapshot>();
        document.ContentChanged += snapshots.Add;

        document.Content = "public class Dirty { }";
        document.Undo();
        document.Redo();

        Assert.HasCount(3, snapshots);
        Assert.IsTrue(snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.Version < pair.Second.Version));
        Assert.IsTrue(snapshots[^1].IsDirty);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task LoadsEvaluatedProjectsAndSynchronizesLinkedDirtyBuffersAcrossContexts()
    {
        using var fixture = new ProjectFixture();
        await using var projectSystem = new RoslynProjectSystem();

        await projectSystem.OpenAsync(fixture.SolutionFile);

        Assert.IsFalse(projectSystem.State.IsLoading);
        Assert.IsTrue(projectSystem.State.ProjectCount >= 3);
        Assert.IsTrue(projectSystem.State.DocumentCount >= 3);
        Assert.IsTrue(projectSystem.Contexts.Any(context => context.TargetFramework == "net9.0" && context.Configuration == "Debug"));
        Assert.IsTrue(projectSystem.Contexts.Any(context => context.TargetFramework == "net10.0" && context.Configuration == "Debug"));
        var app = projectSystem.CurrentSolution!.Projects.First(project => project.Name.StartsWith("App", StringComparison.Ordinal)
            && project.CompilationOptions?.NullableContextOptions == NullableContextOptions.Enable);
        Assert.IsTrue(app.ProjectReferences.Any());
        Assert.IsTrue(((CSharpParseOptions)app.ParseOptions!).PreprocessorSymbolNames.Contains("PHASE6"));
        Assert.AreEqual(NullableContextOptions.Enable, app.CompilationOptions!.NullableContextOptions);
        Assert.IsTrue(app.MetadataReferences.Any());
        Assert.IsTrue(projectSystem.CurrentSolution.Projects.Any(project => project.AnalyzerReferences.Any()));

        var contexts = projectSystem.GetContexts(fixture.SharedFile);
        Assert.IsTrue(contexts.Count >= 2, "The linked file must retain every project context.");
        using var document = new EditorDocumentState();
        await document.OpenAsync(fixture.SharedFile);
        projectSystem.Track(document);
        document.Content = "public class Shared { public int Dirty => 6; }";

        await WaitUntilAsync(async () =>
        {
            var texts = await Task.WhenAll(contexts.Select(async context =>
            {
                projectSystem.SelectContext(fixture.SharedFile, context.Id);
                return (await projectSystem.GetActiveDocument(fixture.SharedFile)!.GetTextAsync()).ToString();
            }));
            return texts.All(text => text.Contains("Dirty", StringComparison.Ordinal));
        });
        Assert.AreEqual("public class Shared { }", await File.ReadAllTextAsync(fixture.SharedFile));
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task ReloadRebuildsMappingsAndReappliesUnsavedEditorSnapshot()
    {
        using var fixture = new ProjectFixture();
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.SolutionFile);
        using var document = new EditorDocumentState();
        await document.OpenAsync(fixture.SharedFile);
        projectSystem.Track(document);
        document.Content = "public class Shared { public string Buffer => \"kept\"; }";

        await projectSystem.ReloadAsync();
        await WaitUntilAsync(async () => (await projectSystem.GetActiveDocument(fixture.SharedFile)!.GetTextAsync())
            .ToString().Contains("Buffer", StringComparison.Ordinal));

        Assert.IsTrue(document.IsDirty);
        Assert.IsTrue(projectSystem.GetContexts(fixture.SharedFile).Count >= 2);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task NamedFiveHundredDocumentFixtureMeetsSolutionLoadBudget()
    {
        using var fixture = new PerformanceFixture();
        await using var projectSystem = new RoslynProjectSystem();
        var stopwatch = Stopwatch.StartNew();

        await projectSystem.OpenAsync(fixture.SolutionFile);

        stopwatch.Stop();
        Assert.IsTrue(projectSystem.State.ProjectCount >= 12);
        Assert.IsTrue(projectSystem.State.DocumentCount >= 500);
        if (OperatingSystem.IsLinux())
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"Phase 6 fixture load took {stopwatch.Elapsed}.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(25, timeout.Token);
    }

    private sealed class ProjectFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.ProjectSystem.Tests", Guid.NewGuid().ToString("N"));
        internal string SharedFile => Path.Combine(Root, "Shared.cs");
        internal string SolutionFile => Path.Combine(Root, "Fixture.slnx");

        internal ProjectFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "Lib"));
            Directory.CreateDirectory(Path.Combine(Root, "App"));
            File.WriteAllText(SharedFile, "public class Shared { }");
            File.WriteAllText(Path.Combine(Root, "Lib", "Lib.csproj"), Project("Microsoft.NET.Sdk", "net10.0"));
            File.WriteAllText(Path.Combine(Root, "Lib", "Lib.cs"), "public class LibType { }");
            File.WriteAllText(Path.Combine(Root, "App", "App.csproj"),
                Project("Microsoft.NET.Sdk", "net9.0;net10.0", "<ProjectReference Include=\"../Lib/Lib.csproj\" />", multiTarget: true));
            File.WriteAllText(Path.Combine(Root, "App", "Program.cs"), "public class Program { }");
            Directory.CreateDirectory(Path.Combine(Root, "Web"));
            File.WriteAllText(Path.Combine(Root, "Web", "Web.csproj"), Project("Microsoft.NET.Sdk.Web", "net10.0"));
            File.WriteAllText(Path.Combine(Root, "Web", "Program.cs"), "public class WebProgram { }");
            File.WriteAllText(Path.Combine(Root, "Web", "Component.razor"), "<h1>Fixture</h1>");
            File.WriteAllText(SolutionFile,
                "<Solution><Project Path=\"Lib/Lib.csproj\" /><Project Path=\"App/App.csproj\" /><Project Path=\"Web/Web.csproj\" /></Solution>");
        }

        private static string Project(string sdk, string framework, string reference = "", bool multiTarget = false) =>
            $"""
            <Project Sdk="{sdk}">
              <PropertyGroup>
                <{(multiTarget ? "TargetFrameworks" : "TargetFramework")}>{framework}</{(multiTarget ? "TargetFrameworks" : "TargetFramework")}>
                <Nullable>enable</Nullable>
                <DefineConstants>PHASE6</DefineConstants>
                {(multiTarget ? "<OutputType>Exe</OutputType>" : "")}
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="../Shared.cs" Link="Shared.cs" />
                {reference}
              </ItemGroup>
            </Project>
            """;

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class PerformanceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.ProjectSystem.Performance", Guid.NewGuid().ToString("N"));
        internal string SolutionFile => Path.Combine(Root, "Performance.slnx");

        internal PerformanceFixture()
        {
            Directory.CreateDirectory(Root);
            var solution = new System.Text.StringBuilder("<Solution>");
            for (var projectIndex = 0; projectIndex < 8; projectIndex++)
            {
                var name = $"Project{projectIndex}";
                var directory = Path.Combine(Root, name);
                Directory.CreateDirectory(directory);
                var frameworks = projectIndex < 4
                    ? "<TargetFrameworks>net9.0;net10.0</TargetFrameworks>"
                    : "<TargetFramework>net10.0</TargetFramework>";
                File.WriteAllText(Path.Combine(directory, $"{name}.csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{frameworks}</PropertyGroup></Project>");
                for (var document = 0; document < 63; document++)
                    File.WriteAllText(Path.Combine(directory, $"Document{document:D2}.cs"),
                        $"namespace {name}; public class Document{document:D2} {{ }}");
                solution.Append($"<Project Path=\"{name}/{name}.csproj\" />");
            }
            solution.Append("</Solution>");
            File.WriteAllText(SolutionFile, solution.ToString());
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
