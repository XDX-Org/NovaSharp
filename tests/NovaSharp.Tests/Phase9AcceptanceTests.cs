using System.Diagnostics;
using System.Text;

namespace NovaSharp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Phase9AcceptanceTests
{
    [TestMethod]
    public async Task UnicodeMixedLineEndingsRegexAndLiteralReplacementRoundTrip()
    {
        using var fixture = new Fixture();
        var utf16 = fixture.Path("unicode.cs");
        await File.WriteAllTextAsync(utf16, "α Cat\r\nβ category\nγ cat\r", Encoding.Unicode);
        var service = new WorkspaceSearchService(fixture.Root);
        var matches = await Collect(service.SearchAsync(new("cat", MatchWholeWord: true)));

        Assert.HasCount(2, matches);
        Assert.AreEqual(1, matches[0].Line);
        Assert.AreEqual(3, matches[0].Column);
        Assert.AreEqual(3, matches[1].Line);

        var edit = await service.CreateReplaceEditAsync(new("cat", MatchWholeWord: true), "$value");
        await new WorkspaceEditTransaction().ApplyAsync(edit, []);
        StringAssert.Contains(await File.ReadAllTextAsync(utf16), "$value");
        var bytes = await File.ReadAllBytesAsync(utf16);
        Assert.IsTrue(bytes[0] == 0xff && bytes[1] == 0xfe);
    }

    [TestMethod]
    public async Task RegexReplacementExpandsCaptureGroups()
    {
        using var fixture = new Fixture();
        var path = fixture.Path("capture.cs");
        await File.WriteAllTextAsync(path, "one-1 two-2");
        var service = new WorkspaceSearchService(fixture.Root);

        var edit = await service.CreateReplaceEditAsync(new("([a-z]+)-(\\d)", UseRegex: true), "$2:$1");
        await new WorkspaceEditTransaction().ApplyAsync(edit, []);

        Assert.AreEqual("1:one 2:two", await File.ReadAllTextAsync(path));
        Assert.HasCount(2, edit.Documents[0].ExpectedRanges!);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task FiveThousandFileFixtureMeetsSearchBudgets()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 5_000; index++)
            await File.WriteAllTextAsync(fixture.Path($"src/{index / 250:D2}/File{index:D4}.cs"),
                index % 10 == 0 ? "class Needle { }" : "class Fixture { }");
        var before = GC.GetTotalMemory(true);
        var stopwatch = Stopwatch.StartNew();
        var first = TimeSpan.Zero;
        var count = 0;
        await foreach (var batch in new WorkspaceSearchService(fixture.Root).SearchAsync(new("Needle", BatchSize: 32)))
        {
            if (count == 0 && batch.Matches.Count > 0) first = stopwatch.Elapsed;
            count += batch.Matches.Count;
        }
        stopwatch.Stop();
        var retained = GC.GetTotalMemory(true) - before;

        Assert.AreEqual(500, count);
        Assert.IsTrue(first < TimeSpan.FromSeconds(2), $"First result took {first}.");
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"Search took {stopwatch.Elapsed}.");
        Assert.IsTrue(retained < 64 * 1024 * 1024, $"Search retained {retained:N0} bytes.");
    }

    private static async Task<List<WorkspaceSearchMatch>> Collect(IAsyncEnumerable<WorkspaceSearchBatch> source)
    {
        var result = new List<WorkspaceSearchMatch>();
        await foreach (var batch in source) result.AddRange(batch.Matches);
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovaSharp.Phase9.Tests", Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        internal string Path(string relative)
        {
            var path = System.IO.Path.Combine(Root, relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
