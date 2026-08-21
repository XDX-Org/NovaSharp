using System.Text;
using Microsoft.AspNetCore.Components;
using NovaSharp.Commands;
using NovaSharp.Editing;

namespace NovaSharp.Tests;

/// <summary>
/// A stand-in for Monaco that keeps the same two version counters and pushes the same edit batches.
/// </summary>
/// <remarks>
/// It replicates the behaviour the session depends on — a version identifier that only ever increases, an alternative
/// identifier that returns to an earlier value on undo, and batches whose offsets refer to the text before the batch —
/// so a session test exercises the real ordering rather than a simplification of it.
/// </remarks>
internal sealed class FakeEditorHost : IEditorHost
{
    private readonly StringBuilder _text = new();
    private EditorBridge? _bridge;

    public long Sequence { get; private set; }

    public long AlternativeSequence { get; private set; }

    public string Text => _text.ToString();

    public bool IsReadOnly { get; private set; }

    public int SnapshotCount { get; private set; }

    /// <summary>When set, batches are held here instead of being sent, so a save barrier can be observed waiting.</summary>
    public List<TextEditBatch>? Held { get; set; }

    public ValueTask InitializeAsync(ElementReference container, EditorBridge bridge, CancellationToken cancellationToken)
    {
        _bridge = bridge;
        return ValueTask.CompletedTask;
    }

    public ValueTask<EditorSequence> OpenDocumentAsync(DocumentContent content, CancellationToken cancellationToken)
    {
        _text.Clear();
        _text.Append(content.Text);
        Sequence = 1;
        AlternativeSequence = 1;
        IsReadOnly = content.ReadOnly;
        return ValueTask.FromResult(new EditorSequence(Sequence, AlternativeSequence));
    }

    public ValueTask<EditorSequence> ReplaceDocumentAsync(string text, string lineEnding, CancellationToken cancellationToken)
    {
        _text.Clear();
        _text.Append(text);
        Sequence++;
        AlternativeSequence++;
        return ValueTask.FromResult(new EditorSequence(Sequence, AlternativeSequence));
    }

    public ValueTask<DocumentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        SnapshotCount++;
        return ValueTask.FromResult(new DocumentSnapshot(Text, Sequence, AlternativeSequence));
    }

    public ValueTask<EditorSequence> GetSequenceAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EditorSequence(Sequence, AlternativeSequence));

    public ValueTask SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken)
    {
        IsReadOnly = readOnly;
        return ValueTask.CompletedTask;
    }

    /// <summary>The commands the editor was last told to bind.</summary>
    public IReadOnlyList<CommandDescriptor> RegisteredCommands { get; private set; } = [];

    /// <summary>Whether a comparison is open, and what it is showing.</summary>
    public string? ComparingAgainst { get; private set; }

    public ValueTask<IReadOnlyList<string>> RegisterCommandsAsync(
        IReadOnlyList<CommandDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        RegisteredCommands = descriptors;
        return ValueTask.FromResult<IReadOnlyList<string>>([]);
    }

    public ValueTask BeginCompareAsync(
        ElementReference diffContainer,
        string originalText,
        CancellationToken cancellationToken)
    {
        ComparingAgainst = originalText;
        return ValueTask.CompletedTask;
    }

    public ValueTask EndCompareAsync(CancellationToken cancellationToken)
    {
        ComparingAgainst = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask<EditorRuntimeInfo> GetRuntimeInfoAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EditorRuntimeInfo("test", DedicatedWorker: true, ModelCount: 1, ExternalRequestCount: 0));

    /// <summary>Edits the text the way a user would, and replicates the batch the way Monaco would.</summary>
    public void Edit(int start, int end, string replacement)
    {
        _text.Remove(start, end - start).Insert(start, replacement);
        Sequence++;
        AlternativeSequence++;
        Send(new TextEditBatch(
            "file:///fake",
            Sequence - 1,
            Sequence,
            AlternativeSequence,
            EditOrigins.User,
            [new TextEdit(start, end, replacement)]));
    }

    /// <summary>Types at the end of the document.</summary>
    public void Type(string text) => Edit(_text.Length, _text.Length, text);

    /// <summary>
    /// Undoes back to the state <paramref name="alternativeSequence"/> names.
    /// </summary>
    /// <remarks>
    /// The version identifier still moves forward — an undo is another change — while the alternative identifier
    /// returns to what it was, which is exactly why dirty state is compared against the alternative.
    /// </remarks>
    public void UndoTo(int start, int end, string replacement, long alternativeSequence)
    {
        _text.Remove(start, end - start).Insert(start, replacement);
        Sequence++;
        AlternativeSequence = alternativeSequence;
        Send(new TextEditBatch(
            "file:///fake",
            Sequence - 1,
            Sequence,
            AlternativeSequence,
            EditOrigins.User,
            [new TextEdit(start, end, replacement)]));
    }

    /// <summary>Sends everything that was held back, in the order it happened.</summary>
    public void ReleaseHeld()
    {
        var held = Held;
        Held = null;

        if (held is null)
        {
            return;
        }

        foreach (var batch in held)
        {
            _bridge?.ReplicateEdits([batch]);
        }
    }

    private void Send(TextEditBatch batch)
    {
        if (Held is not null)
        {
            Held.Add(batch);
            return;
        }

        _bridge?.ReplicateEdits([batch]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A watcher whose notifications the test decides on.</summary>
internal sealed class FakeDocumentWatcher : IDocumentWatcher
{
    public string? Watching { get; private set; }

    public event Action? Changed;

    public void Watch(string path) => Watching = path;

    public void Stop() => Watching = null;

    public void Notify() => Changed?.Invoke();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
