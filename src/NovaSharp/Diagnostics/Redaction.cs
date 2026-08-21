using System.Security.Cryptography;
using System.Text;

namespace NovaSharp.Diagnostics;

/// <summary>
/// Reduces values to something safe to write into a log.
/// </summary>
/// <remarks>
/// Redaction is the default here rather than an option a caller remembers to apply, because the two things NovaSharp
/// handles most of — the user's source code and the paths it lives at — are exactly the two things a log must not
/// contain. A log is written to disk, quoted into issue reports, and pasted into chat; anything in it has effectively
/// been published.
/// </remarks>
public static class Redaction
{
    /// <summary>What replaces any document text that reaches a log.</summary>
    public const string RemovedText = "[text removed]";

    /// <summary>
    /// Reduces <paramref name="path"/> to its file name and a stable digest of the directory holding it.
    /// </summary>
    /// <remarks>
    /// The file name is what makes a log readable; the directory is what makes it identifying. The digest keeps two
    /// entries about the same folder recognizably about the same folder without saying which folder it is.
    /// </remarks>
    public static string Path(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "[no path]";
        }

        var name = System.IO.Path.GetFileName(path);
        var directory = System.IO.Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directory))
        {
            return string.IsNullOrEmpty(name) ? "[no path]" : name;
        }

        return $"{Digest(directory)}/{(string.IsNullOrEmpty(name) ? "[no name]" : name)}";
    }

    /// <summary>Reports how long some text was without saying what it said.</summary>
    /// <remarks>
    /// Length is usually the only thing a log actually needed from document text — whether a save wrote what was
    /// expected, whether a snapshot was empty — and it reveals nothing about the content.
    /// </remarks>
    public static string Text(string? text) =>
        text is null ? "[none]" : $"{RemovedText} ({text.Length} characters)";

    private static string Digest(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}
