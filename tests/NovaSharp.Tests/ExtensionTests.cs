namespace NovaSharp.Tests;

[TestClass]
public sealed class ExtensionTests
{
    [TestMethod]
    public void ManifestAndPermissionsAreDenyByDefault()
    {
        using var fixture = new ExtensionFixture("{\"manifestVersion\":1,\"id\":\"sample.hello\",\"name\":\"Hello\",\"version\":\"1.0.0\",\"apiVersion\":\"1.0\",\"entryPoint\":\"extension.dll\",\"permissions\":[\"WorkspaceRead\",\"Process\"],\"activationEvents\":[\"onCommand:sample.hello\"],\"commands\":[{\"id\":\"sample.hello\",\"title\":\"Hello\"}],\"settings\":[]}");
        var manifest = ExtensionManifestReader.Read(fixture.Manifest);
        var registry = new ExtensionRegistry(new(1, 0));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Enable(manifest, ExtensionPermission.WorkspaceRead, true));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Enable(manifest, manifest.Permissions, false));
        registry.Enable(manifest, manifest.Permissions, true);
        Assert.IsTrue(registry.Disable(manifest.Id));
        Assert.AreEqual(0, registry.Enabled.Count);
    }

    [TestMethod]
    public void TraversalMalformedAndIncompatibleManifestsFail()
    {
        using var fixture = new ExtensionFixture("{\"manifestVersion\":1,\"id\":\"sample.bad\",\"name\":\"Bad\",\"version\":\"1.0\",\"apiVersion\":\"1.0\",\"entryPoint\":\"../bad.dll\"}");
        Assert.Throws<InvalidDataException>(() => ExtensionManifestReader.Read(fixture.Manifest));
    }

    [TestMethod]
    public async Task SlowAndCrashingActivationFailsWithoutEscapingCoordinator()
    {
        using var fixture = new ExtensionFixture("{\"manifestVersion\":1,\"id\":\"sample.slow\",\"name\":\"Slow\",\"version\":\"1.0\",\"apiVersion\":\"1.0\",\"entryPoint\":\"extension.dll\"}");
        var manifest = ExtensionManifestReader.Read(fixture.Manifest);
        var coordinator = new ExtensionActivationCoordinator(TimeSpan.FromMilliseconds(20));
        var slow = await coordinator.ActivateAsync(manifest, token => Task.Delay(TimeSpan.FromMinutes(1), token));
        Assert.AreEqual("EXT_TIMEOUT", slow.Diagnostic!.Code);
        var crash = await coordinator.ActivateAsync(manifest, _ => throw new InvalidOperationException("private host detail"));
        Assert.AreEqual("EXT_CRASH", crash.Diagnostic!.Code);
        Assert.IsFalse(crash.Diagnostic.Message.Contains("private host detail", StringComparison.Ordinal));
        coordinator.Disable(manifest.Id);
        var disabled = await coordinator.ActivateAsync(manifest, _ => Task.CompletedTask);
        Assert.AreEqual(ExtensionActivationState.Disabled, disabled.State);
    }

    private sealed class ExtensionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "novasharp-extension-" + Guid.NewGuid().ToString("N"));
        internal string Manifest => Path.Combine(_root, "extension.json");
        internal ExtensionFixture(string content) { Directory.CreateDirectory(_root); File.WriteAllText(Manifest, content); }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
