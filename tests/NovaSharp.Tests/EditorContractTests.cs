using NovaSharp.Commands;
using NovaSharp.Editing;
using Xunit;

namespace NovaSharp.Tests;

/// <summary>
/// Guards the editor contracts that are stated in AGENTS.md and ADR 0001 but are not expressible as types: Monaco is
/// the only editor, nothing loads from a CDN, and the packaged bundle is only ever loaded as an ES module.
/// </summary>
public sealed class EditorContractTests
{
    private static string ReadContract(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Contracts", fileName);
        Assert.True(File.Exists(path), $"The contract file {fileName} was not copied next to the tests.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void EditorPanel_ContainsNoTextAreaOrSecondEditorPath()
    {
        var markup = ReadContract("EditorPanel.razor");

        Assert.DoesNotContain("textarea", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contenteditable", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("editor-host", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorPanel_DoesNotRerenderMonacoForPointerOrUnchangedSolutionEvents()
    {
        var panel = ReadContract("EditorPanel.razor");
        var pane = ReadContract("EditorGroupPane.razor");
        var shell = ReadContract("workbench-shell.js");

        Assert.DoesNotContain("@onpointerdown=\"CloseCommandMenu\"", panel, StringComparison.Ordinal);
        Assert.Contains("if (_contexts.SequenceEqual(previous)) return;", panel, StringComparison.Ordinal);
        Assert.Contains("@key=\"_groups.Layout.Id\"", panel, StringComparison.Ordinal);
        Assert.Contains("@key=\"split.First.Id\"", pane, StringComparison.Ordinal);
        Assert.Contains("@key=\"split.Second.Id\"", pane, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.InvariantCulture", pane, StringComparison.Ordinal);
        Assert.Contains("DismissCommandMenusAsync", shell, StringComparison.Ordinal);
        Assert.Contains("command-menu-scrim", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorPanel_SynchronizesTheRestoredProjectContextIntoMonaco()
    {
        var panel = ReadContract("EditorPanel.razor").ReplaceLineEndings("\n");

        Assert.Contains("RefreshProjectContexts();\n            await UpdateLanguageContextAsync();", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void AppStylesheet_StylesNoEditorSurfaceOtherThanMonaco()
    {
        var css = ReadContract("app.css");

        Assert.DoesNotContain("textarea", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndexPage_LoadsTheMonacoStylesheetLocally()
    {
        var html = ReadContract("index.html");

        Assert.Contains("monaco/monaco.css", html, StringComparison.Ordinal);
        Assert.Contains("workbench-assets/fonts.css", html, StringComparison.Ordinal);
        Assert.Contains("workbench-assets/codicon.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndexPage_NeverLoadsTheBundleAsAClassicScript()
    {
        var html = ReadContract("index.html");

        // The bundle resolves its worker URL from import.meta.url. A classic script tag leaves that undefined and the
        // worker silently falls back to the browser thread, which fails the phase.
        Assert.DoesNotContain("monaco.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_ImportsThePackagedBundleAndNothingRemote()
    {
        var module = ReadContract("monaco-editor-host.js");

        Assert.Contains("from './monaco/monaco.js'", module, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn.", module, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditorHost_DisposesEverythingItCreates()
    {
        var module = ReadContract("monaco-editor-host.js");

        Assert.Contains("observer.disconnect()", module, StringComparison.Ordinal);
        Assert.Contains("editor.dispose()", module, StringComparison.Ordinal);
        Assert.Contains("model.dispose()", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_DrivesLayoutFromAResizeObserverRatherThanPolling()
    {
        var module = ReadContract("monaco-editor-host.js");

        Assert.Contains("new ResizeObserver", module, StringComparison.Ordinal);
        Assert.Contains("automaticLayout: false", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_HidesHorizontalScrollbarsInNormalAndDiffEditors()
    {
        var module = ReadContract("monaco-editor-host.js");

        Assert.Equal(2, module.Split("scrollbar: { horizontal: 'hidden' }", StringSplitOptions.None).Length - 1);
        Assert.Contains("const viewEditor = createCodeEditor(viewContainer);", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_UsesRoslynRatherThanWordBasedCSharpSuggestions()
    {
        var module = ReadContract("monaco-editor-host.js");

        Assert.Contains("wordBasedSuggestions: 'off'", module, StringComparison.Ordinal);
        Assert.Contains("variable: monaco.languages.CompletionItemKind.Variable", module, StringComparison.Ordinal);
        Assert.Contains("constant: monaco.languages.CompletionItemKind.Constant", module, StringComparison.Ordinal);
        Assert.Contains("enumMember: monaco.languages.CompletionItemKind.EnumMember", module, StringComparison.Ordinal);
        Assert.Contains("preselect: item.preselect", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_NeverAssignsAWholeModelValueForAnOrdinaryEdit()
    {
        var module = ReadContract("monaco-editor-host.js");

        // setValue replaces the model wholesale: it discards undo history and forces a full resynchronization. Every
        // NovaSharp-originated change goes through pushEditOperations instead.
        Assert.DoesNotContain(".setValue(", module, StringComparison.Ordinal);
        Assert.Contains("pushEditOperations", module, StringComparison.Ordinal);
        Assert.Contains("pushStackElement", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_KeepsAtMostOneReplicationCallInFlight()
    {
        var module = ReadContract("monaco-editor-host.js");

        // The change handler appends and returns; the pump is what talks to .NET. A handler that awaited interop
        // directly would put a .NET round trip in the keystroke-to-paint path, which ADR 0001 forbids.
        Assert.Contains("function onContentChanged(", module, StringComparison.Ordinal);
        Assert.DoesNotContain("async function onContentChanged(", module, StringComparison.Ordinal);
        Assert.Contains("if (document.sending || document.queued.length === 0", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_BindsShortcutsWithoutNamingAnOperatingSystem()
    {
        var module = ReadContract("monaco-editor-host.js");

        // Modifiers are looked up in Monaco's own table rather than decided here. CtrlCmd resolves to the command key
        // on macOS and to control everywhere else; naming a platform would be an operating-system branch in product
        // code, which the supported-platform rule forbids.
        Assert.Contains("monaco.KeyMod[modifier]", module, StringComparison.Ordinal);
        Assert.Contains("monaco.KeyCode[parts[parts.length - 1]]", module, StringComparison.Ordinal);
        Assert.DoesNotContain("navigator.platform", module, StringComparison.Ordinal);
        Assert.DoesNotContain("isMacintosh", module, StringComparison.Ordinal);
        Assert.DoesNotContain("userAgent", module, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbenchCommands_BindShortcutsWithThePlatformNeutralModifier()
    {
        // The bindings themselves live in the registry now, so this is where the rule is enforced.
        var declared = WorkbenchCommands.All
            .SelectMany(id => WorkbenchCommands.Describe(id).Keybindings)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Contains(declared, keybinding => keybinding.Contains("CtrlCmd", StringComparison.Ordinal));
        Assert.DoesNotContain(declared, keybinding => keybinding.Contains("Cmd+", StringComparison.Ordinal)
            && !keybinding.Contains("CtrlCmd", StringComparison.Ordinal));
        Assert.DoesNotContain(declared, keybinding => keybinding.StartsWith("Ctrl+", StringComparison.Ordinal));
    }

    [Fact]
    public void EditorHost_KeepsNoCommandListOfItsOwn()
    {
        var module = ReadContract("monaco-editor-host.js");

        // The registry is authoritative. The editor is handed descriptors and binds what it is given, so a command
        // added, retitled, or rebound in .NET needs no second edit here — and cannot be silently forgotten here.
        Assert.Contains("registerCommands(descriptors)", module, StringComparison.Ordinal);
        Assert.Contains("resolveKeybinding", module, StringComparison.Ordinal);
        foreach (var id in WorkbenchCommands.All)
        {
            Assert.DoesNotContain(id, module, StringComparison.Ordinal);
        }

        Assert.Contains(EditOrigins.User, module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_ReportsAKeybindingItCouldNotBind()
    {
        var module = ReadContract("monaco-editor-host.js");

        // A binding that does not resolve is a shortcut that silently does nothing. It is returned to .NET rather
        // than dropped, so it becomes a notification instead of a mystery.
        Assert.Contains("unresolved.push", module, StringComparison.Ordinal);
        Assert.Contains("new Set(unresolved)", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHost_GivesTheLiveModelBackWhenAComparisonCloses()
    {
        var module = ReadContract("monaco-editor-host.js");

        // The comparison borrows the live model. Disposing the diff view while it still holds it would dispose the
        // open document, so the model is detached first and returned to the editor afterwards.
        Assert.Contains("diffEditor.setModel(null);", module, StringComparison.Ordinal);
        Assert.Contains("originalModel?.dispose();", module, StringComparison.Ordinal);
        Assert.Contains("comparisonSource.editor.setModel(comparisonSource.document.model);", module, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorPanel_ShowsTheEncodingAndLineEndingASaveWillUse()
    {
        var markup = ReadContract("EditorPanel.razor");

        // A document property that changes what a save writes cannot be invisible.
        Assert.Contains("LineEndingLabel", markup, StringComparison.Ordinal);
        Assert.Contains("status-bar", markup, StringComparison.Ordinal);
        Assert.Contains("Encoding?.DisplayName", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceTree_IsAccessibleAndIncrementallyRendered()
    {
        var explorer = ReadContract("WorkspaceExplorer.razor");
        var node = ReadContract("WorkspaceTreeNode.razor");

        Assert.Contains("role=\"tree\"", explorer, StringComparison.Ordinal);
        Assert.Contains("role=\"treeitem\"", node, StringComparison.Ordinal);
        Assert.Contains("aria-expanded", node, StringComparison.Ordinal);
        Assert.Contains("@onkeydown", node, StringComparison.Ordinal);
        Assert.Contains("ArrowRight", node, StringComparison.Ordinal);
        Assert.Contains("ArrowDown", node, StringComparison.Ordinal);
        Assert.Contains("F2", node, StringComparison.Ordinal);
        Assert.Contains("Delete", node, StringComparison.Ordinal);
        Assert.Contains("Take(_visibleChildren)", node, StringComparison.Ordinal);
        var navigation = ReadContract("workspace-explorer.js");
        Assert.DoesNotContain("http://", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target?.focus()", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceTree_ExposesFileMoveAndEditorDropPayloads()
    {
        var explorer = ReadContract("WorkspaceExplorer.razor");
        var node = ReadContract("WorkspaceTreeNode.razor");
        var navigation = ReadContract("workspace-explorer.js");
        var editor = ReadContract("EditorPanel.razor");

        Assert.Contains("draggable=", node, StringComparison.Ordinal);
        Assert.Contains("data-workspace-path", node, StringComparison.Ordinal);
        Assert.Contains("data-workspace-drop-target", node, StringComparison.Ordinal);
        Assert.Contains("OnDrop", node, StringComparison.Ordinal);
        Assert.Contains("Workbench.Explorer.MoveAsync", explorer, StringComparison.Ordinal);
        Assert.Contains("application/x-novasharp-workspace-item", navigation, StringComparison.Ordinal);
        Assert.Contains("application/x-novasharp-workspace-file", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("setData('text/plain'", navigation, StringComparison.Ordinal);
        Assert.Contains("OpenPinnedInGroupAsync", editor, StringComparison.Ordinal);
        Assert.Contains("OpenPinnedAtEdgeAsync", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentTabs_ExposeAccessiblePointerInteractions()
    {
        var markup = ReadContract("EditorPanel.razor") + ReadContract("EditorGroupPane.razor");

        Assert.Contains("role=\"tablist\"", markup, StringComparison.Ordinal);
        Assert.Contains("role=\"tab\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-selected", markup, StringComparison.Ordinal);
        Assert.Contains("AccessibleLabel", markup, StringComparison.Ordinal);
        Assert.Contains("draggable=\"true\"", markup, StringComparison.Ordinal);
        Assert.Contains("@ondrop", markup, StringComparison.Ordinal);
        Assert.Contains("@onmouseup", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("tabs-overflow", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Open editor list", markup, StringComparison.Ordinal);
        Assert.Contains("@onfocusin", markup, StringComparison.Ordinal);
        Assert.Contains("OnFocus=\"FocusGroupAsync\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentTabs_HaveKeyboardEquivalentMoveAndCloseCommands()
    {
        var ids = WorkbenchCommands.All.ToHashSet(StringComparer.Ordinal);

        Assert.Contains(WorkbenchCommands.MoveTabLeft, ids);
        Assert.Contains(WorkbenchCommands.MoveTabRight, ids);
        Assert.Contains(WorkbenchCommands.Close, ids);
        Assert.Contains(WorkbenchCommands.CloseOthers, ids);
        Assert.Contains(WorkbenchCommands.CloseRight, ids);
        Assert.Contains(WorkbenchCommands.CloseSaved, ids);
        Assert.Contains(WorkbenchCommands.CloseAll, ids);
        Assert.All(
            WorkbenchCommands.Describe(WorkbenchCommands.MoveTabLeft).Keybindings
                .Concat(WorkbenchCommands.Describe(WorkbenchCommands.MoveTabRight).Keybindings),
            binding => Assert.Contains("CtrlCmd", binding, StringComparison.Ordinal));
    }

    [Fact]
    public void Explorer_DistinguishesPreviewFromPinnedActivation()
    {
        var explorer = ReadContract("WorkspaceExplorer.razor");
        var node = ReadContract("WorkspaceTreeNode.razor");

        Assert.Contains("PreviewFile", explorer, StringComparison.Ordinal);
        Assert.Contains("OpenFile", explorer, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"ClickAsync\"", node, StringComparison.Ordinal);
        Assert.Contains("@ondblclick=\"ActivateAsync\"", node, StringComparison.Ordinal);
    }
}
