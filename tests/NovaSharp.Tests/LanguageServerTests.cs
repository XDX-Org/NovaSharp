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
        document.Content = "class C { int Value = \"wrong\"; }";
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
