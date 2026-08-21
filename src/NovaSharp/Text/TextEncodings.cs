using System.Collections.Immutable;
using System.Text;

namespace NovaSharp.Text;

/// <summary>
/// The catalogue of encodings NovaSharp can open and save a document with.
/// </summary>
/// <remarks>
/// The catalogue is whatever the running framework can actually round-trip, extended with
/// <see cref="CodePagesEncodingProvider"/> so the same set is available on every supported platform. NovaSharp does not
/// curate a shorter hard-coded list: an encoding the framework supports and NovaSharp hides is a file the user cannot
/// open for no reason they can see. See ADR 0002.
/// </remarks>
public static class TextEncodings
{
    private const int Utf16LittleEndian = 1200;
    private const int Utf16BigEndian = 1201;
    private const int Utf32LittleEndian = 12000;
    private const int Utf32BigEndian = 12001;

    private static readonly ImmutableArray<TextEncodingProfile> Catalog = BuildCatalog();

    /// <summary>UTF-8 with no byte-order mark. The default for new documents and for bytes nothing else identified.</summary>
    public static TextEncodingProfile Utf8 { get; } = Require(Encoding.UTF8.CodePage, byteOrderMark: false);

    /// <summary>UTF-8 with a byte-order mark.</summary>
    public static TextEncodingProfile Utf8ByteOrderMark { get; } = Require(Encoding.UTF8.CodePage, byteOrderMark: true);

    /// <summary>
    /// ISO-8859-1, the fallback for bytes no other encoding accepted.
    /// </summary>
    /// <remarks>
    /// Every one of the 256 byte values maps to a distinct character, so a document opened this way round-trips
    /// byte-for-byte. It may read as nonsense, but saving it cannot corrupt the original.
    /// </remarks>
    public static TextEncodingProfile Latin1 { get; } = Require(28591, byteOrderMark: false);

    /// <summary>Every encoding in the catalogue, ordered by display name.</summary>
    public static IReadOnlyList<TextEncodingProfile> All => Catalog;

    /// <summary>
    /// The identifiers offered before the full catalogue, in the order they are shown.
    /// </summary>
    /// <remarks>
    /// A short list first and everything else behind it, rather than one list of hundreds. Membership here is
    /// presentation only: any catalogue entry can be chosen, and nothing else about a favourite differs.
    /// </remarks>
    public static IReadOnlyList<string> Favorites { get; } =
        ["utf-8", "utf-8-bom", "utf-16-bom", "utf-16be-bom", "windows-1252", "iso-8859-1", "us-ascii"];

    /// <summary>Returns the profile with <paramref name="id"/>, or <see langword="null"/> when there is none.</summary>
    public static TextEncodingProfile? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        foreach (var profile in Catalog)
        {
            if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the profile the byte-order mark at the start of <paramref name="bytes"/> identifies, if there is one.
    /// </summary>
    /// <param name="bytes">The first bytes of a document.</param>
    /// <param name="markLength">The length of the recognized mark, which the decoder must skip.</param>
    public static TextEncodingProfile? DetectByteOrderMark(ReadOnlySpan<byte> bytes, out int markLength)
    {
        // Longest first: a UTF-32 little-endian mark starts with the whole of the UTF-16 little-endian mark, so testing
        // the shorter one first would identify every UTF-32 LE file as UTF-16 LE followed by a null.
        (byte[] Mark, int CodePage)[] marks =
        [
            ([0xFF, 0xFE, 0x00, 0x00], Utf32LittleEndian),
            ([0x00, 0x00, 0xFE, 0xFF], Utf32BigEndian),
            ([0xFF, 0xFE], Utf16LittleEndian),
            ([0xFE, 0xFF], Utf16BigEndian),
            ([0xEF, 0xBB, 0xBF], Encoding.UTF8.CodePage),
        ];

        foreach (var (mark, codePage) in marks)
        {
            if (bytes.Length < mark.Length || !bytes[..mark.Length].SequenceEqual(mark))
            {
                continue;
            }

            var profile = Find(codePage, byteOrderMark: true);
            if (profile is null)
            {
                // The framework does not offer this code page here. Reporting no mark leaves the caller on its normal
                // fallback path rather than handing it a profile it cannot decode with.
                continue;
            }

            markLength = mark.Length;
            return profile;
        }

        markLength = 0;
        return null;
    }

    /// <summary>Returns the catalogue entry for <paramref name="codePage"/> with or without a byte-order mark.</summary>
    public static TextEncodingProfile? Find(int codePage, bool byteOrderMark)
    {
        foreach (var profile in Catalog)
        {
            if (profile.CodePage == codePage && profile.ByteOrderMark == byteOrderMark)
            {
                return profile;
            }
        }

        return null;
    }

    private static TextEncodingProfile Require(int codePage, bool byteOrderMark) =>
        Find(codePage, byteOrderMark)
        ?? throw new InvalidOperationException(
            $"The framework does not provide code page {codePage}, which NovaSharp requires.");

    private static ImmutableArray<TextEncodingProfile> BuildCatalog()
    {
        // Registering the provider here, before any profile can exist, is what lets TextEncodingProfile ask for a code
        // page without every caller having to remember to register one first.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var builder = ImmutableArray.CreateBuilder<TextEncodingProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in Encoding.GetEncodings())
        {
            Encoding encoding;
            try
            {
                encoding = info.GetEncoding();
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
            {
                // Named by the framework but not usable here. An entry NovaSharp cannot actually open a file with would
                // be worse than an absent one.
                continue;
            }

            var id = encoding.WebName.ToLowerInvariant();
            if (!seen.Add(id))
            {
                continue;
            }

            builder.Add(new TextEncodingProfile(id, info.DisplayName, info.CodePage, ByteOrderMark: false));

            // A preamble is offered as its own choice wherever one exists: "UTF-8" and "UTF-8 with BOM" produce
            // different files, so the user picks between them rather than discovering afterwards which they got.
            if (encoding.GetPreamble().Length > 0)
            {
                builder.Add(new TextEncodingProfile(
                    $"{id}-bom",
                    $"{info.DisplayName} with BOM",
                    info.CodePage,
                    ByteOrderMark: true));
            }
        }

        builder.Sort(static (left, right) =>
            string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
        return builder.ToImmutable();
    }
}
