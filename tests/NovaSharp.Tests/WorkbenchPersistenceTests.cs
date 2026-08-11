namespace NovaSharp.Tests;

[TestClass]
public sealed class WorkbenchPersistenceTests
{
    [TestMethod]
    public async Task CorruptOrHostileStateCannotRestore()
    {
        using var fixture = new PersistenceFixture();
        await File.WriteAllTextAsync(fixture.State, "{broken");
        Assert.IsNull(await fixture.Service.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.SaveAsync(new(1, fixture.Root, [], 0, 0.5, null)));
    }

    [TestMethod]
    public async Task RecoveryPreservesExactDirtyContentWithoutChangingDisk()
    {
        using var fixture = new PersistenceFixture();
        var original = Path.Combine(fixture.Root, "source.cs");
        await File.WriteAllTextAsync(original, "disk");
        await fixture.Service.CaptureRecoveryAsync(original, "dirty\r\n😀");
        var recovered = (await fixture.Service.LoadRecoveryAsync()).Single();
        Assert.AreEqual("dirty\r\n😀", recovered.Content);
        Assert.AreEqual("disk", await File.ReadAllTextAsync(original));
    }

    [TestMethod]
    public void RepeatedRestoreFailureEntersSafeMode()
    {
        Assert.IsFalse(WorkbenchPersistence.RequiresSafeMode(new(1, null, [], 0.3, 0.3, null, 2)));
        Assert.IsTrue(WorkbenchPersistence.RequiresSafeMode(new(1, null, [], 0.3, 0.3, null, 3)));
    }

    [TestMethod]
    public async Task InterruptedRestoresEnterSafeModeUntilReset()
    {
        using var fixture = new PersistenceFixture();
        var guard = new StartupRestoreGuard(Path.Combine(fixture.Root, "restore.json"));
        Assert.IsTrue(await guard.BeginAsync());
        Assert.IsTrue(await guard.BeginAsync());
        Assert.IsTrue(await guard.BeginAsync());
        Assert.IsFalse(await guard.BeginAsync());
        await guard.ResetAsync();
        Assert.IsTrue(await guard.BeginAsync());
    }

    [TestMethod]
    public async Task DurableStateValidatesDebugAndRunConfigurationBounds()
    {
        using var fixture = new PersistenceFixture();
        var breakpoint = new PersistedBreakpoint(Path.Combine(fixture.Root, "source.cs"), 12, "value > 2");
        var run = new PersistedRunConfiguration(Path.Combine(fixture.Root, "app.csproj"), "Debug", "net10.0", ["one"], fixture.Root);
        var state = new WorkbenchSnapshot(1, fixture.Root, [], 0.3, 0.4, null,
            Breakpoints: [breakpoint], RunConfigurations: [run], ActivePanel: "debug", PanelOpen: true);
        await fixture.Service.SaveAsync(state);
        Assert.AreEqual("debug", (await fixture.Service.LoadAsync())!.ActivePanel);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.SaveAsync(state with
            { Breakpoints = [breakpoint with { Line = 0 }] }));
    }

    [TestMethod]
    public async Task DiagnosticsAreRedactedAndResetCanPreserveRecovery()
    {
        using var fixture = new PersistenceFixture();
        await fixture.Service.SaveAsync(new(1, fixture.Root, [], 0.3, 0.4, null));
        await fixture.Service.CaptureRecoveryAsync(Path.Combine(fixture.Root, "secret.cs"), "private source");
        var diagnostics = Path.Combine(fixture.Root, "diagnostics.json");
        await fixture.Service.ExportDiagnosticsAsync(diagnostics);
        var text = await File.ReadAllTextAsync(diagnostics);
        Assert.IsFalse(text.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("private source", StringComparison.Ordinal));
        fixture.Service.Reset();
        Assert.IsNull(await fixture.Service.LoadAsync());
        Assert.AreEqual(1, (await fixture.Service.LoadRecoveryAsync()).Count);
    }

    [TestMethod]
    public async Task RepeatedRecoveryIsBoundedAndMeetsRestorationBudget()
    {
        using var fixture = new PersistenceFixture();
        var path = Path.Combine(fixture.Root, "large.cs");
        var content = new string('x', 1024 * 1024);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var index = 0; index < 20; index++) await fixture.Service.CaptureRecoveryAsync(path, content + index);
        var recovered = await fixture.Service.LoadRecoveryAsync();
        Assert.AreEqual(1, recovered.Count);
        Assert.IsTrue(recovered[0].Content.EndsWith("19", StringComparison.Ordinal));
        Assert.IsTrue(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10),
            "Twenty atomic recovery cycles exceeded ten seconds.");
    }

    private sealed class PersistenceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "novasharp-state-" + Guid.NewGuid().ToString("N"));
        internal string State => Path.Combine(Root, "state.json");
        internal WorkbenchPersistence Service { get; }
        internal PersistenceFixture()
        {
            Directory.CreateDirectory(Root);
            Service = new(State, Path.Combine(Root, "recovery"));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
