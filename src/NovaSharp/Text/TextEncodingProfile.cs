using System.Text;

namespace NovaSharp.Text;

/// <summary>
/// One encoding NovaSharp can read and write a document with, together with whether it carries a byte-order mark.
/// </summary>
/// <remarks>
/// Instances are only obtainable from <see cref="TextEncodings"/>, which is what guarantees the framework's code-page
/// provider has been registered before <see cref="CreateEncoding"/> asks for a code page it supplies.
/// </remarks>
/// <param name="Id">The stable identifier used in settings and persisted document state, such as <c>windows-1252</c>.</param>
/// <param name="DisplayName">The name shown in the workbench.</param>
/// <param name="CodePage">The framework code page backing this encoding.</param>
/// <param name="ByteOrderMark">Whether a document written with this profile begins with the encoding's preamble.</param>
public sealed record TextEncodingProfile(string Id, string DisplayName, int CodePage, bool ByteOrderMark)
{
    /// <summary>
    /// Returns the framework encoding for this profile, configured to throw rather than substitute characters.
    /// </summary>
    /// <remarks>
    /// Exception fallbacks are the whole point. A decoder that substitutes U+FFFD produces a buffer that cannot be
    /// written back over its own file without destroying data, and an encoder that best-fits produces a save that
    /// silently changes the user's text. Both are reported instead.
    /// </remarks>
    public Encoding CreateEncoding() =>
        Encoding.GetEncoding(CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>The bytes a document written with this profile starts with; empty when it carries no mark.</summary>
    public ReadOnlyMemory<byte> Preamble =>
        ByteOrderMark ? Encoding.GetEncoding(CodePage).GetPreamble() : ReadOnlyMemory<byte>.Empty;

    /// <summary>Returns whether <paramref name="text"/> can be written with this encoding without losing anything.</summary>
    /// <remarks>
    /// Offered choices are marked with this before the user picks one, so a lossy conversion is a warning rather than a
    /// discovery made after the file has been overwritten.
    /// </remarks>
    public bool CanRepresent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            CreateEncoding().GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Returns the first character of <paramref name="text"/> this encoding cannot write, if any.</summary>
    /// <remarks>Used to name the offending character when a conversion is refused, rather than only refusing it.</remarks>
    public Rune? FindUnrepresentableRune(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var encoding = CreateEncoding();
        foreach (var rune in text.EnumerateRunes())
        {
            try
            {
                encoding.GetByteCount(rune.ToString());
            }
            catch (EncoderFallbackException)
            {
                return rune;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public override string ToString() => Id;
}
