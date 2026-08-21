using System.Text;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Text;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentLoaderTests : IAsyncDisposable
{
    private readonly BoundedWorkQueue _queue = new(capacity: 4, workerCount: 1);
    private readonly string _directory = Directory.CreateTempSubdirectory("novasharp-load").FullName;

    private DocumentLoader Loader(IDocumentFileStore store) =>
        new(new WorkspacePaths(), store, new DocumentTextCodec(), _queue);

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    private sealed class ThrowingStore(Exception failure) : IDocumentFileStore
    {
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(failure);

        public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            Task.FromException(failure);

        public DiskState GetState(string path) => DiskState.Missing;
    }

    [Fact]
    public async Task OpenAsync_ProducesWhatMonacoAndThePersistenceMetadataBothNeed()
    {
        var path = Path("Widget.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);

        var opened = await Loader(new DocumentFileStore())
            .OpenAsync(path, TextEncodings.Utf8, LineEndingStyle.Lf, TestContext.Current.CancellationToken);

        Assert.Equal("Widget.cs", opened.Content.DisplayName);
        Assert.Equal("csharp", opened.Content.LanguageId);
        Assert.Equal("class Widget;\n", opened.Content.Text);
        Assert.Equal("\n", opened.Content.LineEnding);
        Assert.Equal(Uri.UriSchemeFile, opened.Content.Uri.Scheme);

        Assert.Equal(TextEncodings.Utf8, opened.Record.Encoding);
        Assert.Equal(LineEndingStyle.Lf, opened.Record.LineEnding);
        Assert.True(opened.Record.Disk.Exists);
        Assert.False(opened.Record.IsDirty(opened.Record.SavedSequence));
    }

    [Fact]
    public async Task OpenAsync_KeepsEveryCharacterTheFileHeld()
    {
        var path = Path("unicode.cs");
        // A non-ASCII identifier and an astral-plane character: both must survive the read unchanged.
        const string source = "// naïve — 𝄞\nclass Widget;\n";
        await File.WriteAllTextAsync(path, source, TestContext.Current.CancellationToken);

        var opened = await Loader(new DocumentFileStore())
            .OpenAsync(path, TextEncodings.Utf8, LineEndingStyle.Lf, TestContext.Current.CancellationToken);

        Assert.Equal(source, opened.Content.Text);
    }

    [Fact]
    public async Task OpenAsync_TellsMonacoWhenTheFileCannotBeWritten()
    {
        var path = Path("locked.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var opened = await Loader(new DocumentFileStore())
                .OpenAsync(path, TextEncodings.Utf8, LineEndingStyle.Lf, TestContext.Current.CancellationToken);

            Assert.True(opened.Content.ReadOnly);
            Assert.True(opened.Record.IsReadOnly);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task OpenAsync_GivesMonacoAnEndingItCanRepresent()
    {
        var path = Path("classic.cs");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("a\rb\rc"), TestContext.Current.CancellationToken);

        var opened = await Loader(new DocumentFileStore())
            .OpenAsync(path, TextEncodings.Utf8, LineEndingStyle.Lf, TestContext.Current.CancellationToken);

        Assert.Equal(LineEndingStyle.Cr, opened.Record.LineEnding);
        Assert.Equal("\n", opened.Content.LineEnding);
        Assert.Equal("a\nb\nc", opened.Content.Text);
    }

    [Fact]
    public async Task OpenAsync_SurfacesReadFailures()
    {
        var loader = Loader(new ThrowingStore(new UnauthorizedAccessException("denied")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => loader.OpenAsync(Path("Widget.cs"), TextEncodings.Utf8, LineEndingStyle.Lf, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_IsCancellable()
    {
        var path = Path("Widget.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Loader(new DocumentFileStore())
                .OpenAsync(path, TextEncodings.Utf8, LineEndingStyle.Lf, cancellation.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
