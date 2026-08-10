using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaSharp;

[Flags]
internal enum ExtensionPermission { None = 0, WorkspaceRead = 1, WorkspaceWrite = 2, Process = 4, Network = 8, Debugger = 16, Secrets = 32 }
internal sealed record ExtensionCommandContribution(string Id, string Title);
internal sealed record ExtensionSettingContribution(string Id, string Type, JsonElement? Default);
internal sealed record ExtensionManifest(int ManifestVersion, string Id, string Name, string Version, string ApiVersion,
    string EntryPoint, ExtensionPermission Permissions, IReadOnlyList<string> ActivationEvents,
    IReadOnlyList<ExtensionCommandContribution> Commands, IReadOnlyList<ExtensionSettingContribution> Settings);
internal sealed record ExtensionDiagnostic(string ExtensionId, string Code, string Message);

internal static partial class ExtensionManifestReader
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    internal static ExtensionManifest Read(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new() { CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        var version = RequiredInt(root, "manifestVersion");
        var id = Required(root, "id");
        var name = Required(root, "name");
        var extensionVersion = Required(root, "version");
        var apiVersion = Required(root, "apiVersion");
        var entryPoint = Required(root, "entryPoint");
        if (version != 1 || !IdPattern().IsMatch(id) || !Version.TryParse(extensionVersion, out _)
            || !Version.TryParse(apiVersion, out _) || Path.IsPathRooted(entryPoint) || entryPoint.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
            throw new InvalidDataException("Invalid extension manifest identity or entry point.");
        var permissions = ExtensionPermission.None;
        foreach (var permission in Strings(root, "permissions"))
            permissions |= Enum.TryParse<ExtensionPermission>(permission, true, out var parsed) ? parsed
                : throw new InvalidDataException($"Unknown extension permission '{permission}'.");
        var commands = root.TryGetProperty("commands", out var commandItems) ? commandItems.EnumerateArray()
            .Select(item => new ExtensionCommandContribution(Required(item, "id"), Required(item, "title"))).ToArray() : [];
        var settings = root.TryGetProperty("settings", out var settingItems) ? settingItems.EnumerateArray()
            .Select(item => new ExtensionSettingContribution(Required(item, "id"), Required(item, "type"),
                item.TryGetProperty("default", out var value) ? value.Clone() : null)).ToArray() : [];
        if (commands.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != commands.Length
            || settings.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != settings.Length)
            throw new InvalidDataException("Extension contribution identifiers must be unique.");
        return new(version, id, name, extensionVersion, apiVersion, entryPoint, permissions,
            Strings(root, "activationEvents"), commands, settings);
    }

    private static string Required(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!
        : throw new InvalidDataException($"Extension manifest requires '{name}'.");
    private static int RequiredInt(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var result) ? result : throw new InvalidDataException($"Extension manifest requires '{name}'.");
    private static string[] Strings(JsonElement item, string name) => !item.TryGetProperty(name, out var value) ? []
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(entry => entry.GetString()
            ?? throw new InvalidDataException($"'{name}' entries must be strings.")).ToArray()
        : throw new InvalidDataException($"'{name}' must be an array.");
}

internal sealed class ExtensionRegistry(Version supportedApiVersion)
{
    private readonly Dictionary<string, ExtensionManifest> _enabled = new(StringComparer.Ordinal);
    internal IReadOnlyCollection<ExtensionManifest> Enabled => _enabled.Values;

    internal void Enable(ExtensionManifest manifest, ExtensionPermission approvedPermissions, bool workspaceTrusted)
    {
        var requestedApi = Version.Parse(manifest.ApiVersion);
        if (requestedApi.Major != supportedApiVersion.Major || requestedApi > supportedApiVersion)
            throw new InvalidOperationException("Extension API version is incompatible.");
        if ((manifest.Permissions & ~approvedPermissions) != 0) throw new UnauthorizedAccessException("Extension permissions have not been approved.");
        if (!workspaceTrusted && (manifest.Permissions & (ExtensionPermission.WorkspaceWrite | ExtensionPermission.Process
            | ExtensionPermission.Network | ExtensionPermission.Debugger | ExtensionPermission.Secrets)) != 0)
            throw new UnauthorizedAccessException("Workspace trust is required for privileged extension permissions.");
        if (!_enabled.TryAdd(manifest.Id, manifest)) throw new InvalidOperationException("Extension is already enabled.");
    }

    internal bool Disable(string extensionId) => _enabled.Remove(extensionId);
}

internal enum ExtensionActivationState { Inactive, Activating, Active, Failed, Disabled }
internal sealed record ExtensionActivationResult(ExtensionActivationState State, ExtensionDiagnostic? Diagnostic = null);

internal sealed class ExtensionActivationCoordinator(TimeSpan? activationTimeout = null)
{
    private readonly Dictionary<string, ExtensionActivationResult> _states = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout = activationTimeout ?? TimeSpan.FromSeconds(5);
    internal IReadOnlyDictionary<string, ExtensionActivationResult> States => _states;

    internal async Task<ExtensionActivationResult> ActivateAsync(ExtensionManifest manifest,
        Func<CancellationToken, Task> isolatedActivation, CancellationToken cancellationToken = default)
    {
        if (_states.TryGetValue(manifest.Id, out var current) && current.State == ExtensionActivationState.Disabled) return current;
        _states[manifest.Id] = new(ExtensionActivationState.Activating);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await isolatedActivation(timeout.Token);
            return _states[manifest.Id] = new(ExtensionActivationState.Active);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(manifest.Id, "EXT_TIMEOUT", "Extension activation exceeded its time limit.");
        }
        catch (Exception)
        {
            return Fail(manifest.Id, "EXT_CRASH", "Extension activation failed in its isolated host.");
        }
    }

    internal void Disable(string extensionId) => _states[extensionId] = new(ExtensionActivationState.Disabled);
    private ExtensionActivationResult Fail(string id, string code, string message) => _states[id] =
        new(ExtensionActivationState.Failed, new(id, code, message));
}
