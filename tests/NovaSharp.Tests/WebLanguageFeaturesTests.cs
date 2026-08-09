namespace NovaSharp.Tests;

[TestClass]
public sealed class WebLanguageFeaturesTests
{
    [TestMethod]
    public void RazorProjectionMapsOnlyMatchingVersionsAndSegments()
    {
        const string source = "<h1>Hello</h1>\n<style>.x { color: red; }</style>\n<style>.y { color: blue; }</style>\n@code { int value = 1; }";
        var projection = WebProjectionParser.Parse("Index.razor", source, 7);
        var css = source.IndexOf(".x", StringComparison.Ordinal);
        var csharp = source.IndexOf("int value", StringComparison.Ordinal);

        Assert.IsTrue(projection.TryMapToProjected(7, WebProjectionKind.Css, css, out var cssPosition));
        Assert.IsTrue(projection.TryMapToHost(7, WebProjectionKind.Css, new(cssPosition, 2), out var cssHost));
        Assert.AreEqual(new TextRange(css, 2), cssHost);
        var firstCss = projection.Segments.First(item => item.Kind == WebProjectionKind.Css);
        Assert.IsFalse(projection.TryMapToHost(7, WebProjectionKind.Css,
            new(firstCss.ProjectedStart + firstCss.Length - 1, 2), out _));
        Assert.IsTrue(projection.TryMapToProjected(7, WebProjectionKind.CSharp, csharp, out _));
        Assert.IsFalse(projection.TryMapToProjected(8, WebProjectionKind.Css, css, out _));
        Assert.IsFalse(projection.TryMapToHost(8, WebProjectionKind.Css, new(cssPosition, 2), out _));
    }

    [TestMethod]
    public async Task HtmlProvidesCompletionHoverDiagnosticsFormattingSymbolsAndRename()
    {
        await using var fixture = await WebFixture.CreateAsync("Index.html", "<div class=\"hero\">\n<span>Text</div>");
        var provider = fixture.Provider;
        var request = fixture.Request(fixture.Document.Content!.IndexOf("div", StringComparison.Ordinal) + 2);

        var completion = await provider.GetCompletionsAsync(fixture.Request(1), true, default);
        Assert.IsTrue(completion.Value!.Items.Any(item => item.DisplayText == "div"));
        Assert.IsNotNull((await provider.GetHoverAsync(request, default)).Value);
        Assert.IsTrue((await provider.GetDiagnosticsAsync(request, default)).Value!.Count > 0);
        Assert.IsTrue((await provider.GetSemanticSpansAsync(request, default)).Value!.Count > 0);
        Assert.IsTrue((await provider.GetDocumentSymbolsAsync(request, default)).Count >= 2);
        Assert.Contains("    <span>", (await provider.FormatAsync(request, default)).Value!.Text);
        var rename = await provider.RenameAsync(request, "section", default);
        Assert.Contains("<section", rename!.Documents.Single().NewText);
        Assert.Contains("</section>", rename.Documents.Single().NewText);
    }

    [TestMethod]
    public async Task RazorCompletionDiscoversComponentsAndRejectsStaleSnapshots()
    {
        await using var fixture = await WebFixture.CreateAsync("Index.razor", "<MyCard />", ("MyCard.razor", "<article>@Title</article>"));
        var completion = await fixture.Provider.GetCompletionsAsync(fixture.Request(4), true, default);
        Assert.IsTrue(completion.Value!.Items.Any(item => item.DisplayText == "MyCard" && item.Kind == "Component"));

        var stale = fixture.Request(4) with { Version = fixture.Document.Version - 1 };
        Assert.IsTrue((await fixture.Provider.GetCompletionsAsync(stale, true, default)).IsDegraded);
        var definitions = await fixture.Provider.GetDefinitionsAsync(fixture.Request(4), false, default);
        Assert.AreEqual("MyCard.razor", Path.GetFileName(definitions.Single().DocumentPath));
    }

    [TestMethod]
    public async Task RazorDiagnosticsIgnoreGenericTypesInsideCodeBlocks()
    {
        await using var fixture = await WebFixture.CreateAsync("CodeEditor.razor",
            "<div>Editor</div>\n@code { private IReadOnlyList<NavigationTarget> targets = []; public Phase15SmokeResult? Result { get; set; } }");

        var diagnostics = await fixture.Provider.GetDiagnosticsAsync(
            fixture.Request(fixture.Document.Content!.Length), default);

        Assert.IsFalse(diagnostics.Value!.Any(item => item.Id is "WEB001" or "WEB002"));
    }

    [TestMethod]
    public async Task RegistrySelectsCapabilitiesWithoutEditorTypeKnowledge()
    {
        await using var system = new RoslynProjectSystem();
        var registry = new LanguageProviderRegistry(system);
        Assert.AreEqual("csharp", registry.GetInfo("Program.cs").LanguageId);
        Assert.AreEqual("razor", registry.GetInfo("Index.razor").LanguageId);
        Assert.AreEqual("html", registry.GetInfo("Index.html").LanguageId);
        Assert.AreEqual("css", registry.GetInfo("site.css").LanguageId);
        Assert.IsTrue(registry.GetInfo("site.css").Capabilities.HasFlag(LanguageCapabilities.Formatting));
        Assert.IsFalse(registry.GetInfo("site.css").Capabilities.HasFlag(LanguageCapabilities.Rename));
        Assert.IsFalse(registry.GetInfo("notes.lua").IsAvailable);
    }

    [TestMethod]
    public async Task RazorPageAndComponentLibraryFixturesProvideAdvertisedFeatures()
    {
        await using var page = await WebFixture.CreateAsync("Pages/Index.cshtml",
            "@page\n@using Fixture.Components\n<MyCard><span>Text</span></MyCard>",
            ("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Razor\" />"),
            ("Components/MyCard.razor", "<article>@Title</article>"));
        var directive = await page.Provider.GetCompletionsAsync(page.Request(1), true, default);
        Assert.IsTrue(directive.Value!.Items.Any(item => item.DisplayText == "page"));
        Assert.IsTrue((await page.Provider.GetSemanticSpansAsync(page.Request(1), default)).Value!
            .Any(span => span.Classification == "keyword"));
        Assert.IsTrue((await page.Provider.GetDocumentSymbolsAsync(page.Request(1), default)).Count >= 2);
        var card = page.Document.Content!.IndexOf("MyCard", StringComparison.Ordinal) + 2;
        Assert.EndsWith("MyCard.razor",
            (await page.Provider.GetDefinitionsAsync(page.Request(card), false, default)).Single().DocumentPath);

        await using var component = await WebFixture.CreateAsync("App/Index.razor", "<LibCard />",
            ("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Razor\" />"),
            ("Library/LibCard.razor", "<section>Library</section>"),
            ("Library/LibraryModel.cs", "public sealed class LibraryModel { }"));
        var completion = await component.Provider.GetCompletionsAsync(component.Request(4), true, default);
        Assert.IsTrue(completion.Value!.Items.Any(item => item.DisplayText == "LibCard"));
    }

    [TestMethod]
    public async Task StandaloneCssProvidesEveryAdvertisedCapability()
    {
        await using var fixture = await WebFixture.CreateAsync("wwwroot/site.css", ".hero { color: red; }");
        var position = fixture.Document.Content!.IndexOf("color", StringComparison.Ordinal) + 2;
        var completion = await fixture.Provider.GetCompletionsAsync(fixture.Request(position), true, default);
        Assert.IsTrue(completion.Value!.Items.Any(item => item.DisplayText == "color"));
        Assert.IsNotNull((await fixture.Provider.GetHoverAsync(fixture.Request(position), default)).Value);
        Assert.IsTrue((await fixture.Provider.GetSemanticSpansAsync(fixture.Request(position), default)).Value!.Count > 0);
        Assert.IsNotNull((await fixture.Provider.FormatAsync(fixture.Request(position), default)).Value);
        fixture.Document.Content = ".hero { color: red;";
        Assert.IsTrue((await fixture.Provider.GetDiagnosticsAsync(fixture.Request(position), default)).Value!
            .Any(item => item.Id == "CSS002"));
    }

    [TestMethod]
    public async Task RapidWebEditsRejectStaleRangesAndKeepCurrentRangesInBounds()
    {
        await using var fixture = await WebFixture.CreateAsync("Index.razor", "<MyCard />",
            ("MyCard.razor", "<article>Card</article>"));
        var stale = fixture.Request(4);
        fixture.Document.Content = "<div><span></div>";

        Assert.IsTrue((await fixture.Provider.GetDiagnosticsAsync(stale, default)).IsDegraded);
        Assert.IsEmpty(await fixture.Provider.GetDefinitionsAsync(stale, false, default));
        var current = await fixture.Provider.GetDiagnosticsAsync(fixture.Request(fixture.Document.Content.Length), default);
        Assert.IsTrue(current.Value!.All(item => item.Range.Start >= 0
            && item.Range.Start + item.Range.Length <= fixture.Document.Content.Length));
    }

    [TestMethod]
    public async Task DiscoveryAndRetainedStateStayWithinPhase15Bounds()
    {
        var files = Enumerable.Range(0, 250)
            .Select(index => ($"Types/Type{index:D3}.cs", $"public sealed class Type{index:D3} {{ }}")).ToArray();
        await using var fixture = await WebFixture.CreateAsync("Index.razor", "@code { public T value; }", files);
        var position = fixture.Document.Content!.IndexOf('T') + 1;
        var completion = await fixture.Provider.GetCompletionsAsync(fixture.Request(position), true, default);

        Assert.IsTrue(completion.Value!.Items.Count <= 200);
        Assert.IsTrue(fixture.Provider.RetainedCompletionCount <= 200);
        Assert.IsTrue(fixture.Provider.RetainedWebCompletionBytes <= 64 * 1024,
            $"Retained web completion state was {fixture.Provider.RetainedWebCompletionBytes} bytes.");
        Assert.AreEqual(0, fixture.Provider.RetainedProjectionCount);
    }

    [TestMethod]
    public async Task RazorProjectedCSharpAndCssAreVersionedAndRecoverAfterRestart()
    {
        await using var fixture = await WebFixture.CreateAsync("Index.razor",
            "<style>.hero { color: red; }</style>\n@code { public int Count { get; set; } }");
        var csharp = fixture.Document.Content!.IndexOf("public", StringComparison.Ordinal) + 2;
        var css = fixture.Document.Content.IndexOf("color", StringComparison.Ordinal) + 2;
        var csharpCompletion = await fixture.Provider.GetCompletionsAsync(fixture.Request(csharp), true, default);
        var cssCompletion = await fixture.Provider.GetCompletionsAsync(fixture.Request(css), true, default);
        var semantic = await fixture.Provider.GetSemanticSpansAsync(fixture.Request(csharp), default);

        Assert.IsTrue(csharpCompletion.Value!.Items.Any(item => item.DisplayText == "public"));
        Assert.IsTrue(cssCompletion.Value!.Items.Any(item => item.DisplayText == "color"));
        Assert.IsTrue(semantic.Value!.Any(item => item.Start == fixture.Document.Content.IndexOf("public", StringComparison.Ordinal)));
        var content = fixture.Document.Content;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        fixture.Provider.Restart();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"Restart took {stopwatch.Elapsed}.");
        Assert.AreEqual(content, fixture.Document.Content);
        Assert.AreEqual(0, fixture.Provider.RetainedCompletionCount);
        Assert.AreEqual(0, fixture.Provider.RetainedProjectionCount);
        Assert.IsFalse((await fixture.Provider.GetCompletionsAsync(fixture.Request(csharp), true, default)).IsDegraded);
    }

    [TestMethod]
    public async Task ProjectionAndFirstResultsMeetPhase15Budgets()
    {
        var source = string.Join('\n', Enumerable.Range(0, 2_000)
            .Select(index => $"<div class=\"item-{index}\">@index</div>")) + "\n@code { public int Value { get; set; } }";
        await using var fixture = await WebFixture.CreateAsync("Large.razor", source);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _ = WebProjectionParser.Parse(fixture.Document.FilePath!, source, fixture.Document.Version);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"Projection took {stopwatch.Elapsed}.");
        stopwatch.Restart();
        _ = await fixture.Provider.GetCompletionsAsync(fixture.Request(1), true, default);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"First completion took {stopwatch.Elapsed}.");
        Assert.IsTrue(fixture.Provider.RetainedCompletionCount <= 200);
        Assert.IsTrue(fixture.Provider.RetainedWebCompletionBytes <= 64 * 1024);
        Assert.AreEqual(0, fixture.Provider.RetainedProjectionCount);
        stopwatch.Restart();
        _ = await fixture.Provider.GetSemanticSpansAsync(fixture.Request(source.Length), default);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Semantic result took {stopwatch.Elapsed}.");
    }

    private sealed class WebFixture : IAsyncDisposable
    {
        private readonly string _root;
        internal RoslynProjectSystem System { get; }
        internal LanguageProviderRegistry Provider { get; }
        internal EditorDocumentState Document { get; }
        private WebFixture(string root, RoslynProjectSystem system, LanguageProviderRegistry provider, EditorDocumentState document) =>
            (_root, System, Provider, Document) = (root, system, provider, document);

        internal static async Task<WebFixture> CreateAsync(string name, string content, params (string Name, string Content)[] others)
        {
            var root = Path.Combine(Path.GetTempPath(), "NovaSharp.WebLanguage.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            foreach (var other in others)
            {
                var otherPath = Path.Combine(root, other.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
                await File.WriteAllTextAsync(otherPath, other.Content);
            }
            var document = new EditorDocumentState();
            await document.OpenAsync(path);
            var system = new RoslynProjectSystem();
            system.Track(document);
            return new(root, system, new(system), document);
        }

        internal LanguageRequest Request(int position) => new(Document.FilePath!, null, Document.Version, position);
        public async ValueTask DisposeAsync()
        {
            System.Untrack(Document);
            Document.Dispose();
            await System.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}
