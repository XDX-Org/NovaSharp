using System.Text;

namespace NovaSharp.Tests;

[TestClass]
public sealed class TerminalTests
{
    [TestMethod]
    public void BufferDecodesSplitUnicodeAndAnsiStyles()
    {
        var buffer = new TerminalBuffer();
        var bytes = Encoding.UTF8.GetBytes("plain \ud83d\ude80 \u001b[31;1mred\u001b[0m\n");

        buffer.Append(bytes.AsSpan(0, 8));
        buffer.Append(bytes.AsSpan(8));

        var runs = buffer.Lines[0].Runs;
        Assert.AreEqual("plain \ud83d\ude80 ", runs[0].Text);
        Assert.AreEqual("red", runs[1].Text);
        Assert.AreEqual("#bf616a", runs[1].Style.Foreground);
        Assert.IsTrue(runs[1].Style.Bold);
    }

    [TestMethod]
    public void BufferBoundsScrollbackAndRejectsUnsafeLinks()
    {
        var buffer = new TerminalBuffer(maxLines: 2, maxBytes: 100);
        buffer.Append(Encoding.UTF8.GetBytes("first\nsecond\n\u001b]8;;javascript:alert(1)\aunsafe\u001b]8;;\a"));

        Assert.AreEqual(2, buffer.Lines.Count);
        Assert.AreEqual("second", string.Concat(buffer.Lines[0].Runs.Select(run => run.Text)));
        Assert.IsNull(buffer.Lines[1].Runs.Single().Style.Link);
    }

    [TestMethod]
    public void DeviceAttributeQueriesAreAnsweredAcrossReads()
    {
        var responder = new TerminalQueryResponder();

        Assert.AreEqual(0, responder.Feed(Encoding.ASCII.GetBytes("\x1b[")).Count);
        var primary = responder.Feed(Encoding.ASCII.GetBytes("c"));
        var secondary = responder.Feed(Encoding.ASCII.GetBytes("\x1b[>0c"));

        Assert.AreEqual("\x1b[?1;2c", Encoding.ASCII.GetString(primary.Single()));
        Assert.AreEqual("\x1b[>0;10;1c", Encoding.ASCII.GetString(secondary.Single()));
    }

    [TestMethod]
    public async Task PtyPreservesUnicodeInputResizeAndExit()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Unix shell fixture");
        var profile = new TerminalProfile("test", "test", "/bin/sh", []);
        await using var session = new TerminalSession("test", profile, Path.GetTempPath());

        await session.StartAsync(40, 10);
        Assert.AreEqual(TerminalSessionState.Running, session.State, session.Error);
        session.Resize(100, 30);
        await session.SendAsync(Encoding.UTF8.GetBytes("printf '\\342\\234\\223 unicode\\n'; exit 7\r"));
        await WaitUntilAsync(() => session.State == TerminalSessionState.Exited);

        Assert.AreEqual(7, session.ExitCode);
        Assert.IsTrue(session.Buffer.Lines.Any(line => string.Concat(line.Runs.Select(run => run.Text)).Contains("\u2713 unicode")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, cancellation.Token);
    }
}
