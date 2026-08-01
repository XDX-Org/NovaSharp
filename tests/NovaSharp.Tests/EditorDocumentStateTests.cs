namespace NovaSharp.Tests;

[TestClass]
public sealed class EditorDocumentStateTests
{
    [TestMethod]
    public async Task CancelLeavesCurrentDocumentUnchanged()
    {
        var document = await OpenDocumentAsync();

        await document.OpenAsync(null, _ => throw new AssertFailedException("Reader should not run."));

        Assert.AreEqual(Path.GetFullPath("original.cs"), document.FilePath);
        Assert.AreEqual("class Original;", document.Content);
        Assert.IsNull(document.Error);
    }

    [TestMethod]
    public async Task SuccessfulOpenReplacesDocumentAndClearsError()
    {
        var document = new EditorDocumentState();
        await document.OpenAsync("denied.cs", _ => throw new UnauthorizedAccessException("Denied"));

        await document.OpenAsync("next.cs", _ => Task.FromResult("class Next;"));

        Assert.AreEqual(Path.GetFullPath("next.cs"), document.FilePath);
        Assert.AreEqual("class Next;", document.Content);
        Assert.IsNull(document.Error);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReadFailureIsVisibleAndPreservesCurrentDocument(bool permissionFailure)
    {
        var document = await OpenDocumentAsync();
        Exception failure = permissionFailure
            ? new UnauthorizedAccessException("Denied")
            : new IOException("Unreadable");

        await document.OpenAsync("broken.cs", _ => Task.FromException<string>(failure));

        Assert.AreEqual(Path.GetFullPath("original.cs"), document.FilePath);
        Assert.AreEqual("class Original;", document.Content);
        StringAssert.Contains(document.Error, "broken.cs");
        StringAssert.Contains(document.Error, failure.Message);
    }

    [TestMethod]
    public async Task EditingUndoRedoAndSaveTrackDirtyVersion()
    {
        var path = TemporaryPath();
        try
        {
            await File.WriteAllTextAsync(path, "class Original;\n");
            var document = new EditorDocumentState();
            await document.OpenAsync(path);
            var openedVersion = document.Version;

            document.Content = "class Changed;\n";
            Assert.IsTrue(document.IsDirty);
            Assert.IsTrue(document.Version > openedVersion);
            document.Undo();
            Assert.AreEqual("class Original;\n", document.Content);
            document.Redo();
            await document.SaveAsync();

            Assert.IsFalse(document.IsDirty);
            Assert.AreEqual("class Changed;\n", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task SavePreservesBomAndCrLf()
    {
        var path = TemporaryPath();
        try
        {
            await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("a\r\nb\r\n")]);
            var document = new EditorDocumentState();
            await document.OpenAsync(path);
            document.Content += "c\n";
            await document.SaveAsync();

            var bytes = await File.ReadAllBytesAsync(path);
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.AreEqual("a\r\nb\r\nc\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task ExternalModificationPreventsSave()
    {
        var path = TemporaryPath();
        try
        {
            await File.WriteAllTextAsync(path, "one");
            var document = new EditorDocumentState();
            await document.OpenAsync(path);
            document.Content = "mine";
            await File.WriteAllTextAsync(path, "external change");

            await Assert.ThrowsExactlyAsync<SaveConflictException>(() => document.SaveAsync());
            Assert.AreEqual("external change", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task InvalidUtf8DoesNotReplaceCurrentDocument()
    {
        var path = TemporaryPath();
        try
        {
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
            var document = await OpenDocumentAsync();
            await document.OpenAsync(path);

            Assert.AreEqual("class Original;", document.Content);
            Assert.IsNotNull(document.Error);
        }
        finally { File.Delete(path); }
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"novasharp-{Guid.NewGuid():N}.cs");

    private static async Task<EditorDocumentState> OpenDocumentAsync()
    {
        var document = new EditorDocumentState();
        await document.OpenAsync("original.cs", _ => Task.FromResult("class Original;"));
        return document;
    }
}
