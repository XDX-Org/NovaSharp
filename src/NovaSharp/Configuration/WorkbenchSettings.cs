using NovaSharp.Text;

namespace NovaSharp.Configuration;

/// <summary>
/// The settings phase 2 defines, as validated values rather than as whatever was in the file.
/// </summary>
/// <remarks>
/// Every property has a usable default, so a missing, empty, or partly invalid settings file still produces a
/// complete configuration. Nothing downstream has to handle "not configured".
/// </remarks>
/// <param name="DefaultEncoding">Tried when a file carries no byte-order mark.</param>
/// <param name="FallbackEncoding">Used for bytes no other encoding accepted. Must round-trip every byte.</param>
/// <param name="DefaultLineEnding">Used for a file that contains no line break at all.</param>
/// <param name="ReloadUnmodifiedFiles">
/// Whether a clean document follows its file when something else changes it. A dirty document always asks, whatever
/// this is set to.
/// </param>
/// <param name="EditorFont">The allow-listed, locally packaged font Monaco uses for source text.</param>
public sealed record WorkbenchSettings(
    TextEncodingProfile DefaultEncoding,
    TextEncodingProfile FallbackEncoding,
    LineEndingStyle DefaultLineEnding,
    bool ReloadUnmodifiedFiles,
    IReadOnlyList<string> WorkspaceIgnoredPaths,
    EditorFontPreference EditorFont,
    bool CSharpSuggestions)
{
    /// <summary>The schema version written to, and expected in, a settings file.</summary>
    /// <remarks>
    /// Present from the first release rather than added when it is first needed, because a file written without a
    /// version is a file no later migration can identify.
    /// </remarks>
    public const int CurrentSchemaVersion = 4;

    /// <summary>What NovaSharp uses when nothing has been configured.</summary>
    public static WorkbenchSettings Defaults { get; } = new(
        TextEncodings.Utf8,
        TextEncodings.Latin1,
        LineEndingStyle.Lf,
        ReloadUnmodifiedFiles: true,
        WorkspaceIgnoredPaths: [],
        EditorFontPreference.Default,
        CSharpSuggestions: true);
}
