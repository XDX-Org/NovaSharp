using Microsoft.CodeAnalysis;
using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.LanguageServices;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using Xunit;

namespace NovaSharp.Tests;

public sealed class CSharpLanguageServiceTests : IAsyncDisposable
{
    private readonly BoundedWorkQueue _solutionWork = new(capacity: 8, workerCount: 2);
    private readonly WorkspacePaths _paths = new();
    private readonly SolutionWorkspaceService _solutions;
    private readonly CSharpLanguageService _language;
    private readonly string _path = Path.Combine(Path.GetTempPath(), "NovaSharp.Phase7.Tests", "Shared.cs");
    private readonly Uri _uri;

    public CSharpLanguageServiceTests()
    {
        var log = new BoundedWorkbenchLog();
        _solutions = new(
            _paths,
            new AdhocSolutionLoader(),
            _solutionWork,
            new DiagnosticStore(),
            new NotificationService(log),
            log);
        _language = new(_solutions, capacity: 8, workerCount: 2);
        _uri = _paths.ToDocumentUri(_path);
    }

    [Fact]
    public async Task UnsavedReplica_DrivesProjectAwareCompletionAndLazyDetails()
    {
        const string source = "internal sealed class Shared { void M() { string.Empt } }";
        await OpenWithReplicaAsync(source, 7);
        var request = Request("completion", 7, source.IndexOf("Empt", StringComparison.Ordinal) + 4, isExplicit: true);

        var completion = await _language.GetCompletionsAsync(request, TestContext.Current.CancellationToken);
        var empty = Assert.Single(completion!.Items, item => item.Label == "Empty");
        Assert.Contains(completion.Items, item => item.Label == "foreach" && item.IsSnippet);
        var details = await _language.ResolveCompletionAsync(
            new(new LanguageRequest(
                "resolve", request.DocumentUri, request.ProjectContextId, request.SourceVersion, request.Sequence, request.Position),
                empty.Id),
            TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Contains("Empty", details.Documentation ?? details.InsertText, StringComparison.Ordinal);
        Assert.True(_language.Metrics.Completed >= 2);
    }

    [Fact]
    public async Task SignatureHoverFormattingAndSemanticTokens_UseExactReplica()
    {
        const string source = "/// <summary>A shared value.</summary>\ninternal sealed class Shared{void M(){string.Concat(\"a\",\"b\");}}";
        await OpenWithReplicaAsync(source, 11);

        var signature = await _language.GetSignatureHelpAsync(
            Request("signature", 11, source.IndexOf("\",\"", StringComparison.Ordinal) + 2, trigger: ","),
            TestContext.Current.CancellationToken);
        var hover = await _language.GetHoverAsync(
            Request("hover", 11, source.LastIndexOf("Concat", StringComparison.Ordinal) + 1),
            TestContext.Current.CancellationToken);
        var format = await _language.FormatAsync(
            Request("format", 11, 0, rangeStart: 0, rangeEnd: source.Length),
            TestContext.Current.CancellationToken);
        var semantics = await _language.GetSemanticTokensAsync(
            Request("semantic", 11, 0, rangeStart: 0, rangeEnd: source.Length),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(signature!.Signatures);
        Assert.Equal(1, signature.ActiveParameter);
        Assert.Contains("Concat", hover!.Signature, StringComparison.Ordinal);
        Assert.NotEmpty(format!.Edits);
        Assert.Contains(semantics!.Tokens, token => token.Type == "class");
        Assert.Contains(semantics.Tokens, token => token.Type == "method");
    }

    [Fact]
    public async Task StaleSequence_IsRejectedWithoutPublishingResults()
    {
        const string oldSource = "internal sealed class Shared { void M() { Str } }";
        await OpenWithReplicaAsync(oldSource, 3);
        const string currentSource = "internal sealed class Shared { void M() { string.Empty.ToString(); } }";
        _solutions.QueueReplica(new(_uri, _path, new DocumentReplica(currentSource, 4, 4), 4));
        await _solutions.WaitForReplicaAsync(_uri, 4, TestContext.Current.CancellationToken);

        var result = await _language.GetCompletionsAsync(
            Request("stale", 3, oldSource.IndexOf("Str", StringComparison.Ordinal) + 3, isExplicit: true),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, _language.Metrics.RejectedStale);
    }

    [Fact]
    public async Task SupersededSemanticRequest_IsCanceledAndAdmissionRemainsBounded()
    {
        var source = "internal sealed class Shared {" + string.Concat(
            Enumerable.Range(0, 5_000).Select(index => $"int Value{index};")) + "}";
        await OpenWithReplicaAsync(source, 12);
        var first = _language.GetSemanticTokensAsync(
            Request("semantic-old", 12, 0, rangeStart: 0, rangeEnd: source.Length),
            TestContext.Current.CancellationToken);
        var current = _language.GetSemanticTokensAsync(
            Request("semantic-current", 12, 0, rangeStart: 0, rangeEnd: source.Length),
            TestContext.Current.CancellationToken);

        Assert.Null(await first);
        Assert.NotNull(await current);
        Assert.True(_language.Metrics.Canceled >= 1);
        Assert.InRange(_language.Metrics.MaximumPending, 1, _language.Metrics.Capacity);
    }

    [Fact]
    public void PublicContracts_DoNotExposeRoslynTypes()
    {
        Type[] contracts = [
            typeof(LanguageRequest), typeof(LanguageTextEdit), typeof(LanguageCompletionItem),
            typeof(LanguageCompletionList), typeof(LanguageCompletionDetails), typeof(LanguageCompletionResolveRequest),
            typeof(LanguageSignature), typeof(LanguageSignatureHelp), typeof(LanguageHover),
            typeof(LanguageFormatResult), typeof(LanguageSemanticTokens), typeof(LanguageServiceMetrics),
        ];
        Assert.DoesNotContain(contracts.SelectMany(type => type.GetProperties()), property =>
            property.PropertyType.FullName?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true);
    }

    private async Task OpenWithReplicaAsync(string source, long sequence)
    {
        await _solutions.OpenAsync(
            Path.Combine(Path.GetDirectoryName(_path)!, "Workspace.slnx"),
            TestContext.Current.CancellationToken);
        _solutions.QueueReplica(new(_uri, _path, new DocumentReplica(source, sequence, sequence), sequence));
        await _solutions.WaitForReplicaAsync(_uri, sequence, TestContext.Current.CancellationToken);
    }

    private LanguageRequest Request(
        string id,
        long sequence,
        int position,
        string? trigger = null,
        bool isExplicit = false,
        int? rangeStart = null,
        int? rangeEnd = null)
    {
        var context = _solutions.GetDocumentContexts(_uri).Single(candidate => candidate.IsActive);
        return new(
            id,
            _uri.AbsoluteUri,
            context.ProjectId.Id.ToString(),
            _solutions.Snapshot.SourceVersion,
            sequence,
            position,
            rangeStart,
            rangeEnd,
            trigger,
            isExplicit);
    }

    public async ValueTask DisposeAsync()
    {
        await _language.DisposeAsync();
        await _solutions.DisposeAsync();
        await _solutionWork.DisposeAsync();
    }
}
