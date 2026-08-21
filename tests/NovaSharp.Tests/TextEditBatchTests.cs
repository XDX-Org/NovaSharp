using System.Text.Json;
using NovaSharp.Editing;
using Xunit;

namespace NovaSharp.Tests;

public sealed class TextEditBatchTests
{
    private static TextEditBatch Batch(long baseSequence, long result, params TextEdit[] edits) =>
        new("file:///widget.cs", baseSequence, result, result, EditOrigins.User, edits);

    [Fact]
    public void Validate_AcceptsAscendingNonOverlappingEdits()
    {
        var batch = Batch(1, 2, new TextEdit(0, 2, "x"), new TextEdit(2, 4, "y"), new TextEdit(6, 6, "z"));

        Assert.Equal(TextEditBatchProblem.None, batch.Validate(currentSequence: 1, textLength: 10));
    }

    [Fact]
    public void Validate_RejectsEditsThatArriveOutOfOrder()
    {
        var batch = Batch(1, 2, new TextEdit(4, 6, "y"), new TextEdit(0, 2, "x"));

        Assert.Equal(TextEditBatchProblem.OutOfOrder, batch.Validate(currentSequence: 1, textLength: 10));
    }

    [Fact]
    public void Validate_RejectsOverlappingEdits()
    {
        var batch = Batch(1, 2, new TextEdit(0, 4, "x"), new TextEdit(2, 6, "y"));

        Assert.Equal(TextEditBatchProblem.OutOfOrder, batch.Validate(currentSequence: 1, textLength: 10));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(4, 2)]
    [InlineData(0, 11)]
    public void Validate_RejectsOffsetsOutsideTheText(int start, int end)
    {
        var batch = Batch(1, 2, new TextEdit(start, end, "x"));

        Assert.Equal(TextEditBatchProblem.OffsetsOutOfRange, batch.Validate(currentSequence: 1, textLength: 10));
    }

    [Fact]
    public void Validate_RejectsABatchThatDoesNotContinueFromHere()
    {
        var batch = Batch(5, 6, new TextEdit(0, 0, "x"));

        Assert.Equal(TextEditBatchProblem.SequenceGap, batch.Validate(currentSequence: 3, textLength: 10));
    }

    [Fact]
    public void Validate_RejectsABatchThatDoesNotAdvance()
    {
        var batch = Batch(3, 3, new TextEdit(0, 0, "x"));

        Assert.Equal(TextEditBatchProblem.SequenceNotAdvancing, batch.Validate(currentSequence: 3, textLength: 10));
    }

    [Fact]
    public void WireFormat_MatchesWhatTheEditorHostSends()
    {
        // The JavaScript side builds this object literally. Interop uses the web defaults, so the contract is the
        // camel-cased property names; a rename on either side has to fail here rather than at runtime in the browser.
        const string json = """
            {
              "documentUri": "file:///widget.cs",
              "baseSequence": 4,
              "resultSequence": 5,
              "alternativeSequence": 5,
              "origin": "user",
              "edits": [ { "start": 2, "end": 4, "text": "ab" } ]
            }
            """;

        var batch = JsonSerializer.Deserialize<TextEditBatch>(json, JsonSerializerOptions.Web);

        Assert.NotNull(batch);
        Assert.Equal("file:///widget.cs", batch.DocumentUri);
        Assert.Equal(4, batch.BaseSequence);
        Assert.Equal(5, batch.ResultSequence);
        Assert.Equal(EditOrigins.User, batch.Origin);
        Assert.Equal(new TextEdit(2, 4, "ab"), Assert.Single(batch.Edits));
    }
}
