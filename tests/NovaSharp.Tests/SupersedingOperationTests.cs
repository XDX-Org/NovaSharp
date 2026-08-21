using NovaSharp.Async;
using Xunit;

namespace NovaSharp.Tests;

public sealed class SupersedingOperationTests
{
    [Fact]
    public async Task RunAsync_PublishesTheResultOfAnUncontestedOperation()
    {
        await using var operation = new SupersedingOperation();
        var published = new List<int>();

        var accepted = await operation.RunAsync(
            _ => Task.FromResult(7),
            (value, _) =>
            {
                published.Add(value);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        Assert.True(accepted);
        Assert.Equal(7, Assert.Single(published));
    }

    [Fact]
    public async Task RunAsync_DiscardsAStaleResultInsteadOfPublishingIt()
    {
        await using var operation = new SupersedingOperation();
        var published = new List<string>();
        var slowStarted = new TaskCompletionSource();
        var releaseSlow = new TaskCompletionSource();

        var slow = operation.RunAsync(
            async _ =>
            {
                slowStarted.SetResult();
                await releaseSlow.Task;
                return "slow";
            },
            (value, _) =>
            {
                published.Add(value);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        await slowStarted.Task;

        var fast = await operation.RunAsync(
            _ => Task.FromResult("fast"),
            (value, _) =>
            {
                published.Add(value);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        releaseSlow.SetResult();

        Assert.True(fast);
        Assert.False(await slow);
        Assert.Equal("fast", Assert.Single(published));
    }

    [Fact]
    public async Task RunAsync_CancelsTheOperationItSupersedes()
    {
        await using var operation = new SupersedingOperation();
        var started = new TaskCompletionSource();
        var observed = new TaskCompletionSource<bool>();

        var superseded = operation.RunAsync(
            async token =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    observed.SetResult(true);
                    throw;
                }

                return 0;
            },
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        await started.Task;
        await operation.RunAsync(_ => Task.FromResult(1), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(await superseded);
    }

    [Fact]
    public async Task RunAsync_HonoursTheCallersCancellationToken()
    {
        await using var operation = new SupersedingOperation();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var published = false;
        var accepted = await operation.RunAsync(
            token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(1);
            },
            (_, _) =>
            {
                published = true;
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.False(accepted);
        Assert.False(published);
    }

    [Fact]
    public async Task RunAsync_SurfacesFailuresFromTheWork()
    {
        await using var operation = new SupersedingOperation();

        await Assert.ThrowsAsync<IOException>(() => operation.RunAsync<int>(
            _ => throw new IOException("unreadable"),
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Version_AdvancesOncePerStartedOperation()
    {
        await using var operation = new SupersedingOperation();

        Assert.Equal(0L, operation.Version);
        await operation.RunAsync(_ => Task.FromResult(0), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        await operation.RunAsync(_ => Task.FromResult(0), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(2L, operation.Version);
    }

    [Fact]
    public async Task RunAsync_ThrowsAfterDisposal()
    {
        var operation = new SupersedingOperation();
        await operation.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation.RunAsync(
            _ => Task.FromResult(0),
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));
    }
}
