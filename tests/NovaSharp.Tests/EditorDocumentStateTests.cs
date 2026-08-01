namespace NovaSharp.Tests;

[TestClass]
public sealed class EditorDocumentStateTests
{
    [TestMethod]
    public async Task CancelLeavesCurrentDocumentUnchanged()
    {
        var document = await OpenDocumentAsync();

        await document.OpenAsync(null, _ => throw new AssertFailedException("Reader should not run."));

        Assert.AreEqual("original.cs", document.FilePath);
        Assert.AreEqual("class Original;", document.Content);
        Assert.IsNull(document.Error);
    }

    [TestMethod]
    public async Task SuccessfulOpenReplacesDocumentAndClearsError()
    {
        var document = new EditorDocumentState();
        await document.OpenAsync("denied.cs", _ => throw new UnauthorizedAccessException("Denied"));

        await document.OpenAsync("next.cs", _ => Task.FromResult("class Next;"));

        Assert.AreEqual("next.cs", document.FilePath);
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

        Assert.AreEqual("original.cs", document.FilePath);
        Assert.AreEqual("class Original;", document.Content);
        StringAssert.Contains(document.Error, "broken.cs");
        StringAssert.Contains(document.Error, failure.Message);
    }

    private static async Task<EditorDocumentState> OpenDocumentAsync()
    {
        var document = new EditorDocumentState();
        await document.OpenAsync("original.cs", _ => Task.FromResult("class Original;"));
        return document;
    }
}
