namespace NovaSharp.Configuration;

/// <summary>The locally packaged font Monaco uses for source text.</summary>
public enum EditorFontPreference
{
    /// <summary>NovaSharp's platform-neutral monospace fallback stack.</summary>
    Default,

    /// <summary>The locally packaged Fast Mono face.</summary>
    FastMono,
}

/// <summary>Stable settings identifiers for the built-in editor fonts.</summary>
public static class EditorFonts
{
    public const string DefaultId = "default";
    public const string FastMonoId = "fast-mono";

    public static string Id(EditorFontPreference font) => font switch
    {
        EditorFontPreference.Default => DefaultId,
        EditorFontPreference.FastMono => FastMonoId,
        _ => throw new ArgumentOutOfRangeException(nameof(font), font, null),
    };

    public static string DisplayName(EditorFontPreference font) => font switch
    {
        EditorFontPreference.Default => "Default monospace",
        EditorFontPreference.FastMono => "Fast Mono",
        _ => throw new ArgumentOutOfRangeException(nameof(font), font, null),
    };

    public static bool TryParse(string id, out EditorFontPreference font)
    {
        font = id switch
        {
            DefaultId => EditorFontPreference.Default,
            FastMonoId => EditorFontPreference.FastMono,
            _ => default,
        };
        return id is DefaultId or FastMonoId;
    }
}
