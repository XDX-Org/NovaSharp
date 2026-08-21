using System.Text;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Text;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentSaverTests : IAsyncDisposable
{
    private readonly BoundedWorkQueue _queue = new(capacity: 8, workerCount: 1);
    private readonly DocumentFileStore _store = new();
    private readonly DocumentLoader _loader;
    private readonly DocumentSaver _saver;
    private readonly string _directory = Directory.CreateTempSubdirectory("novasharp-save").FullName;

    public DocumentSaverTests()
    {
        var paths = new WorkspacePaths();
        var codec = new DocumentTextCodec();
        _loader = new DocumentLoader(paths, _store, codec, _queue);
        _saver = new DocumentSaver(paths, _store, codec, _queue);
    }

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    private Task<OpenedDocument> OpenAsync(string path) =>
        _loader.OpenAsync(path, TextEncodings.Utf8, LineEndingStyle.Lf, TestContext.Current.CancellationToken);

    private static DocumentSnapshot Snapshot(string text, long sequence = 5) => new(text, sequence, sequence);

    [Fact]
    public async Task SaveAsync_WritesTheSnapshotAndAdvancesTheSavedSequence()
    {
        var path = Path("widget.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);

        var result = await _saver.SaveAsync(
            opened.Record,
            Snapshot("class Gadget;\n", sequence: 12),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, result.Status);
        Assert.Equal("class Gadget;\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(12, result.Record.SavedSequence);
        Assert.False(result.Record.IsDirty(12));
        Assert.True(result.Record.IsDirty(13));
    }

    [Fact]
    public async Task SaveAsync_KeepsTheEncodingAndLineEndingTheFileHad()
    {
        var path = Path("windows.cs");
        var original = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("class Widget;\r\n"))
            .ToArray();
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);

        var opened = await OpenAsync(path);
        Assert.Equal(LineEndingStyle.CrLf, opened.Record.LineEnding);
        Assert.True(opened.Record.Encoding.ByteOrderMark);

        // The editor's text uses the ending Monaco was given; the save converts it back.
        var result = await _saver.SaveAsync(
            opened.Record,
            Snapshot("class Widget;\r\nclass Gadget;\r\n"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, result.Status);
        var written = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetPreamble(), written.Take(3));
        Assert.Equal("class Widget;\r\nclass Gadget;\r\n", Encoding.UTF8.GetString(written.Skip(3).ToArray()));
    }

    [Fact]
    public async Task SaveAsync_RefusesToOverwriteAFileSomethingElseChanged()
    {
        var path = Path("contested.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "written by something else\n", TestContext.Current.CancellationToken);

        var refused = await _saver.SaveAsync(opened.Record, Snapshot("mine\n"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.ExternallyChanged, refused.Status);
        Assert.Equal("written by something else\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        // And writes it only when the user has explicitly chosen to.
        var forced = await _saver.SaveAsync(
            opened.Record,
            Snapshot("mine\n"),
            overwriteExternalChange: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, forced.Status);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_RefusesAnEncodingThatWouldLoseCharacters()
    {
        var path = Path("lossy.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);

        var result = await _saver.SaveAsync(
            opened.Record,
            Snapshot("// 𝄞\n"),
            encoding: TextEncodings.Latin1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Unrepresentable, result.Status);
        Assert.Contains("U+1D11E", result.Message);
        Assert.Equal("class Widget;\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_RefusesAReadOnlyFile()
    {
        var path = Path("locked.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var result = await _saver.SaveAsync(
                opened.Record,
                Snapshot("mine\n"),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(DocumentSaveStatus.ReadOnly, result.Status);
            Assert.Equal("class Widget;\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task SaveAsync_ToAnotherPathMovesTheDocumentThere()
    {
        var path = Path("original.cs");
        var target = Path("renamed.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);

        var result = await _saver.SaveAsync(
            opened.Record,
            Snapshot("class Widget;\n"),
            targetPath: target,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, result.Status);
        Assert.Equal(target, result.Record.Path);
        Assert.Equal("renamed.cs", result.Record.DisplayName);
        Assert.EndsWith("renamed.cs", result.Record.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.True(File.Exists(path), "Save as must not remove the file the document came from.");
    }

    [Fact]
    public async Task SaveAsync_ToAnotherPathDoesNotTreatThatFileAsAnExternalChange()
    {
        // Naming an existing file in a save-as dialog is the user choosing to overwrite it. It is not NovaSharp
        // finding that the document's own file moved underneath it, and confusing the two would make save-as
        // impossible on any file that already exists.
        var path = Path("source.cs");
        var target = Path("target.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(target, "something already here\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);

        var result = await _saver.SaveAsync(
            opened.Record,
            Snapshot("mine\n"),
            targetPath: target,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, result.Status);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_WithADifferentLineEndingConvertsTheWholeFile()
    {
        var path = Path("endings.cs");
        await File.WriteAllTextAsync(path, "a\nb\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);

        var result = await _saver.SaveAsync(
            opened.Record,
            Snapshot("a\nb\n"),
            lineEnding: LineEndingStyle.CrLf,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, result.Status);
        Assert.Equal("a\r\nb\r\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(LineEndingStyle.CrLf, result.Record.LineEnding);
    }

    [Fact]
    public async Task SaveAsync_IsCancellable()
    {
        var path = Path("cancelled.cs");
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        var opened = await OpenAsync(path);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _saver.SaveAsync(opened.Record, Snapshot("mine\n"), cancellationToken: cancellation.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
