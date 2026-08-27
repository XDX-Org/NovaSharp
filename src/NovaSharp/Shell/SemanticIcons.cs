using System.Collections.Frozen;

namespace NovaSharp.Shell;

public enum SemanticIcon
{
    AddFile,
    AddFolder,
    ChevronDown,
    ChevronRight,
    Close,
    CollapseAll,
    CommandPalette,
    Compare,
    Dirty,
    Error,
    Explorer,
    File,
    FileCode,
    Folder,
    FolderOpen,
    Information,
    Menu,
    Missing,
    More,
    Move,
    OpenFile,
    Panel,
    Pin,
    ReadOnly,
    Refresh,
    Rename,
    Save,
    Search,
    Solution,
    Symlink,
    Trash,
    Warning,
}

public static class SemanticIcons
{
    private static readonly FrozenDictionary<SemanticIcon, string> Classes =
        new Dictionary<SemanticIcon, string>
        {
            [SemanticIcon.AddFile] = "new-file",
            [SemanticIcon.AddFolder] = "new-folder",
            [SemanticIcon.ChevronDown] = "chevron-down",
            [SemanticIcon.ChevronRight] = "chevron-right",
            [SemanticIcon.Close] = "close",
            [SemanticIcon.CollapseAll] = "collapse-all",
            [SemanticIcon.CommandPalette] = "search-sparkle",
            [SemanticIcon.Compare] = "diff",
            [SemanticIcon.Dirty] = "circle-filled",
            [SemanticIcon.Error] = "error",
            [SemanticIcon.Explorer] = "files",
            [SemanticIcon.File] = "file",
            [SemanticIcon.FileCode] = "file-code",
            [SemanticIcon.Folder] = "folder",
            [SemanticIcon.FolderOpen] = "folder-opened",
            [SemanticIcon.Information] = "info",
            [SemanticIcon.Menu] = "menu",
            [SemanticIcon.Missing] = "warning",
            [SemanticIcon.More] = "ellipsis",
            [SemanticIcon.Move] = "move",
            [SemanticIcon.OpenFile] = "go-to-file",
            [SemanticIcon.Panel] = "layout-panel",
            [SemanticIcon.Pin] = "pin",
            [SemanticIcon.ReadOnly] = "lock-small",
            [SemanticIcon.Refresh] = "refresh",
            [SemanticIcon.Rename] = "edit",
            [SemanticIcon.Save] = "save",
            [SemanticIcon.Search] = "search",
            [SemanticIcon.Solution] = "project",
            [SemanticIcon.Symlink] = "file-symlink-file",
            [SemanticIcon.Trash] = "trash",
            [SemanticIcon.Warning] = "warning",
        }.ToFrozenDictionary();

    static SemanticIcons()
    {
        var missing = Enum.GetValues<SemanticIcon>().Where(icon => !Classes.ContainsKey(icon)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"Missing semantic icons: {string.Join(", ", missing)}");
    }

    public static string CssClass(SemanticIcon icon) => $"codicon codicon-{Classes[icon]}";

    public static void Validate() => _ = Classes.Count;
}
