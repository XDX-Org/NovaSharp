using System.Text;

namespace NovaSharp.Text;

/// <summary>The line ending a document is stored with.</summary>
public enum LineEndingStyle
{
    /// <summary>A single line feed.</summary>
    Lf,

    /// <summary>A carriage return followed by a line feed.</summary>
    CrLf,

    /// <summary>A single carriage return.</summary>
    Cr,
}

/// <summary>What was found when a document's line endings were counted.</summary>
/// <param name="Style">The ending the document is treated as having, and the one a save writes.</param>
/// <param name="IsMixed">Whether more than one kind of ending was present.</param>
/// <param name="LfCount">Lone line feeds.</param>
/// <param name="CrLfCount">Carriage-return/line-feed pairs.</param>
/// <param name="CrCount">Lone carriage returns.</param>
public sealed record LineEndingReport(LineEndingStyle Style, bool IsMixed, int LfCount, int CrLfCount, int CrCount);

/// <summary>
/// Detects, normalizes, and reproduces document line endings.
/// </summary>
/// <remarks>
/// Two sequences matter and they are not always the same one. <see cref="ToStorageSequence"/> is what a save writes.
/// <see cref="ToEditorSequence"/> is what Monaco is given, and Monaco can only represent a line feed or a
/// carriage-return pair. A carriage-return-only document is therefore edited as line feeds and written back as
/// carriage returns; the conversion happens at the two boundaries and nowhere in between, so offsets in the replica
/// always mean exactly what they mean in Monaco.
/// </remarks>
public static class LineEndings
{
    /// <summary>Returns the sequence a save writes for <paramref name="style"/>.</summary>
    public static string ToStorageSequence(this LineEndingStyle style) => style switch
    {
        LineEndingStyle.Lf => "\n",
        LineEndingStyle.CrLf => "\r\n",
        LineEndingStyle.Cr => "\r",
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };

    /// <summary>Returns the sequence Monaco is given for <paramref name="style"/>.</summary>
    public static string ToEditorSequence(this LineEndingStyle style) =>
        style == LineEndingStyle.CrLf ? "\r\n" : "\n";

    /// <summary>Counts the endings in <paramref name="text"/> and decides which one the document has.</summary>
    /// <param name="text">The decoded document text.</param>
    /// <param name="fallback">The ending used when the text contains no line break at all.</param>
    /// <remarks>
    /// The most common ending wins, and ties fall to the fallback rather than to a hard-coded preference, because a
    /// tie is exactly the case where the file has no opinion and the user's configured default should be honoured.
    /// </remarks>
    public static LineEndingReport Detect(string text, LineEndingStyle fallback)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lf = 0;
        var crlf = 0;
        var cr = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    crlf++;
                    i++;
                    break;
                case '\r':
                    cr++;
                    break;
                case '\n':
                    lf++;
                    break;
            }
        }

        var kinds = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var style = fallback;

        if (kinds > 0)
        {
            var highest = Math.Max(lf, Math.Max(crlf, cr));
            var winners = (lf == highest ? 1 : 0) + (crlf == highest ? 1 : 0) + (cr == highest ? 1 : 0);

            style = winners > 1
                ? fallback
                : lf == highest ? LineEndingStyle.Lf
                : crlf == highest ? LineEndingStyle.CrLf
                : LineEndingStyle.Cr;
        }

        return new LineEndingReport(style, kinds > 1, lf, crlf, cr);
    }

    /// <summary>Rewrites every line ending in <paramref name="text"/> as <paramref name="sequence"/>.</summary>
    public static string Normalize(string text, string sequence)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sequence);

        // Scanning first keeps the common case — text that already uses the target sequence — free of an allocation.
        if (!NeedsNormalization(text, sequence))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append(sequence);
            }
            else if (character == '\n')
            {
                builder.Append(sequence);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool NeedsNormalization(string text, string sequence)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character != '\r' && character != '\n')
            {
                continue;
            }

            var length = character == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            if (length != sequence.Length || !text.AsSpan(i, length).SequenceEqual(sequence))
            {
                return true;
            }

            i += length - 1;
        }

        return false;
    }
}
