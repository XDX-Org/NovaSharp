#if DEBUG
using System.Diagnostics;

namespace NovaSharp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LanguageFeaturesTests
{
    [TestMethod]
    [Timeout(30000)]
    public async Task CompletionUsesProjectContextAndUnsavedVersion()
    {
        using var fixture = new LanguageFixture();
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.SolutionFile);
        using var editor = await fixture.OpenEditorAsync();
        projectSystem.Track(editor);
        editor.Content = "using Fixture; class C { ProjectType Value = new Pro }";
        var provider = new CSharpLanguageProvider(projectSystem);
        var position = editor.Content!.IndexOf("Pro }", StringComparison.Ordinal) + 3;

        var response = await provider.GetCompletionsAsync(Request(projectSystem, editor, position), true, default);

        Assert.IsFalse(response.IsDegraded);
        var item = response.Value!.Items.Single(item => item.DisplayText == "ProjectType");
        Assert.IsFalse(response.Value.Items.Any(candidate => candidate.DisplayText == "HiddenType"));
        var details = await provider.GetCompletionDetailsAsync(Request(projectSystem, editor, position), item.Id, default);
        var edit = await provider.GetCompletionEditAsync(Request(projectSystem, editor, position), item.Id, null, default);
        Assert.IsFalse(string.IsNullOrWhiteSpace(details.Value?.Detail));
        Assert.AreEqual("ProjectType", edit.Value?.NewText);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task SignatureHoverSemanticAndFormattingReturnVersionedResults()
    {
        using var fixture = new LanguageFixture();
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.SolutionFile);
        using var editor = await fixture.OpenEditorAsync();
        projectSystem.Track(editor);
        editor.Content = "class C{string M(){return string.Concat(\"a\", );}}";
        var provider = new CSharpLanguageProvider(projectSystem);
        var signaturePosition = editor.Content!.IndexOf(", )", StringComparison.Ordinal) + 2;

        var signature = await provider.GetSignatureHelpAsync(Request(projectSystem, editor, signaturePosition), default);
        var hover = await provider.GetHoverAsync(Request(projectSystem, editor,
            editor.Content.IndexOf("string.Concat", StringComparison.Ordinal)), default);
        var semantic = await provider.GetSemanticSpansAsync(Request(projectSystem, editor, editor.Content.Length), default);
        var formatted = await provider.FormatAsync(Request(projectSystem, editor, editor.Content.Length), default);

        Assert.AreEqual(editor.Version, signature.SourceVersion);
        Assert.IsTrue(signature.Value!.Signatures.Count > 1);
        Assert.AreEqual(1, signature.Value.ActiveParameter);
        Assert.IsTrue(hover.Value!.Sections.Any(section => section.Contains("string", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(hover.Value.Sections.Any(section => section.Contains("Project: App", StringComparison.Ordinal)));
        Assert.IsTrue(semantic.Value!.Any(span => span.Classification.Contains("class", StringComparison.OrdinalIgnoreCase)));
        Assert.StartsWith("class C {", formatted.Value!.Text);
        Assert.AreNotEqual(editor.Content, formatted.Value.Text);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task SignatureHelpTracksTheSelectedOverload()
    {
        using var fixture = new LanguageFixture();
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.SolutionFile);
        using var editor = await fixture.OpenEditorAsync();
        projectSystem.Track(editor);
        var provider = new CSharpLanguageProvider(projectSystem);

        editor.Content = "class C { string M() => string.Concat(\"a\", \"b\"); }";
        var strings = await provider.GetSignatureHelpAsync(Request(projectSystem, editor,
            editor.Content.IndexOf(",", StringComparison.Ordinal) + 2), default);
        editor.Content = "class C { string M() => string.Concat(\"a\", 1); }";
        var objects = await provider.GetSignatureHelpAsync(Request(projectSystem, editor,
            editor.Content.IndexOf(",", StringComparison.Ordinal) + 2), default);

        StringAssert.Contains(strings.Value!.Signatures[strings.Value.ActiveSignature], "string?");
        StringAssert.Contains(objects.Value!.Signatures[objects.Value.ActiveSignature], "object?");
        Assert.AreNotEqual(strings.Value.Signatures[strings.Value.ActiveSignature],
            objects.Value.Signatures[objects.Value.ActiveSignature]);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task CompletionDetailsPreserveNullableProjectContext()
    {
        using var fixture = new LanguageFixture();
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.SolutionFile);
        using var editor = await fixture.OpenEditorAsync();
        projectSystem.Track(editor);
        editor.Content = "using Fixture; class C { void M(ProjectType value) { value.Null } }";
        var provider = new CSharpLanguageProvider(projectSystem);
        var position = editor.Content.IndexOf("Null }", StringComparison.Ordinal) + 4;

        var completion = await provider.GetCompletionsAsync(Request(projectSystem, editor, position), true, default);
        var item = completion.Value!.Items.Single(candidate => candidate.DisplayText == "NullableProperty");
        var details = await provider.GetCompletionDetailsAsync(Request(projectSystem, editor, position), item.Id, default);

        StringAssert.Contains(details.Value!.Detail, "string?");
    }

    [TestMethod]
    public async Task LatestRequestCancelsAndDiscardsOutOfOrderResponses()
    {
        using var latest = new LatestLanguageRequest();
        var first = latest.RunAsync(async token =>
        {
            await Task.Delay(500, token);
            return new LanguageResponse<string>(1, "stale");
        }, 1);
        var second = latest.RunAsync(async token =>
        {
            await Task.Yield();
            return new LanguageResponse<string>(2, "current");
        }, 2);

        Assert.IsNull(await first);
        Assert.AreEqual("current", await second);
        Assert.IsNull(await latest.RunAsync<string>(_ => throw new InvalidOperationException("provider failed"), 3));
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task DiagnosticsUseUnsavedDocumentVersionAndExactRange()
    {
        using var fixture = new LanguageFixture();
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.SolutionFile);
        using var editor = await fixture.OpenEditorAsync();
        projectSystem.Track(editor);
        editor.Content = "class C { void M() { int value = missing; } }";
        var provider = new CSharpLanguageProvider(projectSystem);

        var response = await provider.GetDiagnosticsAsync(Request(projectSystem, editor, editor.Content.Length), default);

        var diagnostics = response.Value!;
        var diagnostic = diagnostics.Single(item => item.Id == "CS0103");
        Assert.AreEqual(editor.Version, response.SourceVersion);
        Assert.AreEqual(LanguageDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(editor.Content.IndexOf("missing", StringComparison.Ordinal), diagnostic.Range.Start);
        Assert.AreEqual("missing".Length, diagnostic.Range.Length);
        Assert.AreEqual(diagnostics.Count, provider.Diagnostics.Entries.Count);
    }

    [TestMethod]
    public void DiagnosticStoreKeepsProducersSeparateAndRejectsStaleResults()
    {
        var store = new LanguageDiagnosticStore();
        var path = Path.Combine(Path.GetTempPath(), "diagnostics.cs");
        var compiler = new LanguageDiagnostic("CS1", LanguageDiagnosticSource.Compiler,
            LanguageDiagnosticSeverity.Error, "compiler", path, new(0, 1), 0, 0, "App");
        var analyzer = compiler with { Id = "AN1", Source = LanguageDiagnosticSource.Analyzer, Message = "analyzer" };

        Assert.IsTrue(store.Replace(path, 2, LanguageDiagnosticSource.Compiler, [compiler]));
        Assert.IsTrue(store.Replace(path, 2, LanguageDiagnosticSource.Analyzer, [analyzer]));
        Assert.IsFalse(store.Replace(path, 1, LanguageDiagnosticSource.Compiler, []));
        CollectionAssert.AreEquivalent(new[] { "CS1", "AN1" }, store.Entries.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task MediumFixtureMeetsLanguageLatencyBudgets()
    {
        using var fixture = new LanguageFixture(200);
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.SolutionFile);
        using var editor = await fixture.OpenEditorAsync();
        projectSystem.Track(editor);
        var provider = new CSharpLanguageProvider(projectSystem);
        editor.Content = "using System; class Program { void M() { Console.WriteLine(string.Concat(\"a\", )); } }";
        var completionRequest = Request(projectSystem, editor,
            editor.Content.IndexOf("Console.", StringComparison.Ordinal) + "Console.".Length);
        var stopwatch = Stopwatch.StartNew();

        await provider.GetCompletionsAsync(completionRequest, true, default);
        var completion = stopwatch.Elapsed;
        stopwatch.Restart();
        await provider.GetHoverAsync(Request(projectSystem, editor,
            editor.Content.IndexOf("Console", StringComparison.Ordinal)), default);
        var hover = stopwatch.Elapsed;
        stopwatch.Restart();
        await provider.GetSignatureHelpAsync(Request(projectSystem, editor,
            editor.Content.IndexOf(", )", StringComparison.Ordinal) + 2), default);
        var signature = stopwatch.Elapsed;
        stopwatch.Restart();
        await provider.GetSemanticSpansAsync(Request(projectSystem, editor, editor.Content.Length), default);

        if (OperatingSystem.IsLinux())
        {
            Assert.IsTrue(completion < TimeSpan.FromSeconds(2), $"Completion took {completion}.");
            Assert.IsTrue(hover < TimeSpan.FromSeconds(1), $"Hover took {hover}.");
            Assert.IsTrue(signature < TimeSpan.FromSeconds(1), $"Signature help took {signature}.");
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Semantic classification took {stopwatch.Elapsed}.");
        }
    }

    private static LanguageRequest Request(RoslynProjectSystem projectSystem, EditorDocumentState editor, int position) =>
        new(editor.FilePath!, projectSystem.GetActiveDocument(editor.FilePath!)?.Project.Id.Id.ToString(), editor.Version, position);

    private sealed class LanguageFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.Language.Tests", Guid.NewGuid().ToString("N"));
        internal string SolutionFile => Path.Combine(Root, "Fixture.slnx");
        private string AppFile => Path.Combine(Root, "App", "Program.cs");

        internal LanguageFixture(int extraDocuments = 0)
        {
            Directory.CreateDirectory(Path.Combine(Root, "Lib"));
            Directory.CreateDirectory(Path.Combine(Root, "App"));
            File.WriteAllText(Path.Combine(Root, "Lib", "Lib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(Root, "Lib", "Types.cs"),
                "namespace Fixture; public class ProjectType { public string? NullableProperty { get; } } internal class HiddenType { }");
            File.WriteAllText(Path.Combine(Root, "App", "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(AppFile, "using System; class Program { static void Main() { Console. } }");
            for (var i = 0; i < extraDocuments; i++)
                File.WriteAllText(Path.Combine(Root, "App", $"Type{i:D3}.cs"), $"internal class Type{i:D3} {{ }}");
            File.WriteAllText(SolutionFile,
                "<Solution><Project Path=\"Lib/Lib.csproj\" /><Project Path=\"App/App.csproj\" /></Solution>");
        }

        internal async Task<EditorDocumentState> OpenEditorAsync()
        {
            var editor = new EditorDocumentState();
            await editor.OpenAsync(AppFile);
            return editor;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
#endif
