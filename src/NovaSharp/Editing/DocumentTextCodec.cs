using System.Text;
using NovaSharp.Text;

namespace NovaSharp.Editing;

/// <summary>The result of turning a file's bytes into the text Monaco is given.</summary>
/// <param name="Text">The text, with every line ending rewritten as the editor sequence for <paramref name="LineEndings"/>.</param>
/// <param name="Encoding">The encoding the bytes were actually decoded with.</param>
/// <param name="LineEndings">What the file's line endings were before normalization.</param>
/// <param name="DecodedWithFallback">
/// Whether the requested encoding failed and the byte-preserving fallback was used instead. The workbench says so
/// rather than leaving the user to notice.
/// </param>
public sealed record DecodedDocument(
    string Text,
    TextEncodingProfile Encoding,
    LineEndingReport LineEndings,
    bool DecodedWithFallback);

/// <summary>
/// Converts between a file's bytes and the text in the editor, in both directions, losslessly or not at all.
/// </summary>
/// <remarks>
/// The two conversions are exact inverses given the same encoding and line ending, which is what makes a save safe:
/// text that was decoded from a file and never edited encodes back to the same bytes. See ADR 0002.
/// </remarks>
public sealed class DocumentTextCodec
{
    /// <summary>The encoding used for bytes that no other encoding accepted.</summary>
    /// <remarks>
    /// Every byte value maps to a distinct character, so the document round-trips exactly even when it reads as
    /// nonsense. Decoding with replacement characters instead would produce a buffer that cannot be saved back over
    /// its own file without destroying data.
    /// </remarks>
    public TextEncodingProfile Fallback { get; init; } = TextEncodings.Latin1;

    /// <summary>Decodes <paramref name="bytes"/>, preferring <paramref name="preferred"/> when no mark says otherwise.</summary>
    public DecodedDocument Decode(
        ReadOnlySpan<byte> bytes,
        TextEncodingProfile preferred,
        LineEndingStyle defaultLineEnding)
    {
        ArgumentNullException.ThrowIfNull(preferred);

        var marked = TextEncodings.DetectByteOrderMark(bytes, out var markLength);
        var content = bytes[markLength..];

        // A byte-order mark is a statement about the file, so it outranks the preferred encoding. It is still only
        // tried, not trusted: a file can carry a mark and then not be that encoding.
        if (marked is not null && TryDecode(content, marked, out var markedText))
        {
            return Report(markedText, marked, fallback: false, defaultLineEnding);
        }

        if (marked is null && TryDecode(content, preferred, out var preferredText))
        {
            return Report(preferredText, preferred, fallback: false, defaultLineEnding);
        }

        // Nothing accepted these bytes. The fallback cannot fail, and the caller is told it was used.
        var fallbackText = Fallback.CreateEncoding().GetString(bytes);
        return Report(fallbackText, Fallback, fallback: true, defaultLineEnding);
    }

    /// <summary>Encodes the editor's text back into the bytes a file should hold.</summary>
    /// <exception cref="EncoderFallbackException">
    /// <paramref name="encoding"/> cannot represent the text. Thrown rather than substituting characters, so a save
    /// never silently rewrites what the user typed.
    /// </exception>
    public byte[] Encode(string editorText, TextEncodingProfile encoding, LineEndingStyle lineEnding)
    {
        ArgumentNullException.ThrowIfNull(editorText);
        ArgumentNullException.ThrowIfNull(encoding);

        var stored = NovaSharp.Text.LineEndings.Normalize(editorText, lineEnding.ToStorageSequence());
        var preamble = encoding.Preamble.Span;
        var body = encoding.CreateEncoding().GetBytes(stored);

        if (preamble.IsEmpty)
        {
            return body;
        }

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result);
        body.CopyTo(result.AsSpan(preamble.Length));
        return result;
    }

    private static DecodedDocument Report(
        string text,
        TextEncodingProfile encoding,
        bool fallback,
        LineEndingStyle defaultLineEnding)
    {
        var report = NovaSharp.Text.LineEndings.Detect(text, defaultLineEnding);
        var normalized = NovaSharp.Text.LineEndings.Normalize(text, report.Style.ToEditorSequence());
        return new DecodedDocument(normalized, encoding, report, fallback);
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, TextEncodingProfile profile, out string text)
    {
        try
        {
            text = profile.CreateEncoding().GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
