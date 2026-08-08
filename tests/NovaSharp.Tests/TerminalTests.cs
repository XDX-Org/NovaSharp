using System.Text;

namespace NovaSharp.Tests;

[TestClass]
public sealed class TerminalTests
{
    [TestMethod]
    public void TranscriptPreservesRawBytesAndBoundsMemory()
    {
        var transcript = new TerminalTranscript(maxBytes: 5);
        transcript.Append([1, 2, 3]);
        transcript.Append([4, 5, 6]);

        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, transcript.Chunks.Single().Data);
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
        var output = session.Transcript.Chunks.SelectMany(chunk => chunk.Data).ToArray();
        Assert.Contains("\u2713 unicode", Encoding.UTF8.GetString(output));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, cancellation.Token);
    }
}
