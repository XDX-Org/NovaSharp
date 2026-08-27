using NovaSharp.Text;

namespace NovaSharp.Configuration;

/// <summary>Which file a problem came from.</summary>
public enum SettingsScope
{
    /// <summary>The per-user file, applying to every workspace.</summary>
    User,

    /// <summary>The file beside the workspace root, applying to that workspace only.</summary>
    Workspace,
}

/// <summary>Something wrong with a settings file, stated so the user can fix it.</summary>
/// <param name="Scope">Which file it was in.</param>
/// <param name="Path">The file, so the message can name it.</param>
/// <param name="Message">What is wrong and what NovaSharp did instead.</param>
public sealed record SettingsProblem(SettingsScope Scope, string Path, string Message);

/// <summary>The settings NovaSharp will use, and everything it had to ignore to get there.</summary>
/// <param name="Settings">The validated result.</param>
/// <param name="Problems">
/// What was rejected. Never silently discarded: an ignored setting the user believes is in force is worse than one
/// they were told about.
/// </param>
public sealed record SettingsResolution(WorkbenchSettings Settings, IReadOnlyList<SettingsProblem> Problems)
{
    /// <summary>Whether every value in every scope was understood.</summary>
    public bool IsClean => Problems.Count == 0;
}

/// <summary>
/// Turns the scopes' raw documents into one validated result.
/// </summary>
/// <remarks>
/// Pure, and deliberately separate from anything that reads a file, so the merge and validation rules can be tested
/// without a disk.
/// </remarks>
public static class SettingsResolver
{
    /// <summary>Overlays <paramref name="workspace"/> on <paramref name="user"/> on the built-in defaults.</summary>
    /// <param name="user">The user-scoped document, or <see langword="null"/> when there is no readable one.</param>
    /// <param name="userPath">The user file's path, for problem messages.</param>
    /// <param name="workspace">The workspace-scoped document, or <see langword="null"/>.</param>
    /// <param name="workspacePath">The workspace file's path, for problem messages.</param>
    public static SettingsResolution Resolve(
        SettingsDocument? user,
        string userPath,
        SettingsDocument? workspace,
        string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(userPath);
        ArgumentNullException.ThrowIfNull(workspacePath);

        var problems = new List<SettingsProblem>();
        var settings = WorkbenchSettings.Defaults;

        // Widest scope first, so a narrower one overrides it key by key rather than wholesale.
        settings = Apply(settings, user, SettingsScope.User, userPath, problems);
        settings = Apply(settings, workspace, SettingsScope.Workspace, workspacePath, problems);

        return new SettingsResolution(settings, problems);
    }

    private static WorkbenchSettings Apply(
        WorkbenchSettings current,
        SettingsDocument? document,
        SettingsScope scope,
        string path,
        List<SettingsProblem> problems)
    {
        if (document is null)
        {
            return current;
        }

        if (document.SchemaVersion is { } version && version > WorkbenchSettings.CurrentSchemaVersion)
        {
            // Written by a newer NovaSharp. Guessing at what its keys mean is how a settings file gets silently
            // rewritten into something the version that wrote it no longer understands.
            problems.Add(new SettingsProblem(
                scope,
                path,
                $"Schema version {version} is newer than this version of NovaSharp understands ({WorkbenchSettings.CurrentSchemaVersion}). "
                + "The file was ignored."));
            return current;
        }

        if (document.DefaultEncoding is { } defaultEncoding)
        {
            if (TextEncodings.Find(defaultEncoding) is { } profile)
            {
                current = current with { DefaultEncoding = profile };
            }
            else
            {
                problems.Add(new SettingsProblem(
                    scope, path, $"'{defaultEncoding}' is not an encoding this platform provides. defaultEncoding was ignored."));
            }
        }

        if (document.FallbackEncoding is { } fallbackEncoding)
        {
            var profile = TextEncodings.Find(fallbackEncoding);
            if (profile is null)
            {
                problems.Add(new SettingsProblem(
                    scope, path, $"'{fallbackEncoding}' is not an encoding this platform provides. fallbackEncoding was ignored."));
            }
            else if (!RoundTripsEveryByte(profile))
            {
                // The fallback exists to open bytes nothing else accepted. One that cannot represent every byte value
                // would turn an unreadable file into a corrupted one the moment it was saved.
                problems.Add(new SettingsProblem(
                    scope,
                    path,
                    $"'{fallbackEncoding}' cannot round-trip every byte, so it cannot be the fallback encoding. "
                    + "fallbackEncoding was ignored."));
            }
            else
            {
                current = current with { FallbackEncoding = profile };
            }
        }

        if (document.DefaultLineEnding is { } lineEnding)
        {
            if (Enum.IsDefined(lineEnding))
            {
                current = current with { DefaultLineEnding = lineEnding };
            }
            else
            {
                problems.Add(new SettingsProblem(
                    scope, path, $"'{lineEnding}' is not a line ending NovaSharp writes. defaultLineEnding was ignored."));
            }
        }

        if (document.ReloadUnmodifiedFiles is { } reload)
        {
            current = current with { ReloadUnmodifiedFiles = reload };
        }

        if (document.WorkspaceIgnoredPaths is { } ignored)
        {
            var valid = ignored
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim().Replace('\\', '/'))
                .Where(value =>
                {
                    if (Path.IsPathRooted(value) || value.Split('/').Contains("..", StringComparer.Ordinal))
                    {
                        problems.Add(new SettingsProblem(
                            scope, path, $"'{value}' is not a workspace-relative ignore pattern and was ignored."));
                        return false;
                    }
                    return true;
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            current = current with { WorkspaceIgnoredPaths = valid };
        }

        if (document.EditorFont is { } editorFont)
        {
            if (EditorFonts.TryParse(editorFont, out var font))
            {
                current = current with { EditorFont = font };
            }
            else
            {
                problems.Add(new SettingsProblem(
                    scope,
                    path,
                    $"'{editorFont}' is not a packaged editor font. editorFont was ignored."));
            }
        }

        if (document.CSharpSuggestions is { } suggestions)
        {
            current = current with { CSharpSuggestions = suggestions };
        }

        return current;
    }

    /// <summary>Returns whether every one of the 256 byte values decodes to a distinct character.</summary>
    private static bool RoundTripsEveryByte(TextEncodingProfile profile)
    {
        if (profile.ByteOrderMark)
        {
            return false;
        }

        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        try
        {
            var encoding = profile.CreateEncoding();
            return encoding.GetBytes(encoding.GetString(bytes)).AsSpan().SequenceEqual(bytes);
        }
        catch (Exception exception) when (exception is System.Text.DecoderFallbackException or System.Text.EncoderFallbackException)
        {
            return false;
        }
    }
}
