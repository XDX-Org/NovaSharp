using System.Text;

namespace NovaSharp.Tests;

[TestClass]
public sealed class DebuggingTests
{
    [TestMethod]
    public void SessionRejectsCommandsInInvalidStatesAndVersionsPauses()
    {
        var session = new DebugSessionCoordinator();
        Assert.Throws<InvalidOperationException>(() => session.Transition(DebugSessionState.Running));
        session.Transition(DebugSessionState.Starting);
        session.Transition(DebugSessionState.Configuring);
        session.Transition(DebugSessionState.Running);
        session.Transition(DebugSessionState.Paused);
        var epoch = session.PauseEpoch;
        Assert.IsTrue(session.IsCurrentPause(epoch));
        session.Transition(DebugSessionState.Running);
        Assert.IsFalse(session.IsCurrentPause(epoch));
    }

    [TestMethod]
    public async Task ProtocolCorrelatesResponseAndCopiesBody()
    {
        var response = "{\"seq\":2,\"type\":\"response\",\"request_seq\":1,\"success\":true,\"command\":\"initialize\",\"body\":{\"supportsRestartRequest\":true}}";
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(response)}\r\n\r\n{response}");
        await using var input = new MemoryStream(bytes);
        await using var output = new MemoryStream();
        await using var client = new DebugProtocolClient(input, output);

        var body = await client.RequestAsync("initialize", new { clientID = "novasharp" }, TimeSpan.FromSeconds(1));

        Assert.IsTrue(body.GetProperty("supportsRestartRequest").GetBoolean());
        StringAssert.Contains(Encoding.UTF8.GetString(output.ToArray()), "\"command\":\"initialize\"");
    }

    [TestMethod]
    public async Task ProtocolRejectsOversizedMessages()
    {
        await using var client = new DebugProtocolClient(new MemoryStream(), new MemoryStream(), maxMessageBytes: 32);
        await Assert.ThrowsAsync<DebugProtocolException>(() => client.RequestAsync("initialize", new { value = new string('x', 100) }, TimeSpan.FromSeconds(1)));
    }
}
