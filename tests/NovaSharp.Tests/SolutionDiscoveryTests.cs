using NovaSharp.Async;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using Xunit;

namespace NovaSharp.Tests;

public sealed class SolutionDiscoveryTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("novasharp-solution-discovery-").FullName;
    private readonly BoundedWorkQueue _queue = new(4, 1);

    [Fact]
    public async Task PrefersSingleSolutionOverProjects()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Workspace.slnx"), "<Solution />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "App.csproj"), "<Project />", TestContext.Current.CancellationToken);
        var discovery = new SolutionDiscovery(new WorkspacePaths(), _queue);

        var result = await discovery.DiscoverAsync(_root, TestContext.Current.CancellationToken);

        Assert.EndsWith("Workspace.slnx", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotChooseBetweenAmbiguousSolutions()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "One.sln"), string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "Two.sln"), string.Empty, TestContext.Current.CancellationToken);
        var discovery = new SolutionDiscovery(new WorkspacePaths(), _queue);

        Assert.Null(await discovery.DiscoverAsync(_root, TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
