namespace NovaSharp.Editing;

/// <summary>Maps a file path to the Monaco language identifier registered by the packaged bundle.</summary>
public static class LanguageIds
{
    /// <summary>The identifier used when an extension has no registered language.</summary>
    public const string PlainText = "plaintext";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".css"] = "css",
        [".htm"] = "html",
        [".html"] = "html",
    };

    /// <summary>Returns the Monaco language identifier for <paramref name="path"/>.</summary>
    /// <remarks>
    /// Extensions are matched case-insensitively because the extension is a language marker, not a file-system
    /// identity. Document identity is handled by <see cref="Platform.IWorkspacePaths"/> and stays exact.
    /// </remarks>
    public static string FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);
        return ByExtension.TryGetValue(extension, out var languageId) ? languageId : PlainText;
    }
}
