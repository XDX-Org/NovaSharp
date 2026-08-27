using System.Security.Cryptography;
using System.Text.Json;
using NovaSharp.Commands;
using NovaSharp.Shell;
using Xunit;

namespace NovaSharp.Tests;

public sealed class WorkbenchShellTests
{
    private static string ReadContract(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", fileName));

    [Fact]
    public void SemanticIconRegistry_MapsEveryDeclaredMeaningToOnePinnedIcon()
    {
        SemanticIcons.Validate();
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "workbench-assets", "codicon.css"));

        foreach (var icon in Enum.GetValues<SemanticIcon>())
        {
            var mapped = SemanticIcons.CssClass(icon);
            Assert.StartsWith("codicon codicon-", mapped, StringComparison.Ordinal);
            Assert.Contains($".{mapped["codicon ".Length..]}:before", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkbenchCommands_ExposePaletteAndPanelThroughTheSharedRegistry()
    {
        Assert.Contains(WorkbenchCommands.ShowCommandPalette, WorkbenchCommands.All);
        Assert.Contains(WorkbenchCommands.TogglePanel, WorkbenchCommands.All);
        Assert.Contains(WorkbenchCommands.ChooseEditorFont, WorkbenchCommands.All);
        Assert.Contains("CtrlCmd", WorkbenchCommands.Describe(WorkbenchCommands.ShowCommandPalette).Keybindings[0]);
    }

    [Fact]
    public void ShellStyles_DefineTokensAndAccessibilityModes()
    {
        var css = ReadContract("app.css");

        Assert.Contains("--surface-canvas", css, StringComparison.Ordinal);
        Assert.Contains("--focus", css, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", css, StringComparison.Ordinal);
        Assert.Contains("forced-colors: active", css, StringComparison.Ordinal);
        Assert.Contains(".workbench.narrow .bottom-panel", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--sidebar-min", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--sidebar-max", css, StringComparison.Ordinal);
        Assert.Contains(".workspace-tree { min-height: 0; overflow-x: hidden; overflow-y: auto;", css, StringComparison.Ordinal);
        Assert.Contains(".explorer-view select { width: 100%;", css, StringComparison.Ordinal);
        Assert.Contains(".tree-detail {", css, StringComparison.Ordinal);
        Assert.Contains(".bottom-panel { height: min(190px, 40vh); overflow-x: hidden;", css, StringComparison.Ordinal);
        Assert.Contains(".tabs-strip::-webkit-scrollbar { display: none; }", css, StringComparison.Ordinal);
        Assert.Matches(@"\.command-bar \{[^\r\n]*background: var\(--surface-region\);", css);
        Assert.Matches(@"\.status-bar \{[^\r\n]*background: var\(--surface-region\);", css);
        Assert.Matches(@"\.command-palette \{[^\r\n]*grid-template-rows: auto minmax\(0, 1fr\);", css);
        Assert.Matches(@"\.command-palette-results \{[^\r\n]*min-height: 0;[^\r\n]*overflow-y: auto;", css);
    }

    [Fact]
    public void ShellShortcut_IsPlatformNeutralAndDoesNotPoll()
    {
        var module = ReadContract("workbench-shell.js");

        Assert.Contains("event.key === 'Shift'", module, StringComparison.Ordinal);
        Assert.Contains("event.ctrlKey || event.metaKey", module, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", module, StringComparison.Ordinal);
        Assert.Contains("new ResizeObserver", module, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('pointerdown', onPointerDown, true)", module, StringComparison.Ordinal);
        Assert.Contains("DismissCommandMenusAsync", module, StringComparison.Ordinal);
        Assert.DoesNotContain("setInterval", module, StringComparison.Ordinal);
        Assert.DoesNotContain("navigator.platform", module, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellMarkup_UsesSemanticIconsInsteadOfProvisionalGlyphs()
    {
        var markup = string.Join('\n',
            ReadContract("EditorPanel.razor"),
            ReadContract("WorkspaceExplorer.razor"),
            ReadContract("WorkspaceTreeNode.razor"));

        Assert.Contains("ShellIcon", markup, StringComparison.Ordinal);
        foreach (var glyph in new[] { "×", "◇", "◆", "▰", "↻", "＋", "▾", "▸" })
            Assert.DoesNotContain(glyph, markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandBar_HasMenusWithoutFileLabelOrVisiblePaletteControl()
    {
        var shell = ReadContract("EditorPanel.razor");

        Assert.Contains("command-bar", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("command-palette-trigger", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("file-name", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Shift Shift", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextMenus_CloseOnEitherPointerButtonOutside()
    {
        foreach (var markup in new[] { ReadContract("WorkspaceExplorer.razor"), ReadContract("ContextCommandMenu.razor") })
        {
            Assert.Contains("@onclick=", markup, StringComparison.Ordinal);
            Assert.Contains("@oncontextmenu=", markup, StringComparison.Ordinal);
            Assert.Contains("@oncontextmenu:preventDefault", markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TopMenus_AreMutuallyExclusiveAndCloseOnOutsidePointerInput()
    {
        var shell = ReadContract("EditorPanel.razor");
        var menu = ReadContract("CommandMenu.razor");
        var navigation = ReadContract("workbench-shell.js");

        Assert.Contains("_openCommandMenu == category", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("@onpointerdown=\"CloseCommandMenu\"", shell, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('pointerdown', onPointerDown, true)", navigation, StringComparison.Ordinal);
        Assert.Contains("DismissCommandMenusAsync", navigation, StringComparison.Ordinal);
        Assert.Contains("@onpointerdown:stopPropagation", menu, StringComparison.Ordinal);
        Assert.Contains("@oncontextmenu=\"CloseAsync\"", menu, StringComparison.Ordinal);
        Assert.Contains("@oncontextmenu:preventDefault", menu, StringComparison.Ordinal);
    }

    [Fact]
    public void Explorer_RemainsMountedWithAnUnboundedKeyboardResizerAndContextualActions()
    {
        var explorer = ReadContract("WorkspaceExplorer.razor");

        Assert.Contains("hidden=\"@(!_snapshot.SidebarVisible)\"", explorer, StringComparison.Ordinal);
        Assert.DoesNotContain("@if (_snapshot.SidebarVisible)", explorer, StringComparison.Ordinal);
        Assert.Contains("role=\"separator\"", explorer, StringComparison.Ordinal);
        Assert.Contains("ResizeKeyDownAsync", explorer, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-valuemin", explorer, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-valuemax", explorer, StringComparison.Ordinal);
        Assert.DoesNotContain("explorer-actions", explorer, StringComparison.Ordinal);
        Assert.Contains("contextNode.Kind == WorkspaceNodeKind.Directory", explorer, StringComparison.Ordinal);
        Assert.Contains("OnContextMenu", ReadContract("WorkspaceTreeNode.razor"), StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Collapse all folders\"", explorer, StringComparison.Ordinal);
        Assert.Contains("CollapseAllAsync", explorer, StringComparison.Ordinal);
    }

    [Fact]
    public void Explorer_IsDockedToTheRightOfTheWholeEditorWorkspace()
    {
        var shell = ReadContract("EditorPanel.razor");
        var editor = shell.IndexOf("<div class=\"editor-workspace\">", StringComparison.Ordinal);
        var explorer = shell.IndexOf("<WorkspaceExplorer", StringComparison.Ordinal);
        var activity = shell.IndexOf("<ActivityRail", StringComparison.Ordinal);

        Assert.True(editor >= 0 && editor < explorer && explorer < activity);
    }

    [Fact]
    public void PackagedWorkbenchAssetManifest_MatchesEveryGeneratedFile()
    {
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "workbench-assets");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(assetRoot, "asset-manifest.json")));
        var root = manifest.RootElement;

        Assert.Equal("0.0.46-24", root.GetProperty("codiconsVersion").GetString());
        Assert.Equal("5.3.0", root.GetProperty("interVersion").GetString());
        Assert.Equal("5.002", root.GetProperty("fastMonoVersion").GetString());
        Assert.Equal("04cd57761e3855986c79724fd5e8f9105ba871b26ef2c795d7ce4f90284726b6",
            root.GetProperty("fastMonoSourceSha256").GetString());
        Assert.True(root.GetProperty("files").TryGetProperty("fast-mono.ttf", out _));
        Assert.True(root.GetProperty("files").TryGetProperty("licenses/fast-mono-OFL-1.1.txt", out _));
        foreach (var file in root.GetProperty("files").EnumerateObject())
        {
            var path = Path.Combine(assetRoot, file.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing packaged workbench asset {file.Name}.");
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(file.Value.GetString(), actual);
        }
    }
}
