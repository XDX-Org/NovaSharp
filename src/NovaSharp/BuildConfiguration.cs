using System.Text.Json;
using System.Diagnostics;

namespace NovaSharp;

internal sealed record LaunchProfile(string Name, string CommandName, string? ExecutablePath,
    string? CommandLineArgs, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
internal sealed record BuildConfigurationOptions(string ProjectPath, string Configuration, string TargetFramework,
    string? LaunchProfile, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
internal sealed record EffectiveBuildCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> ApplicationArguments)
{
    internal string Preview => string.Join(' ', new[] { Executable }.Concat(Arguments.Select(Quote)));
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}

internal static class BuildConfigurationDiscovery
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (string Fingerprint, Choices Value)> ChoiceCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal sealed record Choices(IReadOnlyList<string> Configurations,
        IReadOnlyDictionary<string, IReadOnlyList<string>> FrameworksByConfiguration)
    {
        internal IReadOnlyList<string> Frameworks(string configuration) =>
            FrameworksByConfiguration.GetValueOrDefault(configuration) ?? [];
    }

    internal static Choices Discover(string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath)) throw new FileNotFoundException("Project does not exist.", projectPath);
        var fingerprint = Fingerprint(projectPath);
        lock (CacheGate)
            if (ChoiceCache.TryGetValue(projectPath, out var cached) && cached.Fingerprint == fingerprint) return cached.Value;
        var initial = QueryProperties(projectPath, null);
        var configurations = Split(initial.GetValueOrDefault("Configurations"));
        if (configurations.Count == 0) configurations = ["Debug", "Release"];
        var evaluations = configurations.Select(configuration => Task.Run(() =>
            (Configuration: configuration, Properties: QueryProperties(projectPath, configuration)))).ToArray();
        Task.WhenAll(evaluations).GetAwaiter().GetResult();
        var frameworks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in evaluations.Select(task => task.Result))
        {
            var values = Split(evaluation.Properties.GetValueOrDefault("TargetFrameworks"));
            if (values.Count == 0) values = Split(evaluation.Properties.GetValueOrDefault("TargetFramework"));
            frameworks[evaluation.Configuration] = values;
        }
        var result = new Choices(configurations, frameworks);
        lock (CacheGate) ChoiceCache[projectPath] = (fingerprint, result);
        return result;
    }

    internal static IReadOnlyList<string> Frameworks(string projectPath)
        => Discover(projectPath).FrameworksByConfiguration.Values.SelectMany(value => value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<string> Configurations(string projectPath)
        => Discover(projectPath).Configurations;

    private static IReadOnlyList<string> Split(string? value) => string.IsNullOrWhiteSpace(value) ? []
        : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyDictionary<string, string> QueryProperties(string projectPath, string? configuration)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(projectPath)! };
        foreach (var argument in new[] { "msbuild", projectPath, "-nologo",
                     "-getProperty:Configurations,TargetFrameworks,TargetFramework" }) start.ArgumentList.Add(argument);
        if (configuration is not null) start.ArgumentList.Add($"-property:Configuration={configuration}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSBuild evaluation.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(true); throw new TimeoutException("MSBuild evaluation timed out."); }
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < jsonStart) throw new InvalidOperationException("MSBuild evaluation returned no property data.");
        using var document = JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
        return document.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
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
        var choices = Discover(project);
        var frameworks = choices.Frameworks(options.Configuration);
        if (!frameworks.Contains(options.TargetFramework, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Target framework is not evaluated for this project.");
        if (!choices.Configurations.Contains(options.Configuration, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Configuration is not valid for this project.");
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Working directory does not exist.");
        var profile = options.LaunchProfile is null ? null : profiles.SingleOrDefault(item => item.Name == options.LaunchProfile)
            ?? throw new InvalidOperationException("Launch profile is not available.");
        if (profile is not null && !string.Equals(profile.CommandName, "Project", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Launch profile '{profile.Name}' uses unsupported command '{profile.CommandName}'.");
        var arguments = new List<string> { "run", "--project", project, "--configuration", options.Configuration,
            "--framework", options.TargetFramework };
        if (profile is null) arguments.Add("--no-launch-profile");
        else arguments.AddRange(["--launch-profile", profile.Name]);
        var applicationArguments = options.Arguments.Count > 0 ? options.Arguments : ParseArguments(profile?.CommandLineArgs);
        if (applicationArguments.Count > 0) { arguments.Add("--"); arguments.AddRange(applicationArguments); }
        var environment = new Dictionary<string, string>(profile?.Environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in options.Environment) environment[item.Key] = item.Value;
        return new("dotnet", arguments, workingDirectory, environment, applicationArguments);
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
    internal static IReadOnlyDictionary<string, string> ParseEnvironment(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator < 1) throw new InvalidOperationException("Environment entries must use NAME=value.");
            var name = line[..separator].Trim();
            if (IsSecret(name)) throw new InvalidOperationException($"{name} looks secret and cannot be persisted here.");
            if (!(char.IsLetter(name[0]) || name[0] == '_')
                || name.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
                throw new InvalidOperationException($"{name} is not a valid environment-variable name.");
            result[name] = line[(separator + 1)..];
        }
        return result;
    }
    internal static string Fingerprint(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    private static bool IsSecret(string name) => name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase) || name.Contains("apikey", StringComparison.OrdinalIgnoreCase);
}

internal sealed record PersistedBuildConfiguration(int SchemaVersion, string ProjectPath, string Configuration,
    string? TargetFramework, string? LaunchProfile, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null, string? ProjectFingerprint = null,
    string? LaunchSettingsFingerprint = null);

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
            || string.IsNullOrWhiteSpace(value.Configuration) || value.Arguments.Count > 256 || value.Arguments.Any(argument => argument.Length > 8192)
            || value.Environment is { Count: > 128 } || value.Environment?.Any(item => item.Key.Length > 256
                || item.Value.Length > 8192 || LooksSecret(item.Key)) == true)
            throw new InvalidDataException("Invalid persisted build configuration.");
    }

    private static bool LooksSecret(string name) => name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase) || name.Contains("apikey", StringComparison.OrdinalIgnoreCase);
}
