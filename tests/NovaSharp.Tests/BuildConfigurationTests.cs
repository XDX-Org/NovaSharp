using System.Text.Json;

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
        CollectionAssert.Contains(command.Arguments.ToArray(), "--launch-profile");
        CollectionAssert.Contains(command.Arguments.ToArray(), "hello world");
        Assert.AreEqual("override", command.Environment["MODE"]);
        StringAssert.Contains(command.Preview, "\"hello world\"");
        var request = new BuildRequest(fixture.Project, BuildOperation.Run, "Profile", "net10.0", "Local",
            command.ApplicationArguments, command.Environment, command.WorkingDirectory);
        CollectionAssert.AreEqual(command.Arguments.ToArray(), BuildRunService.CreateArguments(request).ToArray());
    }

    [TestMethod]
    public void EvaluatesConditionalFrameworksAndRejectsSecretEnvironment()
    {
        using var fixture = new BuildConfigurationFixture(conditional: true);
        var choices = BuildConfigurationDiscovery.Discover(fixture.Project);
        CollectionAssert.AreEqual(new[] { "net10.0" }, choices.Frameworks("Debug").ToArray());
        CollectionAssert.AreEqual(new[] { "net9.0" }, choices.Frameworks("Release").ToArray());
        Assert.Throws<InvalidOperationException>(() => BuildConfigurationDiscovery.ParseEnvironment("API_TOKEN=value"));
        var environment = BuildConfigurationDiscovery.ParseEnvironment("MODE=local\nFEATURE_X=1");
        Assert.AreEqual("local", environment["MODE"]);
    }

    [TestMethod]
    public void ReadingAndResolvingProfilePreservesLaunchSettings()
    {
        using var fixture = new BuildConfigurationFixture();
        var before = File.ReadAllBytes(fixture.LaunchSettings);
        var profiles = BuildConfigurationDiscovery.Profiles(fixture.LaunchSettings);
        _ = BuildConfigurationDiscovery.Resolve(new(fixture.Project, "Debug", "net10.0", "Local", [], fixture.Root,
            new Dictionary<string, string>()), profiles);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.LaunchSettings));
    }

    [TestMethod]
    public void MalformedProjectAndProfileFailRecoverably()
    {
        using var fixture = new BuildConfigurationFixture();
        File.WriteAllText(fixture.LaunchSettings, "{");
        Assert.Throws<JsonException>(() => BuildConfigurationDiscovery.Profiles(fixture.LaunchSettings));
        File.WriteAllText(fixture.Project, "<Project>");
        Assert.Throws<InvalidOperationException>(() => BuildConfigurationDiscovery.Discover(fixture.Project));
        File.WriteAllText(fixture.Project, "<Project Sdk=\"Missing.NovaSharp.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Assert.Throws<InvalidOperationException>(() => BuildConfigurationDiscovery.Discover(fixture.Project));
    }

    [TestMethod]
    public void FingerprintsDetectExternalConfigurationChanges()
    {
        using var fixture = new BuildConfigurationFixture();
        var before = BuildConfigurationDiscovery.Fingerprint(fixture.LaunchSettings);
        File.AppendAllText(fixture.LaunchSettings, " ");
        Assert.AreNotEqual(before, BuildConfigurationDiscovery.Fingerprint(fixture.LaunchSettings));
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
        var value = new PersistedBuildConfiguration(1, fixture.Project, "Release", "net10.0", "Local", ["one"], fixture.Root,
            new Dictionary<string, string> { ["MODE"] = "local" });
        var projectBefore = File.ReadAllText(fixture.Project);
        await store.SaveAsync(value);
        var restored = await store.LoadAsync(fixture.Project);
        Assert.AreEqual("Release", restored!.Configuration);
        CollectionAssert.AreEqual(new[] { "one" }, restored.Arguments.ToArray());
        Assert.AreEqual("local", restored.Environment!["MODE"]);
        Assert.AreEqual(projectBefore, File.ReadAllText(fixture.Project));
    }

    private sealed class BuildConfigurationFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "novasharp config-" + Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "Fixture.csproj");
        internal string LaunchSettings => Path.Combine(Root, "launchSettings.json");
        internal BuildConfigurationFixture(bool conditional = false)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Project, conditional
                ? "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Configurations>Debug;Release</Configurations></PropertyGroup><PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\"><TargetFramework>net10.0</TargetFramework></PropertyGroup><PropertyGroup Condition=\"'$(Configuration)' == 'Release'\"><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"
                : "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks><Configurations>Debug;Release;Profile</Configurations></PropertyGroup></Project>");
            File.WriteAllText(LaunchSettings, "{\"profiles\":{\"Local\":{\"commandName\":\"Project\",\"commandLineArgs\":\"\\\"hello world\\\"\",\"environmentVariables\":{\"MODE\":\"profile\"},\"unknown\":true}}}");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
