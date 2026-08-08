namespace NovaSharp.Tests;

[TestClass]
public sealed class WorkspaceSearchTests
{
    [TestMethod]
    public async Task SearchStreamsDeterministicBoundedBatchesAndUsesDirtyBuffers()
    {
        using var fixture = new Fixture();
        var a = fixture.Write("a.cs", "disk value\nvalue");
        fixture.Write("b.cs", "value");
        using var document = new EditorDocumentState();
        await document.OpenAsync(a);
        document.Content = "dirty value";
        var batches = await Collect(new WorkspaceSearchService(fixture.Root).SearchAsync(
            new("value", BatchSize: 2), [document]));

        Assert.HasCount(2, batches);
        Assert.HasCount(2, batches[0].Matches);
        Assert.AreEqual("a.cs", batches[0].Matches[0].RelativePath);
        Assert.AreEqual(document.Version, batches[0].Matches[0].DocumentVersion);
        Assert.AreEqual("b.cs", batches[0].Matches[1].RelativePath);
        Assert.IsTrue(batches[^1].IsComplete);
    }

    [TestMethod]
    public async Task RegexFiltersWholeWordsAndReportsBinaryFiles()
    {
        using var fixture = new Fixture();
        fixture.Write("src/keep.cs", "Cat cat category");
        fixture.Write("obj/ignored.cs", "cat");
        fixture.WriteBytes("src/data.bin", [1, 0, 2]);

        var batches = await Collect(new WorkspaceSearchService(fixture.Root).SearchAsync(
            new("cat", UseRegex: true, MatchCase: false, MatchWholeWord: true, IncludeGlobs: ["src/**"]))) ;

        Assert.HasCount(2, batches.SelectMany(item => item.Matches).ToArray());
        Assert.HasCount(1, batches.SelectMany(item => item.Issues).ToArray());
    }

    [TestMethod]
    public async Task ReplacementUsesWorkspaceTransactionAndRejectsChangedInputs()
    {
        using var fixture = new Fixture();
        var first = fixture.Write("first.cs", "hello world");
        var second = fixture.Write("second.cs", "hello world");
        var service = new WorkspaceSearchService(fixture.Root);
        var edit = await service.CreateReplaceEditAsync(new("hello"), "goodbye");
        Assert.AreEqual(new TextRange(0, 5), edit.Documents[0].ExpectedRanges![0]);
        await File.WriteAllTextAsync(second, "changed");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new WorkspaceEditTransaction().ApplyAsync(edit, []));

        Assert.AreEqual("hello world", await File.ReadAllTextAsync(first));
        Assert.AreEqual("changed", await File.ReadAllTextAsync(second));
    }

    [TestMethod]
    public async Task CancellationStopsSearch()
    {
        using var fixture = new Fixture();
        fixture.Write("a.cs", new string('a', 10_000));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Collect(new WorkspaceSearchService(fixture.Root).SearchAsync(new("a"),
                cancellationToken: cancellation.Token)));
    }

    [TestMethod]
    public async Task RegexTimeoutIsARecoverableFileIssue()
    {
        using var fixture = new Fixture();
        fixture.Write("slow.txt", new string('a', 50_000) + "!");

        var batches = await Collect(new WorkspaceSearchService(fixture.Root).SearchAsync(
            new("(a+)+$", UseRegex: true, RegexTimeout: TimeSpan.FromMilliseconds(1))));

        Assert.HasCount(1, batches.SelectMany(item => item.Issues).ToArray());
        Assert.IsTrue(batches[^1].IsComplete);
    }

    [TestMethod]
    public async Task ReplacementRejectsMetadataChangesEvenWhenContentIsRestored()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("item.cs", "before");
        var edit = await new WorkspaceSearchService(fixture.Root).CreateReplaceEditAsync(new("before"), "after");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new WorkspaceEditTransaction().ApplyAsync(edit, []));

        Assert.AreEqual("before", await File.ReadAllTextAsync(path));
    }

    private static async Task<List<WorkspaceSearchBatch>> Collect(IAsyncEnumerable<WorkspaceSearchBatch> source,
        CancellationToken cancellationToken = default)
    {
        var result = new List<WorkspaceSearchBatch>();
        await foreach (var item in source.WithCancellation(cancellationToken)) result.Add(item);
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.Search.Tests", Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        internal string Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        internal void WriteBytes(string relative, byte[] bytes)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
