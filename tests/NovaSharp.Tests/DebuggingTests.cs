using System.Text;
using System.Diagnostics;

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

    [TestMethod]
    public void InspectionIsBoundedPagedAndRejectedAfterResume()
    {
        var store = new DebugInspectionStore(maxFrames: 2, maxVariables: 3);
        store.BeginPause(4);
        Assert.IsTrue(store.SetFrames(4, Enumerable.Range(1, 4).Select(id => new DebugStackFrame(id, $"frame{id}", null, id, 1))));
        Assert.AreEqual(2, store.Frames.Count);
        Assert.IsTrue(store.SetVariables(4, 12, Enumerable.Range(1, 5).Select(id => new DebugVariable($"v{id}", id.ToString(), "int", 0, null, null))));
        Assert.AreEqual("v2", store.Variables(12, 1, 1).Single().Name);
        store.Resume();
        Assert.IsFalse(store.SetFrames(4, []));
        Assert.AreEqual(0, store.Variables(12).Count);
    }

    [TestMethod]
    public void BreakpointsTrackLineEditsAndReturnToPending()
    {
        var path = Path.GetFullPath("source.cs");
        var store = new BreakpointStore();
        store.Replace(path, [new(path, 10, State: DebugBreakpointState.Verified, BoundLine: 10)]);
        store.ApplyLineEdit(path, 5, 0, 3);
        var breakpoint = store.ForSource(path).Single();
        Assert.AreEqual(13, breakpoint.Line);
        Assert.AreEqual(DebugBreakpointState.Pending, breakpoint.State);
        Assert.IsNull(breakpoint.BoundLine);
    }

    [TestMethod]
    public void PackagedAdapterResolutionIsExplicit()
    {
        var root = Path.Combine(Path.GetTempPath(), "novasharp-adapter-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "DebugAdapters", "netcoredbg", OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "fixture");
        try { Assert.AreEqual(executable, DebugAdapterCatalog.Resolve(root)); }
        finally { Directory.Delete(root, true); }
        Assert.Throws<FileNotFoundException>(() => DebugAdapterCatalog.Resolve(root));
    }

    [TestMethod, Timeout(30000)]
    public async Task PinnedAdapterLaunchesManagedFixture()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS()
            ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : "linux-x64";
        var macOs = OperatingSystem.IsMacOS();
        var executable = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var adapter = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }.SelectMany(start =>
        {
            var paths = new List<string>();
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                paths.Add(Path.Combine(directory.FullName, "src", "NovaSharp", "DebugAdapters", "Assets", rid, "netcoredbg", executable));
            return paths;
        }).FirstOrDefault(File.Exists) ?? "";
        Assert.IsTrue(File.Exists(adapter), $"Acquire the pinned debug adapter for {rid} before testing.");
        var root = Path.Combine(Path.GetTempPath(), "novasharp-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
            var source = Path.Combine(root, "Program.cs");
            await File.WriteAllTextAsync(source, "var value = 41;\nConsole.WriteLine(value + 1);\n");
            using var build = Process.Start(new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "build", "--nologo", "--configuration", "Debug" } })!;
            await build.WaitForExitAsync();
            Assert.AreEqual(0, build.ExitCode, await build.StandardError.ReadToEndAsync());
            var program = Path.Combine(root, "bin", "Debug", "net9.0", "Fixture.dll");
            await using var session = await DebugAdapterSession.LaunchAsync(new(program, root, [], StopAtEntry: true,
                Breakpoints: macOs ? null : [new(source, 2)]), adapter);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (session.Coordinator.State == DebugSessionState.Running && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.AreEqual(DebugSessionState.Paused, session.Coordinator.State);
            if (macOs)
            {
                Assert.IsTrue((await session.LoadStackAsync()).Count > 0);
                return;
            }
            if (session.Breakpoints.Single().State != DebugBreakpointState.Verified && session.Coordinator.State == DebugSessionState.Paused)
            {
                var epoch = session.Coordinator.PauseEpoch;
                await session.ContinueAsync(session.CurrentThreadId!.Value);
                deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while ((session.Coordinator.PauseEpoch == epoch || session.Breakpoints.Single().State != DebugBreakpointState.Verified)
                    && DateTime.UtcNow < deadline) await Task.Delay(20);
            }
            Assert.AreEqual(DebugBreakpointState.Verified, session.Breakpoints.Single().State, session.Breakpoints.Single().Message);
            var frames = await session.LoadStackAsync();
            if (frames[0].Line != 2)
            {
                var epoch = session.Coordinator.PauseEpoch;
                deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                await session.ContinueAsync(session.CurrentThreadId!.Value);
                while (session.Coordinator.PauseEpoch == epoch && DateTime.UtcNow < deadline) await Task.Delay(20);
                frames = await session.LoadStackAsync();
            }
            Assert.IsTrue(frames.Count > 0);
            Assert.AreEqual(2, frames[0].Line);
            var evaluation = await session.EvaluateAsync("value", frames[0].Id);
            Assert.AreEqual("41", evaluation!.Result);
        }
        finally { Directory.Delete(root, true); }
    }
}
