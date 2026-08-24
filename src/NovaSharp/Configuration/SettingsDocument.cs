using System.Text.Json;
using System.Text.Json.Serialization;
using NovaSharp.Text;

namespace NovaSharp.Configuration;

/// <summary>
/// One settings file exactly as it was written, before anything has been validated.
/// </summary>
/// <remarks>
/// Every property is nullable, and that is the whole design: absent and "set to the default" are different, because
/// only an absent value may be overridden by a wider scope. Parsing into the validated
/// <see cref="WorkbenchSettings"/> directly would lose that distinction and make a workspace file that says nothing
/// indistinguishable from one that says "use UTF-8".
/// </remarks>
public sealed class SettingsDocument
{
    /// <summary>The schema this file was written against.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>The identifier of the encoding tried when a file carries no byte-order mark.</summary>
    public string? DefaultEncoding { get; init; }

    /// <summary>The identifier of the encoding used for bytes nothing else accepted.</summary>
    public string? FallbackEncoding { get; init; }

    /// <summary>The line ending used for a file with no line break.</summary>
    public LineEndingStyle? DefaultLineEnding { get; init; }

    /// <summary>Whether a clean document follows its file when something else changes it.</summary>
    public bool? ReloadUnmodifiedFiles { get; init; }

    /// <summary>Additional Explorer names or workspace-relative glob patterns to hide.</summary>
    public string[]? WorkspaceIgnoredPaths { get; init; }

    /// <summary>The stable identifier of the locally packaged source-editor font.</summary>
    public string? EditorFont { get; init; }

    /// <summary>Keys owned by a newer component, retained when NovaSharp updates one known setting.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    /// <summary>How this file is read and written.</summary>
    /// <remarks>
    /// Indented and case-insensitive because the workspace scope is a source-controlled file that people edit and
    /// diff by hand. Enums are written as names for the same reason: a number in a settings file is unreadable, and
    /// its meaning silently changes if the enum is ever reordered.
    /// </remarks>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
