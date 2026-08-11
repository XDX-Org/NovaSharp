namespace NovaSharp.LanguageServers;

internal enum LanguageServerKind { RoslynRazor, Javascript, Html, Css }
internal sealed record LanguageServerDefinition(LanguageServerKind Kind, IReadOnlySet<string> Extensions,
    LanguageServerLaunchOptions? Launch, string? UnavailableReason = null);

internal sealed class LanguageServerCatalog
{
    private readonly IReadOnlyList<LanguageServerDefinition> _definitions;
    internal LanguageServerCatalog(IEnumerable<LanguageServerDefinition> definitions) => _definitions = definitions.ToArray();
    internal IReadOnlyList<LanguageServerDefinition> Definitions => _definitions;
    internal LanguageServerDefinition? ForDocument(string path) => _definitions.FirstOrDefault(definition =>
        definition.Extensions.Any(extension => extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)));

    internal static LanguageServerCatalog Discover(string workspace, string? assetRoot = null)
    {
        assetRoot ??= ResolveAssetRoot();
        var roslyn = Path.Combine(assetRoot, "roslyn", OperatingSystem.IsWindows()
            ? "Microsoft.CodeAnalysis.LanguageServer.exe" : "Microsoft.CodeAnalysis.LanguageServer");
        var razor = Path.Combine(assetRoot, "razor");
        var razorExtension = Path.Combine(razor, "Microsoft.VisualStudioCode.RazorExtension.dll");
        var webServer = Path.Combine(assetRoot, "server.cjs");
        var javascriptServer = Path.Combine(assetRoot, "node_modules", "typescript-language-server", "lib", "cli.mjs");
        var node = Path.Combine(assetRoot, "node", OperatingSystem.IsWindows() ? "node.exe" : "bin/node");
        var environment = new Dictionary<string, string> { ["DOTNET_gcServer"] = "0" };
        var logDirectory = Path.Combine(Path.GetTempPath(), "NovaSharp", "LanguageServers");
        Directory.CreateDirectory(logDirectory);
        var definitions = new List<LanguageServerDefinition>
        {
            File.Exists(roslyn) && File.Exists(razorExtension)
                ? new(LanguageServerKind.RoslynRazor, new HashSet<string>([".cs", ".razor", ".cshtml"]),
                    new(roslyn, ["--stdio", "--logLevel", "Information", "--razorSourceGenerator",
                        Path.Combine(razor, "Microsoft.CodeAnalysis.Razor.Compiler.dll"), "--razorDesignTimePath",
                        Path.Combine(razor, "Targets", "Microsoft.NET.Sdk.Razor.DesignTime.targets"),
                        "--csharpDesignTimePath", Path.Combine(razor, "Targets", "Microsoft.CSharpExtension.DesignTime.targets"),
                        "--extension", razorExtension, "--telemetryLevel", "off", "--extensionLogDirectory", logDirectory,
                        $"--clientProcessId={Environment.ProcessId}"], workspace, environment))
                : new(LanguageServerKind.RoslynRazor, new HashSet<string>([".cs", ".razor", ".cshtml"]), null,
                    "Packaged Roslyn/Razor assets are missing. Run tools/acquire-language-servers.sh for the publish RID."),
            Web(LanguageServerKind.Html, [".html", ".htm"], "--html"),
            Web(LanguageServerKind.Css, [".css"], "--css"),
            File.Exists(node) && File.Exists(javascriptServer)
                ? new(LanguageServerKind.Javascript, new HashSet<string>([".js", ".jsx", ".ts", ".tsx"]),
                    new(node, [javascriptServer, "--stdio"], workspace))
                : new(LanguageServerKind.Javascript, new HashSet<string>([".js", ".jsx", ".ts", ".tsx"]), null,
                    "Packaged JavaScript/TypeScript language-server assets are missing. Run tools/acquire-language-servers.sh for the publish RID.")
        };
        return new(definitions);

        LanguageServerDefinition Web(LanguageServerKind kind, string[] extensions, string language)
            => File.Exists(node) && File.Exists(webServer)
                ? new(kind, new HashSet<string>(extensions), new(node, [webServer, "--stdio", language], workspace))
                : new(kind, new HashSet<string>(extensions), null,
                    "Packaged web-language-server assets are missing. Run tools/acquire-language-servers.sh for the publish RID.");
    }

    private static string ResolveAssetRoot()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "LanguageServers");
        if (File.Exists(Path.Combine(packaged, "server.cjs")) || Directory.Exists(Path.Combine(packaged, "roslyn")))
            return packaged;
        var developmentRoot = typeof(LanguageServerCatalog).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "LanguageServerDevelopmentAssetRoot")?.Value;
        if (developmentRoot is not null)
        {
            var development = Path.Combine(developmentRoot,
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            if (File.Exists(Path.Combine(development, "server.cjs")) || Directory.Exists(Path.Combine(development, "roslyn")))
                return development;
        }
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }.Distinct())
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                foreach (var development in new[]
                {
                    Path.Combine(directory.FullName, "LanguageServers", "Assets",
                        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier),
                    Path.Combine(directory.FullName, "src", "NovaSharp", "LanguageServers", "Assets",
                        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)
                })
                    if (File.Exists(Path.Combine(development, "server.cjs")) || Directory.Exists(Path.Combine(development, "roslyn")))
                        return development;
                directory = directory.Parent;
            }
        }
        return packaged;
    }

    internal static LanguageServerCatalog Unavailable() => new([
        new(LanguageServerKind.RoslynRazor, new HashSet<string>([".cs", ".razor", ".cshtml"]), null,
            "The packaged Roslyn/Razor language server is unavailable."),
        new(LanguageServerKind.Html, new HashSet<string>([".html", ".htm"]), null,
            "The packaged HTML language server is unavailable."),
        new(LanguageServerKind.Css, new HashSet<string>([".css"]), null,
            "The packaged CSS language server is unavailable."),
        new(LanguageServerKind.Javascript, new HashSet<string>([".js", ".jsx", ".ts", ".tsx"]), null,
            "The packaged JavaScript/TypeScript language server is unavailable.")]);
}
