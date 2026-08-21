using System.Text;
using NovaSharp.Text;
using Xunit;

namespace NovaSharp.Tests;

public sealed class TextEncodingsTests
{
    [Fact]
    public void Catalog_OffersTheEncodingsNovaSharpNamesElsewhere()
    {
        Assert.NotEmpty(TextEncodings.All);

        // These three are referenced directly by the codec and by ADR 0002, so their absence is a broken product
        // rather than a platform difference.
        Assert.NotNull(TextEncodings.Find("utf-8"));
        Assert.NotNull(TextEncodings.Find("utf-8-bom"));
        Assert.NotNull(TextEncodings.Find("iso-8859-1"));
    }

    [Fact]
    public void Catalog_OffersEveryFavouriteOrNoneAtAll()
    {
        // A favourite that does not resolve would be an empty row in the menu. Which code pages the framework supplies
        // can differ, so this asserts the list is coherent rather than that any particular entry exists.
        foreach (var id in TextEncodings.Favorites)
        {
            var profile = TextEncodings.Find(id);
            if (profile is not null)
            {
                Assert.Equal(id, profile.Id, ignoreCase: true);
            }
        }
    }

    [Fact]
    public void Catalog_DistinguishesTheByteOrderMarkVariant()
    {
        var plain = TextEncodings.Utf8;
        var marked = TextEncodings.Utf8ByteOrderMark;

        Assert.Equal(plain.CodePage, marked.CodePage);
        Assert.False(plain.ByteOrderMark);
        Assert.True(marked.ByteOrderMark);
        Assert.Empty(plain.Preamble.ToArray());
        Assert.Equal([0xEF, 0xBB, 0xBF], marked.Preamble.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF }, 3, 65001)]
    [InlineData(new byte[] { 0xFF, 0xFE }, 2, 1200)]
    [InlineData(new byte[] { 0xFE, 0xFF }, 2, 1201)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, 4, 12000)]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, 4, 12001)]
    public void DetectByteOrderMark_RecognizesEveryMarkItClaimsTo(byte[] mark, int expectedLength, int expectedCodePage)
    {
        var profile = TextEncodings.DetectByteOrderMark(mark, out var length);

        Assert.NotNull(profile);
        Assert.Equal(expectedLength, length);
        Assert.Equal(expectedCodePage, profile.CodePage);
        Assert.True(profile.ByteOrderMark);
    }

    [Fact]
    public void DetectByteOrderMark_PrefersTheLongerMark()
    {
        // A UTF-32 little-endian mark begins with the whole of the UTF-16 little-endian mark. Testing the shorter one
        // first would read every UTF-32 file as UTF-16 followed by a null character.
        var profile = TextEncodings.DetectByteOrderMark([0xFF, 0xFE, 0x00, 0x00, 0x41], out var length);

        Assert.Equal(12000, profile?.CodePage);
        Assert.Equal(4, length);
    }

    [Fact]
    public void DetectByteOrderMark_ReportsNothingForUnmarkedBytes()
    {
        Assert.Null(TextEncodings.DetectByteOrderMark("class Widget;"u8, out var length));
        Assert.Equal(0, length);
    }

    [Fact]
    public void CanRepresent_AnswersForTheDocumentsCharacters()
    {
        Assert.True(TextEncodings.Utf8.CanRepresent("naïve — 𝄞"));
        Assert.True(TextEncodings.Latin1.CanRepresent("naïve"));
        Assert.False(TextEncodings.Latin1.CanRepresent("naïve — 𝄞"));
    }

    [Fact]
    public void FindUnrepresentableRune_NamesTheCharacterThatWouldBeLost()
    {
        var rune = TextEncodings.Latin1.FindUnrepresentableRune("class Widget; // 𝄞");

        Assert.Equal(new Rune(0x1D11E), rune);
    }

    [Fact]
    public void CreateEncoding_RefusesRatherThanSubstituting()
    {
        var encoding = TextEncodings.Latin1.CreateEncoding();

        Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("𝄞"));
    }
}
