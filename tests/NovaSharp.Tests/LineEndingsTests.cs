using NovaSharp.Text;
using Xunit;

namespace NovaSharp.Tests;

public sealed class LineEndingsTests
{
    [Theory]
    [InlineData("a\nb\nc", LineEndingStyle.Lf, false)]
    [InlineData("a\r\nb\r\nc", LineEndingStyle.CrLf, false)]
    [InlineData("a\rb\rc", LineEndingStyle.Cr, false)]
    [InlineData("a\r\nb\nc\nd", LineEndingStyle.Lf, true)]
    [InlineData("a\r\nb\r\nc\nd", LineEndingStyle.CrLf, true)]
    public void Detect_TakesTheDominantEnding(string text, LineEndingStyle expected, bool mixed)
    {
        var report = LineEndings.Detect(text, LineEndingStyle.CrLf);

        Assert.Equal(expected, report.Style);
        Assert.Equal(mixed, report.IsMixed);
    }

    [Fact]
    public void Detect_FallsBackWhenTheFileHasNoOpinion()
    {
        // No line break at all, and an exact tie, are both cases where the file says nothing. Guessing either way
        // would silently rewrite the other half of the file on the next save.
        Assert.Equal(LineEndingStyle.CrLf, LineEndings.Detect("class Widget;", LineEndingStyle.CrLf).Style);
        Assert.Equal(LineEndingStyle.Cr, LineEndings.Detect("a\nb\r\nc", LineEndingStyle.Cr).Style);
    }

    [Fact]
    public void Detect_CountsCarriageReturnPairsAsOneEnding()
    {
        var report = LineEndings.Detect("a\r\nb", LineEndingStyle.Lf);

        Assert.Equal(1, report.CrLfCount);
        Assert.Equal(0, report.CrCount);
        Assert.Equal(0, report.LfCount);
    }

    [Theory]
    [InlineData("a\r\nb\rc\nd", "\n", "a\nb\nc\nd")]
    [InlineData("a\nb", "\r\n", "a\r\nb")]
    [InlineData("a\r\nb", "\r", "a\rb")]
    public void Normalize_RewritesEveryEnding(string text, string sequence, string expected)
    {
        Assert.Equal(expected, LineEndings.Normalize(text, sequence));
    }

    [Fact]
    public void Normalize_ReturnsTheSameInstanceWhenNothingChanges()
    {
        const string text = "a\nb\nc";

        Assert.Same(text, LineEndings.Normalize(text, "\n"));
    }

    [Fact]
    public void EditorSequence_NeverAsksMonacoForACarriageReturnOnlyDocument()
    {
        // Monaco represents a line feed or a carriage-return pair and nothing else, so a carriage-return document is
        // edited as line feeds and converted back only when it is written.
        Assert.Equal("\n", LineEndingStyle.Cr.ToEditorSequence());
        Assert.Equal("\r", LineEndingStyle.Cr.ToStorageSequence());
        Assert.Equal("\r\n", LineEndingStyle.CrLf.ToEditorSequence());
        Assert.Equal("\r\n", LineEndingStyle.CrLf.ToStorageSequence());
    }
}
