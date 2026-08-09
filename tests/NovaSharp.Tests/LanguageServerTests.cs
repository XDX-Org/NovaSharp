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

    private sealed class Sink : ILspDocumentSink
    {
        internal List<string> Methods { get; } = [];
        public bool IsReady => true;
        public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default)
        {
            Methods.Add(method);
            return Task.CompletedTask;
        }
    }
}
