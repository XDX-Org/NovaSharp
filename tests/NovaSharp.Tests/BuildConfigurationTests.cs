namespace NovaSharp.Tests;

[TestClass]
public sealed class BuildConfigurationTests
{
    [TestMethod]
    public void DiscoversEvaluatedChoicesAndResolvesExactCommand()
    {
        using var fixture = new BuildConfigurationFixture();
        CollectionAssert.AreEqual(new[] { "net10.0", "net9.0" }, BuildConfigurationDiscovery.Frameworks(fixture.Project).ToArray());
        CollectionAssert.AreEqual(new[] { "Debug", "Release", "Profile" }, BuildConfigurationDiscovery.Configurations(fixture.Project).ToArray());
        var profiles = BuildConfigurationDiscovery.Profiles(fixture.LaunchSettings);
        var command = BuildConfigurationDiscovery.Resolve(new(fixture.Project, "Profile", "net10.0", "Local",
            [], fixture.Root, new Dictionary<string, string> { ["MODE"] = "override" }), profiles);
        CollectionAssert.Contains(command.Arguments.ToArray(), "--no-launch-profile");
        CollectionAssert.Contains(command.Arguments.ToArray(), "hello world");
        Assert.AreEqual("override", command.Environment["MODE"]);
        StringAssert.Contains(command.Preview, "\"hello world\"");
    }

    [TestMethod]
    public void InvalidOrStaleSelectionsFailWithoutChangingFiles()
    {
        using var fixture = new BuildConfigurationFixture();
        var before = File.ReadAllText(fixture.Project);
        Assert.Throws<InvalidOperationException>(() => BuildConfigurationDiscovery.Resolve(new(fixture.Project, "Debug", "net8.0", null, [], fixture.Root, new Dictionary<string, string>()), []));
        Assert.AreEqual(before, File.ReadAllText(fixture.Project));
        Assert.AreEqual("PASSWORD=[redacted]", BuildConfigurationDiscovery.Redact("PASSWORD=value"));
    }

    [TestMethod]
    public async Task WorkspaceSelectionPersistsOutsideProjectWithoutSecrets()
    {
        using var fixture = new BuildConfigurationFixture();
        var storage = Path.Combine(fixture.Root, "state");
        var store = new BuildConfigurationStore(storage);
        var value = new PersistedBuildConfiguration(1, fixture.Project, "Release", "net10.0", "Local", ["one"], fixture.Root);
        var projectBefore = File.ReadAllText(fixture.Project);
        await store.SaveAsync(value);
        var restored = await store.LoadAsync(fixture.Project);
        Assert.AreEqual("Release", restored!.Configuration);
        CollectionAssert.AreEqual(new[] { "one" }, restored.Arguments.ToArray());
        Assert.AreEqual(projectBefore, File.ReadAllText(fixture.Project));
    }

    private sealed class BuildConfigurationFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "novasharp-config-" + Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "Fixture.csproj");
        internal string LaunchSettings => Path.Combine(Root, "launchSettings.json");
        internal BuildConfigurationFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks><Configurations>Debug;Release;Profile</Configurations></PropertyGroup></Project>");
            File.WriteAllText(LaunchSettings, "{\"profiles\":{\"Local\":{\"commandName\":\"Project\",\"commandLineArgs\":\"\\\"hello world\\\"\",\"environmentVariables\":{\"MODE\":\"profile\"},\"unknown\":true}}}");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
