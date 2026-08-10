using NovaSharp.LanguageServers;
using System.Text.Json;

namespace NovaSharp.Tests;

[TestClass]
public sealed class LanguageServerTests
{
    [TestMethod]
    public void PositionsUseUtf16AndNormalizeCrLf()
    {
        const string text = "a😀\r\nbc";
        Assert.AreEqual(new LspPosition(0, 3), LspConverters.ToPosition(text, 3));
        Assert.AreEqual(new LspPosition(1, 1), LspConverters.ToPosition(text, 6));
        Assert.AreEqual(6, LspConverters.ToOffset(text, new(1, 1)));
    }

    [TestMethod]
    public void ProtocolRangesUseCamelCaseServerFields()
    {
        var range = LspLanguageProvider.ParseRange(JsonSerializer.Deserialize<JsonElement>(
            """{"start":{"line":2,"character":3},"end":{"line":4,"character":5}}"""));

        Assert.AreEqual(new LspPosition(2, 3), range.Start);
        Assert.AreEqual(new LspPosition(4, 5), range.End);
    }

    [TestMethod]
    public void EmptyHoverMarkupIsNotDisplayable()
    {
        var contents = JsonSerializer.Deserialize<JsonElement>("""{"kind":"markdown","value":"  "}""");

        Assert.IsEmpty(LspLanguageProvider.HoverSections(contents));
    }

    [TestMethod]
    public void HoverMarkupStripsEmbeddedDataImages()
    {
        var contents = JsonSerializer.SerializeToElement(new
        {
            kind = "markdown",
            value = "Element details\n\n![Baseline icon](data:image/svg+xml;base64,PHN2Zz4=) Widely available"
        });

        var hover = LspLanguageProvider.HoverSections(contents).Single();
        Assert.DoesNotContain("data:image", hover);
        Assert.DoesNotContain("Baseline icon", hover);
        StringAssert.Contains(hover, "Element details");
        StringAssert.Contains(hover, "Widely available");
    }

    [TestMethod]
    public void CompletionSnippetsExpandPlaceholdersAndPlaceTheCaret()
    {
        var expanded = LspLanguageProvider.ExpandSnippet("Method(${1:value}, ${2|true,false|})$0");

        Assert.AreEqual("Method(value, true)", expanded.Text);
        Assert.AreEqual(expanded.Text.Length, expanded.Caret);
    }

    [TestMethod]
    public async Task LspWorkspaceEditPreservesResourceOperationsAndOrderedRenameText()
    {
        var root = Directory.CreateTempSubdirectory("novasharp-edit-");
        var oldPath = Path.Combine(root.FullName, "old.cs");
        var newPath = Path.Combine(root.FullName, "new.cs");
        await File.WriteAllTextAsync(oldPath, "class Old { }");
        var oldUri = LspConverters.FileUri(oldPath).AbsoluteUri;
        var newUri = LspConverters.FileUri(newPath).AbsoluteUri;
        var protocolEdit = JsonSerializer.SerializeToElement(new
        {
            documentChanges = new object[]
            {
                new { kind = "rename", oldUri, newUri },
                new
                {
                    textDocument = new { uri = newUri, version = (long?)null },
                    edits = new[] { new { range = new { start = new { line = 0, character = 6 },
                        end = new { line = 0, character = 9 } }, newText = "New" } }
                }
            }
        });
        var provider = new LspLanguageProvider(_ => null, _ => null);

        var edit = await provider.WorkspaceEditAsync("rename", protocolEdit, default);

        Assert.IsNotNull(edit);
        Assert.AreEqual("rename", edit.Resources!.Single().Kind);
        Assert.AreEqual("class New { }", edit.Documents.Single().NewText);
        await new WorkspaceEditTransaction().ApplyAsync(edit, []);
        Assert.IsFalse(File.Exists(oldPath));
        Assert.AreEqual("class New { }", await File.ReadAllTextAsync(newPath));
        root.Delete(true);
    }

    [TestMethod]
    public async Task CoordinatorSharesViewsAndOrdersLifecycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"novasharp-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(path, "class C { }");
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        var sink = new Sink();
        await using var coordinator = new LanguageDocumentCoordinator(sink);

        await coordinator.OpenAsync(document);
        await coordinator.OpenAsync(document);
        document.Content = "class Changed { }";
        await coordinator.SavedAsync(document);
        await coordinator.CloseAsync(document);
        await coordinator.CloseAsync(document);

        CollectionAssert.AreEqual(new[] { "textDocument/didOpen", "textDocument/didChange",
            "textDocument/didSave", "textDocument/didClose" }, sink.Methods.ToArray());
        Assert.HasCount(1, sink.Changes);
        var change = sink.Changes[0];
        Assert.IsNotNull(change.Range);
        Assert.AreEqual("hanged", change.Text);
        File.Delete(path);
    }

    [TestMethod]
    public void MissingPackagedAssetsAreExplicitlyUnavailable()
    {
        var catalog = LanguageServerCatalog.Discover(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var server = catalog.ForDocument("a.razor");
        Assert.IsNotNull(server);
        Assert.IsNull(server.Launch);
        StringAssert.Contains(server.UnavailableReason, "missing");
    }

    [TestMethod]
    public async Task PublishedDiagnosticsPreserveMetadataAndProducerLifecycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"novasharp-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(path, "class C { }");
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        var store = new LanguageDiagnosticStore();
        var publisher = new LspDiagnosticPublisher("roslyn", store, candidate =>
            string.Equals(candidate, path, StringComparison.Ordinal) ? document.CreateSnapshot() : null);
        var diagnostic = new LspDiagnostic(new(new(0, 0), new(0, 5)), 2,
            JsonSerializer.SerializeToElement("CS0001"), "csharp", "message", [1],
            [new(new(LspConverters.FileUri(path).AbsoluteUri, new(new(0, 6), new(0, 7))), "related")],
            new("https://example.invalid/CS0001"));

        Assert.IsTrue(publisher.Publish(new(LspConverters.FileUri(path).AbsoluteUri, [diagnostic], 1)));
        var published = store.Entries.Single();
        Assert.AreEqual("roslyn", published.Producer);
        Assert.AreEqual("https://example.invalid/CS0001", published.CodeDescription);
        Assert.HasCount(1, published.RelatedInformation!);
        store.SetProducerStale("roslyn", true);
        Assert.IsTrue(store.Entries.Single().IsStale);
        store.ClearProducer("roslyn");
        Assert.IsEmpty(store.Entries);
        File.Delete(path);
    }

    [TestMethod]
    public async Task PublisherRejectsAResponseForAnOlderEditorVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"novasharp-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(path, "class C { }");
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        var publisher = new LspDiagnosticPublisher("roslyn", new(), _ => document.CreateSnapshot());
        var requestedVersion = document.Version;
        document.Content = "class Changed { }";

        Assert.IsFalse(publisher.Publish(new(LspConverters.FileUri(path).AbsoluteUri, []), requestedVersion));
        File.Delete(path);
    }

    [TestMethod]
    public void UnchangedPullReportRetainsTheIdentifiersPreviousDiagnostics()
    {
        var provider = new LspLanguageProvider(_ => null, _ => null);
        var server = new object();
        var key = (server, "file:///test.cs", "compiler");
        var full = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"full","resultId":"one","items":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":1,"message":"error"}]}""");
        var unchanged = JsonSerializer.Deserialize<JsonElement>("""{"kind":"unchanged","resultId":"one"}""");

        Assert.HasCount(1, provider.ApplyDiagnosticReport(key, full));
        Assert.HasCount(1, provider.ApplyDiagnosticReport(key, unchanged));
    }

    [TestMethod]
    public async Task RazorHtmlBridgeSynchronizesProjectionBeforeForwardingHover()
    {
        var notifications = new List<string>();
        object? forwarded = null;
        var bridge = new RazorHtmlBridge(() => true,
            (method, _, _) => { notifications.Add(method); return Task.CompletedTask; },
            (method, parameters, _) =>
            {
                forwarded = parameters;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                    { contents = new { kind = "markdown", value = "div element" } }));
            });
        var update = JsonSerializer.Deserialize<JsonElement>(
            """{"textDocument":{"uri":"file:///Index.razor"},"checksum":"one","text":"<div></div>"}""");
        var hover = JsonSerializer.Deserialize<JsonElement>(
            """{"textDocument":{"uri":"file:///Index.razor"},"checksum":"one","request":{"textDocument":{"uri":"file:///Index.razor"},"position":{"line":0,"character":2}}}""");

        await bridge.UpdateAsync(update, default);
        var result = await bridge.ForwardAsync("textDocument/hover", hover, default);

        CollectionAssert.AreEqual(new[] { "textDocument/didOpen" }, notifications);
        Assert.IsNotNull(forwarded);
        Assert.AreEqual("div element", result?.GetProperty("contents").GetProperty("value").GetString());
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task PackagedRazorDelegatesHtmlHover()
    {
        var root = Path.Combine(Path.GetTempPath(), $"novasharp-razor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Fixture.csproj");
        var path = Path.Combine(root, "Index.razor");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(path, "<div>Content</div>");
        var catalog = LanguageServerCatalog.Discover(root);
        var razorDefinition = catalog.Definitions.Single(item => item.Kind == LanguageServerKind.RoslynRazor);
        var htmlDefinition = catalog.Definitions.Single(item => item.Kind == LanguageServerKind.Html);
        if (razorDefinition.Launch is null || htmlDefinition.Launch is null) { Directory.Delete(root, true); return; }
        var razor = new LanguageServerManager(razorDefinition, root);
        var html = new LanguageServerManager(htmlDefinition, root);
        razor.SetRazorHtmlBridge(new(() => html.IsReady, html.NotifyAsync,
            (method, parameters, token) => html.RequestAsync<JsonElement>(method, parameters, token)));
        var launch = System.Diagnostics.Stopwatch.StartNew();
        await html.StartAsync();
        Assert.IsTrue(launch.Elapsed < TimeSpan.FromSeconds(5), $"HTML initialization took {launch.Elapsed}.");
        launch.Restart();
        await razor.StartAsync();
        Assert.IsTrue(launch.Elapsed < TimeSpan.FromSeconds(5), $"Razor initialization took {launch.Elapsed}.");
        Assert.IsTrue(razor.WorkingSet + html.WorkingSet < 1536L * 1024 * 1024,
            $"Language servers use {(razor.WorkingSet + html.WorkingSet) / 1024 / 1024:N0} MiB.");
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        await using var coordinator = new LanguageDocumentCoordinator(razor);
        await coordinator.OpenAsync(document);
        var provider = new LspLanguageProvider(_ => razor, _ => document.CreateSnapshot(),
            synchronize: (_, token) => coordinator.SynchronizeAsync(path, token));
        HoverResult? hover = null;
        for (var attempt = 0; attempt < 20 && hover is null; attempt++)
        {
            await Task.Delay(250);
            hover = (await provider.GetHoverAsync(new(path, null, document.Version, 2), default)).Value;
        }

        Assert.IsNotNull(hover, $"Razor: {razor.Status.Detail}; HTML: {html.Status.Detail}");
        Assert.IsTrue(hover.Sections.Any(section => section.Contains("div", StringComparison.OrdinalIgnoreCase)));
        var completions = (await provider.GetCompletionsAsync(
            new(path, null, document.Version, 1), true, default)).Value;
        Assert.IsNotNull(completions);
        Assert.IsTrue(completions.Items.Any(item => item.DisplayText.Contains("div", StringComparison.OrdinalIgnoreCase)));
        await razor.DisposeAsync();
        await html.DisposeAsync();
        Directory.Delete(root, true);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task PackagedHtmlAndCssServersProvideNegotiatedFeatures()
    {
        var root = Directory.CreateTempSubdirectory("novasharp-web-lsp-");
        var catalog = LanguageServerCatalog.Discover(root.FullName);
        foreach (var fixture in new[]
        {
            (Kind: LanguageServerKind.Html, Name: "index.html", Text: "<div title=\"x\">Text</div>", Position: 2),
            (Kind: LanguageServerKind.Css, Name: "site.css", Text: "a { color: red; }", Position: 5)
        })
        {
            var definition = catalog.Definitions.Single(item => item.Kind == fixture.Kind);
            if (definition.Launch is null) continue;
            var path = Path.Combine(root.FullName, fixture.Name);
            await File.WriteAllTextAsync(path, fixture.Text);
            await using var manager = new LanguageServerManager(definition, root.FullName);
            await manager.StartAsync();
            Assert.IsTrue(manager.IsReady, manager.Status.Detail);
            using var document = new EditorDocumentState();
            await document.OpenAsync(path);
            await using var coordinator = new LanguageDocumentCoordinator(manager);
            await coordinator.OpenAsync(document);
            var provider = new LspLanguageProvider(_ => manager, _ => document.CreateSnapshot(),
                synchronize: (_, token) => coordinator.SynchronizeAsync(path, token));
            var info = provider.GetInfo(path);
            Assert.IsTrue(info.Capabilities.HasFlag(LanguageCapabilities.Completion));
            Assert.IsTrue(info.Capabilities.HasFlag(LanguageCapabilities.Hover));
            Assert.IsTrue(info.Capabilities.HasFlag(LanguageCapabilities.Formatting));
            document.Content = fixture.Kind == LanguageServerKind.Html ? "<" : "a { ";
            var completions = (await provider.GetCompletionsAsync(
                new(path, null, document.Version, document.Content.Length), true, default)).Value;
            Assert.IsNotNull(completions);
            Assert.IsNotEmpty(completions.Items);
            document.Content = fixture.Text;
            await coordinator.SynchronizeAsync(path);
            var hover = (await provider.GetHoverAsync(new(path, null, document.Version, fixture.Position), default)).Value;
            Assert.IsNotNull(hover);
            var formatted = (await provider.FormatAsync(new(path, null, document.Version, 0), default)).Value;
            Assert.IsNotNull(formatted);
            Assert.Contains(fixture.Kind == LanguageServerKind.Html ? "div" : "color", formatted.Text);
            if (fixture.Kind == LanguageServerKind.Html && info.Capabilities.HasFlag(LanguageCapabilities.Rename))
            {
                var rename = await provider.RenameAsync(new(path, null, document.Version, 2), "section", default);
                Assert.IsNotNull(rename);
                Assert.Contains("<section", rename.Documents.Single().NewText);
                Assert.Contains("</section>", rename.Documents.Single().NewText);
            }
        }
        root.Delete(true);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task PackagedRoslynSurvivesRapidDocumentChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"novasharp-lsp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Fixture.csproj");
        var path = Path.Combine(root, "Program.cs");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(path, "class C { }");
        var definition = LanguageServerCatalog.Discover(root).ForDocument(path);
        if (definition?.Launch is null) { Directory.Delete(root, true); return; }
        var manager = new LanguageServerManager(definition, root);
        await manager.StartAsync();
        Assert.AreEqual(LanguageServerState.Ready, manager.Status.State, manager.Status.Detail);
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        var coordinator = new LanguageDocumentCoordinator(manager);
        await coordinator.OpenAsync(document);
        for (var index = 0; index < 50; index++)
        {
            document.Content = $"class C {{ int Value{index}; }}";
            if (index % 10 == 0)
                try
                {
                    _ = await manager.RequestAsync<JsonElement>("textDocument/hover", new
                    {
                        textDocument = new { uri = LspConverters.FileUri(path).AbsoluteUri },
                        position = new LspPosition(0, 6)
                    });
                }
                catch (OperationCanceledException) { }
                catch (StreamJsonRpc.ConnectionLostException) { break; }
        }
        document.Content = "class C { int Value = ; }";
        await document.SaveAsync();
        await coordinator.SavedAsync(document);
        var provider = new LspLanguageProvider(_ => manager, candidate =>
            string.Equals(candidate, path, StringComparison.Ordinal) ? document.CreateSnapshot() : null,
            synchronize: (_, token) => coordinator.SynchronizeAsync(path, token));
        IReadOnlyList<LanguageDiagnostic> diagnostics = [];
        JsonElement compilerDiagnostics = default;
        for (var attempt = 0; attempt < 20 && !diagnostics.Any(item => item.Severity == LanguageDiagnosticSeverity.Error); attempt++)
        {
            await Task.Delay(250);
            try
            {
                diagnostics = (await provider.GetDiagnosticsAsync(new(path, null, document.Version,
                    document.Content!.Length), default)).Value ?? [];
                compilerDiagnostics = await manager.RequestAsync<JsonElement>("textDocument/diagnostic", new
                {
                    textDocument = new { uri = LspConverters.FileUri(path).AbsoluteUri },
                    identifier = "DocumentCompilerSemantic"
                });
            }
            catch (OperationCanceledException) { }
        }
        Assert.AreEqual(LanguageServerState.Ready, manager.Status.State,
            $"Capabilities: {manager.Capabilities.GetRawText()}{Environment.NewLine}{manager.LastCrash}");
        Assert.IsTrue(diagnostics.Any(item => item.Severity == LanguageDiagnosticSeverity.Error),
            $"Registrations: {string.Join(" | ", manager.Registrations("textDocument/diagnostic").Select(item => item.RegisterOptions?.GetRawText()))}. "
            + $"Compiler: {compilerDiagnostics.GetRawText()}. Diagnostics: {string.Join(" | ", diagnostics.Select(item => $"{item.Id}: {item.Message}"))}. Capabilities: {manager.Capabilities.GetRawText()}");
        var semantic = (await provider.GetSemanticSpansAsync(new(path, null, document.Version,
            document.Content.Length), default)).Value ?? [];
        Assert.IsTrue(semantic.Any(span => span.Classification != "text"),
            $"Semantic registrations: {string.Join(" | ", manager.Registrations("textDocument/semanticTokens").Select(item => item.RegisterOptions?.GetRawText()))}");
        document.Content = "class Restarted { int Broken = ; }";
        await coordinator.SynchronizeAsync(path);
        await manager.RestartAsync();
        await coordinator.ReplayAsync();
        diagnostics = [];
        for (var attempt = 0; attempt < 20 && !diagnostics.Any(item => item.Severity == LanguageDiagnosticSeverity.Error); attempt++)
        {
            await Task.Delay(250);
            diagnostics = (await provider.GetDiagnosticsAsync(new(path, null, document.Version,
                document.Content.Length), default)).Value ?? [];
        }
        Assert.IsTrue(diagnostics.Any(item => item.Severity == LanguageDiagnosticSeverity.Error),
            "Dirty in-memory text was not reanalyzed after the Roslyn restart.");
        await coordinator.DisposeAsync();
        await manager.DisposeAsync();
        Directory.Delete(root, true);
    }

    private sealed class Sink : ILspDocumentSink
    {
        internal List<string> Methods { get; } = [];
        internal List<LspTextDocumentContentChangeEvent> Changes { get; } = [];
        public bool IsReady => true;
        public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default)
        {
            Methods.Add(method);
            if (parameters is LspDidChangeTextDocumentParams changed) Changes.AddRange(changed.ContentChanges);
            return Task.CompletedTask;
        }
    }
}
