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
