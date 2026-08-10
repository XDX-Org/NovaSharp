using System.Text.Json;
using System.Xml.Linq;

namespace NovaSharp;

internal sealed record LaunchProfile(string Name, string CommandName, string? ExecutablePath,
    string? CommandLineArgs, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
internal sealed record BuildConfigurationOptions(string ProjectPath, string Configuration, string TargetFramework,
    string? LaunchProfile, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
internal sealed record EffectiveBuildCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment)
{
    internal string Preview => string.Join(' ', new[] { Executable }.Concat(Arguments.Select(Quote)));
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}

internal static class BuildConfigurationDiscovery
{
    internal static IReadOnlyList<string> Frameworks(string projectPath)
    {
        var project = XDocument.Load(projectPath, LoadOptions.None);
        var values = project.Descendants().Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values;
    }

    internal static IReadOnlyList<string> Configurations(string projectPath)
    {
        var project = XDocument.Load(projectPath, LoadOptions.None);
        var configured = project.Descendants().FirstOrDefault(element => element.Name.LocalName == "Configurations")?.Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return configured.Length == 0 ? ["Debug", "Release"] : configured.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<LaunchProfile> Profiles(string launchSettingsPath)
    {
        if (!File.Exists(launchSettingsPath)) return [];
        using var document = JsonDocument.Parse(File.ReadAllBytes(launchSettingsPath), new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object) return [];
        return profiles.EnumerateObject().Select(profile =>
        {
            var value = profile.Value;
            var environment = value.TryGetProperty("environmentVariables", out var variables) && variables.ValueKind == JsonValueKind.Object
                ? variables.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new LaunchProfile(profile.Name, Text(value, "commandName") ?? "Project", Text(value, "executablePath"),
                Text(value, "commandLineArgs"), Text(value, "workingDirectory"), environment);
        }).ToArray();
    }

    internal static EffectiveBuildCommand Resolve(BuildConfigurationOptions options, IReadOnlyList<LaunchProfile> profiles)
    {
        var project = Path.GetFullPath(options.ProjectPath);
        if (!File.Exists(project)) throw new FileNotFoundException("Project does not exist.", project);
        var frameworks = Frameworks(project);
        if (!frameworks.Contains(options.TargetFramework, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Target framework is not evaluated for this project.");
        if (!Configurations(project).Contains(options.Configuration, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Configuration is not valid for this project.");
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Working directory does not exist.");
        var profile = options.LaunchProfile is null ? null : profiles.SingleOrDefault(item => item.Name == options.LaunchProfile)
            ?? throw new InvalidOperationException("Launch profile is not available.");
        var arguments = new List<string> { "run", "--project", project, "--configuration", options.Configuration,
            "--framework", options.TargetFramework, "--no-launch-profile" };
        var applicationArguments = options.Arguments.Count > 0 ? options.Arguments : ParseArguments(profile?.CommandLineArgs);
        if (applicationArguments.Count > 0) { arguments.Add("--"); arguments.AddRange(applicationArguments); }
        var environment = new Dictionary<string, string>(profile?.Environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in options.Environment) environment[item.Key] = item.Value;
        return new("dotnet", arguments, workingDirectory, environment);
    }

    internal static string Redact(string value) => value.Contains('=') && IsSecret(value[..value.IndexOf('=')])
        ? value[..(value.IndexOf('=') + 1)] + "[redacted]" : value;

    private static string? Text(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static IReadOnlyList<string> ParseArguments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in value)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(character);
        }
        if (quoted) throw new InvalidOperationException("Launch-profile arguments contain an unterminated quote.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
    private static bool IsSecret(string name) => name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase) || name.Contains("apikey", StringComparison.OrdinalIgnoreCase);
}

internal sealed record PersistedBuildConfiguration(int SchemaVersion, string ProjectPath, string Configuration,
    string? TargetFramework, string? LaunchProfile, IReadOnlyList<string> Arguments, string WorkingDirectory);

internal sealed class BuildConfigurationStore(string directory)
{
    internal async Task SaveAsync(PersistedBuildConfiguration value, CancellationToken cancellationToken = default)
    {
        Validate(value);
        await AtomicFile.WriteAsync(PathFor(value.ProjectPath), JsonSerializer.SerializeToUtf8Bytes(value), cancellationToken);
    }

    internal async Task<PersistedBuildConfiguration?> LoadAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var path = PathFor(projectPath);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<PersistedBuildConfiguration>(stream, cancellationToken: cancellationToken);
            if (value is null) return null;
            Validate(value);
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    private string PathFor(string projectPath)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath))));
        return Path.Combine(directory, key + ".json");
    }

    private static void Validate(PersistedBuildConfiguration value)
    {
        if (value.SchemaVersion != 1 || !Path.IsPathFullyQualified(value.ProjectPath) || !Path.IsPathFullyQualified(value.WorkingDirectory)
            || string.IsNullOrWhiteSpace(value.Configuration) || value.Arguments.Count > 256 || value.Arguments.Any(argument => argument.Length > 8192))
            throw new InvalidDataException("Invalid persisted build configuration.");
    }
}
