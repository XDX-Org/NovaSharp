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
    public void SelectionTracksRevertedCompletionEdit()
    {
        const string completed = "class C { Task Run() { var result = Task.CompletedTask; return result; } }";
        const string typed = "class C { Task Run() { var result = Task.c; return result; } }";
        var view = new EditorViewState();
        var completedCaret = completed.IndexOf(';');
        view.SetSelection(completedCaret, completedCaret, completed.Length);

        view.ApplyTextChange(completed, typed);

        Assert.AreEqual(typed.IndexOf(';'), view.SelectionStart);
        Assert.AreEqual(view.SelectionStart, view.SelectionEnd);
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
    public void AdditiveSemanticClassificationsDoNotDuplicateSourceText()
    {
        const string text = "class C { static void Method() { } }";
        var start = text.IndexOf("Method", StringComparison.Ordinal);
        var lines = CSharpTokenizer.Tokenize(text,
        [
            new(start, 6, "method name"),
            new(start, 6, "static symbol")
        ]);

        var method = lines.Single().Spans.Where(span => span.Start == start).ToArray();
        Assert.HasCount(1, method);
        Assert.AreEqual(TokenKind.Method, method[0].Kind);
    }

    [TestMethod]
    public void ServerOnlyColouringDoesNotReleaseLocalTokens()
    {
        const string text = "public class Demo { void Run() { } }";
        var methodStart = text.IndexOf("Run", StringComparison.Ordinal);
        var spans = CSharpTokenizer.Tokenize(text, [new(methodStart, 3, "method")], includeLocalColouring: false)
            .Single().Spans;

        Assert.HasCount(1, spans);
        Assert.AreEqual(new(methodStart, 3, TokenKind.Method), spans[0]);
    }

    [TestMethod]
    public void MultilineBracesProduceConnectedGuides()
    {
        var lines = CSharpTokenizer.Tokenize("void Run()\n{\n    Work();\n}", [], includeLocalColouring: false);

        Assert.AreEqual(BraceGuidePart.Start, lines[1].BraceGuides!.Single().Part);
        Assert.AreEqual(BraceGuidePart.Middle, lines[2].BraceGuides!.Single().Part);
        Assert.AreEqual(BraceGuidePart.End, lines[3].BraceGuides!.Single().Part);
    }

    [TestMethod]
    public void ServerColouringCoversStandardRazorAndUnknownClassifications()
    {
        const string text = "abcdefghijk";
        var classifications = new[]
        {
            "variable", "constant", "enumMember", "interface", "struct", "class", "typeParameter",
            "operator", "razorComponentElement", "razorComponentAttribute", "futureServerToken"
        };
        var expected = new[]
        {
            TokenKind.Variable, TokenKind.Constant, TokenKind.EnumMember, TokenKind.Interface, TokenKind.Struct,
            TokenKind.Class, TokenKind.TypeParameter, TokenKind.Operator, TokenKind.Component,
            TokenKind.ComponentAttribute, TokenKind.Semantic
        };
        var spans = CSharpTokenizer.Tokenize(text, classifications.Select((value, index) =>
            new SemanticSpan(index, 1, value)).ToArray(), includeLocalColouring: false).Single().Spans;

        CollectionAssert.AreEqual(expected, spans.Select(span => span.Kind).ToArray());
    }

    [TestMethod]
    public void SemanticClassificationsPreserveInterpolatedStringColour()
    {
        const string text = ": IOException($\"'{Path.GetFileName(path)}' changed on disk.\");";
        var methodStart = text.IndexOf("GetFileName", StringComparison.Ordinal);
        var spans = CSharpTokenizer.Tokenize(text, [new(methodStart, "GetFileName".Length, "method name")])
            .Single().Spans;

        Assert.AreEqual(TokenKind.Method, spans.Single(span => span.Start == methodStart).Kind);
        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.String && span.Start < methodStart));
        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.String && span.Start > methodStart));
    }

    [TestMethod]
    public void AccessorsUseKeywordColour()
    {
        var spans = CSharpTokenizer.Tokenize("string Name { get; init; } event Action Changed { add { } remove { } }")
            .Single().Spans;

        var text = "string Name { get; init; } event Action Changed { add { } remove { } }";
        CollectionAssert.IsSubsetOf(new[] { "get", "init", "add", "remove" }, spans
            .Where(span => span.Kind == TokenKind.Keyword).Select(span => text.Substring(span.Start, span.Length)).ToArray());
    }

    [TestMethod]
    public void GeneratedRegexPatternsUseEmbeddedColouring()
    {
        var spans = CSharpTokenizer.Tokenize("[GeneratedRegex(\"^(?<line>\\\\d+)[a-z]*$\")]").Single().Spans;

        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.RegexEscape));
        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.RegexGroup));
        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.RegexCharacterClass));
        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.RegexQuantifier));
    }

    [TestMethod]
    public void PartialUsesKeywordColour()
    {
        const string text = "private static partial class Service";
        var spans = CSharpTokenizer.Tokenize(text).Single().Spans;

        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.Keyword
            && text.Substring(span.Start, span.Length) == "partial"));
    }

    [TestMethod]
    public void RazorMarkupUsesDedicatedTagAttributeAndTransitionColours()
    {
        const string text = "<div class=\"code-editor @(WordWrap ? \\\"word-wrap\\\" : null)\" data-language=\"@LanguageInfo.LanguageId\" @ref=\"_root\">";
        var spans = CSharpTokenizer.Tokenize(text).Single().Spans;

        AssertSpan("div", TokenKind.HtmlTag);
        AssertSpan("class", TokenKind.HtmlAttribute);
        AssertSpan("data-language", TokenKind.HtmlAttribute);
        AssertSpan("ref", TokenKind.HtmlAttribute);
        Assert.IsTrue(spans.Any(span => span.Kind == TokenKind.RazorTransition
            && text.Substring(span.Start, span.Length) == "@("));

        void AssertSpan(string value, TokenKind kind) => Assert.IsTrue(spans.Any(span => span.Kind == kind
            && text.Substring(span.Start, span.Length) == value), $"Missing {kind} span for {value}.");
    }

    [TestMethod]
    public void IdentifiersRequireSemanticClassification()
    {
        const string text = "class Refresh { void Run() { service.Value = _field; } }";
        var local = CSharpTokenizer.Tokenize(text).Single().Spans;
        var methodStart = text.IndexOf("Run", StringComparison.Ordinal);
        var semantic = CSharpTokenizer.Tokenize(text, [new(methodStart, 3, "method name")]).Single().Spans;

        Assert.IsFalse(local.Any(span => span.Kind is TokenKind.Type or TokenKind.Method or TokenKind.Property or TokenKind.Field));
        Assert.AreEqual(TokenKind.Method, semantic.Single(span => span.Start == methodStart).Kind);
    }

    [TestMethod]
    public void RazorCodeDirectiveUsesKeywordColour()
    {
        const string text = "@code { private int _value; }";
        var span = CSharpTokenizer.Tokenize(text).Single().Spans.Single(item => item.Start == 1);

        Assert.AreEqual(TokenKind.Keyword, span.Kind);
        Assert.AreEqual("code", text.Substring(span.Start, span.Length));
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
            var settings = new EditorSettings(1, true, 2, PopupPlacement: "BottomCenter");
            await service.SaveUserAsync(settings);
            var restored = new ConfigurationService(path);
            await restored.LoadAsync();
            Assert.AreEqual(settings, restored.Current);
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
