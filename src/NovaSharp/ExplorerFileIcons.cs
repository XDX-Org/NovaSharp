namespace NovaSharp;

internal static class ExplorerFileIcons
{
    internal static string Text(string name, bool symbolicLink = false) => Kind(name) switch
    {
        ".cs" => "C#", ".js" => "JS", ".css" => "CSS", ".html" or ".htm" => "HTML",
        ".json" => "{}", ".md" or ".markdown" => "MD", "git" => "GIT", ".props" => "MSB",
        ".sln" => "SLN", ".slnx" => "SLNX", ".razor" => "RZ", ".dll" => "⚙",
        ".exe" or ".com" => "🖥︎", ".csproj" => "CSP", ".xml" => "<>", ".pdb" => "PDB",
        ".yml" or ".yaml" => "YML", "none" => "FILE", _ => symbolicLink ? "↗" : "▧"
    };

    internal static string CssClass(string name) => Kind(name) switch
    {
        ".cs" => "csharp", ".js" => "javascript", ".css" => "stylesheet", ".html" or ".htm" => "html",
        ".json" => "json", ".md" or ".markdown" => "markdown", "git" => "git", ".props" => "props",
        ".sln" or ".slnx" => "solution", ".razor" => "razor", ".dll" => "assembly",
        ".exe" or ".com" => "executable", ".csproj" => "csproj", ".xml" => "xml", ".pdb" => "symbols",
        ".yml" or ".yaml" => "yaml", "none" => "extensionless", _ => "generic"
    };

    private static string Kind(string name)
    {
        name = name.ToLowerInvariant();
        if (name is ".gitignore" or ".gitattributes" or ".gitmodules" or ".gitkeep") return "git";
        var extension = Path.GetExtension(name);
        return extension.Length == 0 ? "none" : extension;
    }
}
