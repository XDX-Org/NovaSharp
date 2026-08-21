using System.Text;
using NovaSharp.Editing;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentFileStoreTests : IDisposable
{
    private readonly DocumentFileStore _store = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("novasharp-store").FullName;

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    [Fact]
    public async Task WriteAllBytesAsync_CreatesAFileThatWasNotThere()
    {
        var path = Path("new.cs");

        await _store.WriteAllBytesAsync(path, "class Widget;"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("class Widget;", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAllBytesAsync_ReplacesAFileThatWas()
    {
        var path = Path("existing.cs");
        await File.WriteAllTextAsync(path, "old and much longer content", TestContext.Current.CancellationToken);

        await _store.WriteAllBytesAsync(path, "new"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAllBytesAsync_LeavesNoTemporaryFileBehind()
    {
        var path = Path("clean.cs");

        await _store.WriteAllBytesAsync(path, "class Widget;"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task WriteAllBytesAsync_LeavesTheOriginalIntactWhenItCannotFinish()
    {
        // A write that fails must not be observable as a truncated file. The original is replaced in one step or not
        // at all, and the temporary sibling is cleaned up either way.
        var path = Path("survivor.cs");
        await File.WriteAllTextAsync(path, "the original", TestContext.Current.CancellationToken);

        var directory = Path("survivor.cs.impossible");
        Directory.CreateDirectory(directory);

        await Assert.ThrowsAnyAsync<IOException>(
            () => _store.WriteAllBytesAsync(directory, "replacement"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Equal("the original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetState_ReportsWhatWasThereAndWhatWasNot()
    {
        var path = Path("state.cs");
        Assert.False(_store.GetState(path).Exists);

        await File.WriteAllTextAsync(path, "class Widget;", TestContext.Current.CancellationToken);
        var state = _store.GetState(path);

        Assert.True(state.Exists);
        Assert.Equal(Encoding.UTF8.GetByteCount("class Widget;"), state.Length);
        Assert.False(state.ReadOnly);
    }

    [Fact]
    public async Task GetState_NoticesAWriteThroughTheStore()
    {
        var path = Path("changed.cs");
        await File.WriteAllTextAsync(path, "one", TestContext.Current.CancellationToken);
        var before = _store.GetState(path);

        await _store.WriteAllBytesAsync(path, "a different length entirely"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.False(_store.GetState(path).Matches(before));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
