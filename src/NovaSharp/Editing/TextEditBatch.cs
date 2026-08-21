namespace NovaSharp.Editing;

/// <summary>
/// One replacement inside a document, expressed in UTF-16 code units against the text as it was before the batch was
/// applied.
/// </summary>
/// <remarks>
/// Code-unit offsets are Monaco's own unit. Converting to runes or grapheme clusters at this boundary would mean two
/// different notions of position in the same protocol, which is how a surrogate pair ends up split.
/// </remarks>
/// <param name="Start">The first code unit replaced.</param>
/// <param name="End">One past the last code unit replaced. Equal to <paramref name="Start"/> for an insertion.</param>
/// <param name="Text">The replacement text. Empty for a deletion.</param>
public readonly record struct TextEdit(int Start, int End, string Text);

/// <summary>Why a batch could not be applied to a replica.</summary>
public enum TextEditBatchProblem
{
    /// <summary>The batch is well formed.</summary>
    None,

    /// <summary>An edit's offsets are reversed, negative, or past the end of the text.</summary>
    OffsetsOutOfRange,

    /// <summary>The edits are not in ascending order, or two of them overlap.</summary>
    OutOfOrder,

    /// <summary>The batch does not continue from the sequence the replica is at.</summary>
    SequenceGap,

    /// <summary>The batch does not advance the sequence.</summary>
    SequenceNotAdvancing,
}

/// <summary>
/// An ordered group of edits taking a document from one sequence to the next.
/// </summary>
/// <remarks>
/// This is the unit of replication described by ADR 0001 and the shape a durable edit journal would persist, which is
/// why it carries its own identity rather than relying on the order it happened to arrive in.
/// </remarks>
/// <param name="DocumentUri">The canonical document this batch belongs to.</param>
/// <param name="BaseSequence">The sequence the document was at before these edits.</param>
/// <param name="ResultSequence">The sequence the document is at after them.</param>
/// <param name="AlternativeSequence">
/// Monaco's alternative version identifier after the batch. It returns to an earlier value when the user undoes back
/// to a previous state, which is what makes it — and not <paramref name="ResultSequence"/> — the right thing to
/// compare against the last saved state.
/// </param>
/// <param name="Origin">Who produced the edits: see <see cref="EditOrigins"/>.</param>
/// <param name="Edits">The edits, in ascending, non-overlapping offset order.</param>
public sealed record TextEditBatch(
    string DocumentUri,
    long BaseSequence,
    long ResultSequence,
    long AlternativeSequence,
    string Origin,
    IReadOnlyList<TextEdit> Edits)
{
    /// <summary>Checks the batch against <paramref name="currentSequence"/> and <paramref name="textLength"/>.</summary>
    public TextEditBatchProblem Validate(long currentSequence, int textLength)
    {
        if (BaseSequence != currentSequence)
        {
            return TextEditBatchProblem.SequenceGap;
        }

        if (ResultSequence <= BaseSequence)
        {
            return TextEditBatchProblem.SequenceNotAdvancing;
        }

        var previousEnd = 0;
        foreach (var edit in Edits)
        {
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > textLength)
            {
                return TextEditBatchProblem.OffsetsOutOfRange;
            }

            if (edit.Start < previousEnd)
            {
                return TextEditBatchProblem.OutOfOrder;
            }

            previousEnd = edit.End;
        }

        return TextEditBatchProblem.None;
    }
}

/// <summary>The origins a <see cref="TextEditBatch"/> can carry.</summary>
/// <remarks>
/// Every batch is replicated whatever its origin — the shadow has to stay correct — but a change NovaSharp itself
/// caused is not a change the user made, and the workbench states it differently.
/// </remarks>
public static class EditOrigins
{
    /// <summary>Typing, pasting, undo, redo, and Monaco's own editing commands.</summary>
    public const string User = "user";

    /// <summary>An edit NovaSharp pushed into Monaco, such as a reload.</summary>
    public const string NovaSharp = "novasharp";
}
