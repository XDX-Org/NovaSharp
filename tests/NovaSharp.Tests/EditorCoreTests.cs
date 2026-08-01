using System.Text;

namespace NovaSharp.Tests;

[TestClass]
public sealed class EditorCoreTests
{
    [TestMethod]
    public async Task EditsPreserveUnicodeTabsAndMultilineText()
    {
        var document = await DocumentAsync("A😀e\u0301\tZ\r\nlast");
        document.ApplyEdit(new(1, "😀e\u0301".Length, "漢字\nline"));

        Assert.AreEqual("A漢字\nline\tZ\r\nlast", document.Content);
        document.Undo();
        Assert.AreEqual("A😀e\u0301\tZ\r\nlast", document.Content);
        document.Dispose();
    }

    [TestMethod]
    public async Task SelectionReplacementIsOneUndoUnit()
    {
        var document = await DocumentAsync("before selected after");
        document.ApplyEdit(new(7, 8, "value"));
        Assert.AreEqual("before value after", document.Content);
        document.Undo();
        Assert.AreEqual("before selected after", document.Content);
        document.Dispose();
    }

    [TestMethod]
    public async Task FindReplaceAndTokenizerAreOwnedByDocument()
    {
        var document = await DocumentAsync("public class Demo { // Demo\n string value = \"Demo\"; }");
        Assert.AreEqual(3, document.Find("demo").Count);
        document.ReplaceAll("Demo", "Sample", matchCase: true);
        Assert.AreEqual(0, document.Find("Demo", matchCase: true).Count);
        var snapshot = document.CreatePresentationSnapshot();
        Assert.IsTrue(snapshot.SelectMany(line => line.Spans).Any(span => span.Kind == TokenKind.Keyword));
        Assert.IsTrue(snapshot.SelectMany(line => line.Spans).Any(span => span.Kind == TokenKind.Comment));
        document.Dispose();
    }

    [TestMethod]
    public async Task Utf16AndCanonicalPathArePreserved()
    {
        var directory = Directory.CreateTempSubdirectory("novasharp-");
        var path = Path.Combine(directory.FullName, "source.cs");
        try
        {
            await File.WriteAllTextAsync(path, "one\r\ntwo", new UnicodeEncoding(false, true));
            using var document = new EditorDocumentState();
            await document.OpenAsync(Path.Combine(directory.FullName, ".", "source.cs"));
            document.Content += "\nthree";
            await document.SaveAsync();
            var bytes = await File.ReadAllBytesAsync(path);
            CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFE }, bytes[..2]);
            Assert.AreEqual(Path.GetFullPath(path), document.FilePath);
            Assert.AreEqual("one\r\ntwo\r\nthree", Encoding.Unicode.GetString(bytes[2..]));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task DeletedFileIsReportedAsConflict()
    {
        var path = Path.GetTempFileName();
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        document.Content = "mine";
        File.Delete(path);
        Assert.IsTrue(document.IsDeletedOnDisk);
        await Assert.ThrowsExactlyAsync<SaveConflictException>(() => document.SaveAsync());
    }

    private static async Task<EditorDocumentState> DocumentAsync(string content)
    {
        var document = new EditorDocumentState();
        await document.OpenAsync("editor-test.cs", _ => Task.FromResult(content));
        return document;
    }
}

[TestClass]
public sealed class WorkbenchServiceTests
{
    [TestMethod]
    public async Task ConfigurationIsValidatedAndStoredAtomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"novasharp-settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new ConfigurationService(path);
            await service.SaveUserAsync(new(1, true, 2));
            var restored = new ConfigurationService(path);
            await restored.LoadAsync();
            Assert.AreEqual(new EditorSettings(1, true, 2), restored.Current);
            Assert.IsFalse(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*.tmp").Any());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task CommandsHonorEnablementAndRejectDuplicates()
    {
        var executions = 0;
        var registry = new CommandRegistry();
        var command = new CommandDescriptor("file.save", "Save", "Ctrl+S", () => true, _ => { executions++; return Task.CompletedTask; });
        registry.Register(command);
        await registry.ExecuteAsync("file.save");
        Assert.AreEqual(1, executions);
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Register(command));
    }

    [TestMethod]
    public async Task LifetimeCancelsOwnedTasksAndLogIsBounded()
    {
        var lifetime = new LifetimeCoordinator();
        var task = lifetime.Run(token => Task.Delay(Timeout.Infinite, token));
        await lifetime.DisposeAsync();
        Assert.IsTrue(task.IsCanceled);

        var log = new NotificationLog(2);
        log.Add(NotificationSeverity.Information, "1", "first");
        log.Add(NotificationSeverity.Warning, "2", "second");
        log.Add(NotificationSeverity.Error, "3", "third");
        Assert.AreEqual(2, log.Entries.Count);
    }
}
