using System.Diagnostics;

namespace NovaSharp.Tests;

[TestClass]
public sealed class BuildRunTests
{
    [TestMethod]
    public void GuiSessionEnvironmentIsInherited()
    {
        CollectionAssert.Contains(BuildRunService.InheritedEnvironment, "DISPLAY");
        CollectionAssert.Contains(BuildRunService.InheritedEnvironment, "WAYLAND_DISPLAY");
        CollectionAssert.Contains(BuildRunService.InheritedEnvironment, "XAUTHORITY");
        CollectionAssert.Contains(BuildRunService.InheritedEnvironment, "XDG_RUNTIME_DIR");
        CollectionAssert.Contains(BuildRunService.InheritedEnvironment, "DBUS_SESSION_BUS_ADDRESS");
    }

    [TestMethod]
    public void OutputIsBoundedAndKeepsLocations()
    {
        var output = new OutputChannel(maxEntries: 2, maxBytes: 100);
        output.Add(OutputStream.System, "one");
        output.Add(OutputStream.StandardOutput, "two", "/source.cs", 3, 4);
        output.Add(OutputStream.StandardError, "three");

        Assert.AreEqual(2, output.Entries.Count);
        Assert.AreEqual("two", output.Entries[0].Text);
        Assert.AreEqual("/source.cs", output.Entries[0].FilePath);
        Assert.IsTrue(output.Entries[0].Sequence < output.Entries[1].Sequence);
    }

    [TestMethod]
    public async Task FixtureBuildPublishesMatchingStructuredDiagnosticThenClearsIt()
    {
        using var fixture = new ProjectFixture("Console.WriteLine(missing);");
        var diagnostics = new LanguageDiagnosticStore();
        await using var service = new BuildRunService(diagnostics);

        var failed = await service.ExecuteAsync(new(fixture.Project, BuildOperation.Build));

        Assert.AreEqual(BuildTaskState.Failed, failed.State);
        Assert.AreNotEqual(0, failed.ExitCode);
        var diagnostic = diagnostics.Entries.Single(item => item.Id == "CS0103");
        Assert.AreEqual(LanguageDiagnosticSource.Build, diagnostic.Source);
        Assert.AreEqual(fixture.Source, diagnostic.DocumentPath);
        Assert.AreEqual(0, diagnostic.StartLine);
        Assert.IsTrue(diagnostic.Range.Start > 0);
        Assert.IsTrue(service.Output.Entries.Any(item => item.FilePath == fixture.Source));

        await File.WriteAllTextAsync(fixture.Source, "Console.WriteLine(42);");
        var succeeded = await service.ExecuteAsync(new(fixture.Project, BuildOperation.Build));
        Assert.AreEqual(BuildTaskState.Succeeded, succeeded.State);
        Assert.AreEqual(0, diagnostics.Entries.Count);
    }

    [TestMethod]
    public async Task RunSupportsArgumentsAndStandardInput()
    {
        using var fixture = new ProjectFixture("Console.WriteLine(args[0]); Console.WriteLine(Console.ReadLine());");
        await using var service = new BuildRunService(new());
        var run = service.ExecuteAsync(new(fixture.Project, BuildOperation.Run, Arguments: ["argument-value"]));
        await WaitUntilAsync(() => service.ActiveTask is not null);
        Assert.IsTrue(await service.SendInputAsync("input-value\n"));

        var completed = await run;
        Assert.AreEqual(BuildTaskState.Succeeded, completed.State);
        var text = service.Output.Entries.Select(item => item.Text).ToArray();
        CollectionAssert.Contains(text, "argument-value");
        CollectionAssert.Contains(text, "input-value");
    }

    [TestMethod]
    public async Task CancellationStopsOwnedRunWithinBudget()
    {
        using var fixture = new ProjectFixture("await Task.Delay(TimeSpan.FromMinutes(2));");
        await using var service = new BuildRunService(new());
        var running = service.ExecuteAsync(new(fixture.Project, BuildOperation.Run));
        await WaitUntilAsync(() => service.ActiveTask is not null);
        var stopwatch = Stopwatch.StartNew();

        service.Stop();
        var completed = await running;

        Assert.AreEqual(BuildTaskState.Canceled, completed.State);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cleanup took {stopwatch.Elapsed}.");
        Assert.IsNull(service.ActiveTask);
    }

    [TestMethod]
    public async Task ConflictingOperationsAreQueued()
    {
        using var fixture = new ProjectFixture("await Task.Delay(TimeSpan.FromMinutes(2));");
        await using var service = new BuildRunService(new());
        var first = service.ExecuteAsync(new(fixture.Project, BuildOperation.Run));
        await WaitUntilAsync(() => service.ActiveTask is not null);
        var second = service.ExecuteAsync(new(fixture.Project, BuildOperation.Clean));
        await WaitUntilAsync(() => service.Tasks.Count == 2);
        Assert.AreEqual(BuildTaskState.Queued, service.Tasks[1].State);
        service.Stop();
        await first;
        Assert.AreEqual(BuildTaskState.Succeeded, (await second).State);
    }

    [TestMethod]
    public async Task InvalidProjectFailsRecoverably()
    {
        await using var service = new BuildRunService(new());
        var completed = await service.ExecuteAsync(new(Path.Combine(Path.GetTempPath(), "missing.csproj"), BuildOperation.Build));
        Assert.AreEqual(BuildTaskState.Failed, completed.State);
        Assert.IsNotNull(completed.Error);
    }

    [TestMethod]
    public async Task DisplayedArgumentsRedactSecretValues()
    {
        using var fixture = new ProjectFixture("Console.WriteLine(\"done\");");
        await using var service = new BuildRunService(new());
        await service.ExecuteAsync(new(fixture.Project, BuildOperation.Run,
            Arguments: ["--password", "not-for-output", "--api-key=also-secret"]));
        var command = service.Output.Entries.First().Text;
        Assert.DoesNotContain("not-for-output", command);
        Assert.DoesNotContain("also-secret", command);
        Assert.Contains("[redacted]", command);
    }

    [TestMethod]
    public async Task StartingAnActionClearsPreviousOutput()
    {
        using var fixture = new ProjectFixture("Console.WriteLine(42);");
        var output = new OutputChannel();
        output.Add(OutputStream.System, "previous action");
        await using var service = new BuildRunService(new(), output);

        var completed = await service.ExecuteAsync(new(fixture.Project, BuildOperation.Build));

        Assert.AreEqual(BuildTaskState.Succeeded, completed.State);
        Assert.IsFalse(output.Entries.Any(entry => entry.Text == "previous action"));
        Assert.IsTrue(output.Entries.Any(entry => entry.Text.StartsWith("dotnet build", StringComparison.Ordinal)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, cancellation.Token);
    }

    private sealed class ProjectFixture : IDisposable
    {
        internal ProjectFixture(string source)
        {
            Root = Path.Combine(Path.GetTempPath(), $"novasharp-build-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Project = Path.Combine(Root, "Fixture.csproj");
            Source = Path.Combine(Root, "Program.cs");
            File.WriteAllText(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
            File.WriteAllText(Source, source);
        }
        internal string Root { get; }
        internal string Project { get; }
        internal string Source { get; }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
