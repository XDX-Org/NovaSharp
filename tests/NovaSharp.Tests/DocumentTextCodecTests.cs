using System.Text;
using NovaSharp.Editing;
using NovaSharp.Text;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentTextCodecTests
{
    private readonly DocumentTextCodec _codec = new();

    [Fact]
    public void Decode_KeepsTextThatIsAlreadyUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("// naïve — 𝄞\nclass Widget;\n");

        var decoded = _codec.Decode(bytes, TextEncodings.Utf8, LineEndingStyle.Lf);

        Assert.Equal("// naïve — 𝄞\nclass Widget;\n", decoded.Text);
        Assert.Equal(TextEncodings.Utf8, decoded.Encoding);
        Assert.False(decoded.DecodedWithFallback);
    }

    [Fact]
    public void Decode_LetsAByteOrderMarkOutrankThePreferredEncoding()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("class Widget;")).ToArray();

        var decoded = _codec.Decode(bytes, TextEncodings.Latin1, LineEndingStyle.Lf);

        Assert.Equal("class Widget;", decoded.Text);
        Assert.True(decoded.Encoding.ByteOrderMark);
        Assert.Equal(65001, decoded.Encoding.CodePage);
    }

    [Fact]
    public void Decode_ReadsUtf16ThroughItsMark()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("class Widget;")).ToArray();

        var decoded = _codec.Decode(bytes, TextEncodings.Utf8, LineEndingStyle.Lf);

        Assert.Equal("class Widget;", decoded.Text);
        Assert.Equal(1200, decoded.Encoding.CodePage);
    }

    [Fact]
    public void Decode_FallsBackWithoutLosingAByte()
    {
        // 0x80 is not valid UTF-8. Decoding it with replacement characters would produce a buffer that cannot be
        // written back over its own file, so the fallback that round-trips every byte is used instead and reported.
        byte[] bytes = [0x41, 0x80, 0x42];

        var decoded = _codec.Decode(bytes, TextEncodings.Utf8, LineEndingStyle.Lf);

        Assert.True(decoded.DecodedWithFallback);
        Assert.DoesNotContain('�', decoded.Text);
        Assert.Equal(bytes, _codec.Encode(decoded.Text, decoded.Encoding, decoded.LineEndings.Style));
    }

    [Theory]
    [InlineData("a\r\nb\r\n", LineEndingStyle.CrLf, "a\r\nb\r\n")]
    [InlineData("a\nb\n", LineEndingStyle.Lf, "a\nb\n")]
    [InlineData("a\rb\r", LineEndingStyle.Cr, "a\nb\n")]
    public void Decode_GivesMonacoAnEndingItCanRepresent(string file, LineEndingStyle expected, string editorText)
    {
        var decoded = _codec.Decode(Encoding.UTF8.GetBytes(file), TextEncodings.Utf8, LineEndingStyle.Lf);

        Assert.Equal(expected, decoded.LineEndings.Style);
        Assert.Equal(editorText, decoded.Text);
    }

    [Theory]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\nb\n")]
    [InlineData("a\rb\r")]
    public void EncodeAfterDecode_ReproducesTheFileExactly(string file)
    {
        var bytes = Encoding.UTF8.GetBytes(file);

        var decoded = _codec.Decode(bytes, TextEncodings.Utf8, LineEndingStyle.Lf);
        var written = _codec.Encode(decoded.Text, decoded.Encoding, decoded.LineEndings.Style);

        Assert.Equal(bytes, written);
    }

    [Fact]
    public void EncodeAfterDecode_ReproducesAByteOrderMark()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("class Widget;\n")).ToArray();

        var decoded = _codec.Decode(bytes, TextEncodings.Utf8, LineEndingStyle.Lf);

        Assert.Equal(bytes, _codec.Encode(decoded.Text, decoded.Encoding, decoded.LineEndings.Style));
    }

    [Fact]
    public void Encode_RefusesRatherThanWritingSomethingElse()
    {
        Assert.Throws<EncoderFallbackException>(
            () => _codec.Encode("class Widget; // 𝄞", TextEncodings.Latin1, LineEndingStyle.Lf));
    }

    [Fact]
    public void Decode_NormalizesMixedEndingsToTheDominantOne()
    {
        var decoded = _codec.Decode(
            Encoding.UTF8.GetBytes("a\r\nb\r\nc\nd"),
            TextEncodings.Utf8,
            LineEndingStyle.Lf);

        Assert.True(decoded.LineEndings.IsMixed);
        Assert.Equal("a\r\nb\r\nc\r\nd", decoded.Text);
    }
}
