namespace NovaSharp.Commands;

/// <summary>
/// The commands phase 2 registers.
/// </summary>
/// <remarks>
/// Identifiers are constants rather than literals so a keybinding, a toolbar button, and a notification's action all
/// name the same command and cannot drift apart. Titles and bindings live here too: the registry is the single source
/// the editor host is driven from, so the editor no longer keeps its own copy of the list.
/// </remarks>
public static class WorkbenchCommands
{
    /// <summary>The category every phase-2 command is grouped under.</summary>
    public const string Category = "File";

    /// <summary>Open a file, replacing the one that is open.</summary>
    public const string Open = "novasharp.document.open";

    /// <summary>Write the document to its own file.</summary>
    public const string Save = "novasharp.document.save";

    /// <summary>Write the document to a file the user names, and continue editing it there.</summary>
    public const string SaveAs = "novasharp.document.saveAs";

    /// <summary>Discard unsaved changes and re-read the file.</summary>
    public const string Reload = "novasharp.document.reload";

    /// <summary>Show the file on disk beside the editor's text.</summary>
    public const string Compare = "novasharp.document.compare";

    /// <summary>Stop comparing and go back to editing.</summary>
    public const string EndCompare = "novasharp.document.endCompare";

    /// <summary>Keep the editor's text and stop asking about the external change.</summary>
    public const string KeepEditorText = "novasharp.document.keepEditorText";

    /// <summary>Choose the encoding the document is read or written with.</summary>
    public const string ChooseEncoding = "novasharp.document.chooseEncoding";

    /// <summary>Choose the line ending a save writes.</summary>
    public const string ChooseLineEnding = "novasharp.document.chooseLineEnding";

    public const string Close = "novasharp.tabs.close";
    public const string CloseOthers = "novasharp.tabs.closeOthers";
    public const string CloseRight = "novasharp.tabs.closeRight";
    public const string CloseSaved = "novasharp.tabs.closeSaved";
    public const string CloseAll = "novasharp.tabs.closeAll";
    public const string MoveTabLeft = "novasharp.tabs.moveLeft";
    public const string MoveTabRight = "novasharp.tabs.moveRight";
    public const string PreviousTab = "novasharp.tabs.previous";
    public const string NextTab = "novasharp.tabs.next";
    public const string PinTab = "novasharp.tabs.pin";

    public const string OpenWorkspace = "novasharp.workspace.open";
    public const string CloseWorkspace = "novasharp.workspace.close";
    public const string RefreshWorkspace = "novasharp.workspace.refresh";
    public const string ToggleExplorer = "novasharp.workspace.toggleExplorer";
    public const string RevealActiveFile = "novasharp.workspace.revealActiveFile";

    public const string ShowCommandPalette = "novasharp.workbench.showCommandPalette";
    public const string TogglePanel = "novasharp.workbench.togglePanel";
    public const string ChooseEditorFont = "novasharp.workbench.chooseEditorFont";
    public const string SplitEditorLeft = "novasharp.groups.splitLeft";
    public const string SplitEditorRight = "novasharp.groups.splitRight";
    public const string SplitEditorUp = "novasharp.groups.splitUp";
    public const string SplitEditorDown = "novasharp.groups.splitDown";
    public const string FocusPreviousGroup = "novasharp.groups.focusPrevious";
    public const string FocusNextGroup = "novasharp.groups.focusNext";
    public const string MoveEditorToNextGroup = "novasharp.groups.moveToNext";
    public const string CopyEditorToNextGroup = "novasharp.groups.copyToNext";
    public const string CloseEditorGroup = "novasharp.groups.close";
    public const string DistributeEditorGroups = "novasharp.groups.distributeEvenly";

    /// <summary>Returns the descriptor for <paramref name="id"/>, with its title and default bindings.</summary>
    /// <remarks>
    /// Reload, compare, and the two choosers carry no default binding on purpose: phase 2 has no keybinding
    /// customization, so a default claimed here is one the user cannot take back, and each of these is reachable from
    /// the workbench and the palette without one.
    /// </remarks>
    public static CommandDescriptor Describe(string id) => id switch
    {
        Open => new CommandDescriptor(Open, "Open File…", Category, ["CtrlCmd+O"], ShowInPalette: true),
        Save => new CommandDescriptor(Save, "Save", Category, ["CtrlCmd+S"], ShowInPalette: true),
        SaveAs => new CommandDescriptor(SaveAs, "Save As…", Category, ["CtrlCmd+Shift+S"], ShowInPalette: true),
        Reload => new CommandDescriptor(Reload, "Reload From Disk", Category, [], ShowInPalette: true),
        Compare => new CommandDescriptor(Compare, "Compare With File On Disk", Category, [], ShowInPalette: true),
        EndCompare => new CommandDescriptor(EndCompare, "Stop Comparing", Category, [], ShowInPalette: true),
        KeepEditorText => new CommandDescriptor(KeepEditorText, "Keep The Editor's Text", Category, [], ShowInPalette: false),
        ChooseEncoding => new CommandDescriptor(ChooseEncoding, "Change File Encoding…", Category, [], ShowInPalette: true),
        ChooseLineEnding => new CommandDescriptor(ChooseLineEnding, "Change Line Ending…", Category, [], ShowInPalette: true),
        Close => new CommandDescriptor(Close, "Close Editor", "View", ["CtrlCmd+W"], ShowInPalette: true),
        CloseOthers => new CommandDescriptor(CloseOthers, "Close Other Editors", "View", [], ShowInPalette: true),
        CloseRight => new CommandDescriptor(CloseRight, "Close Editors to the Right", "View", [], ShowInPalette: true),
        CloseSaved => new CommandDescriptor(CloseSaved, "Close Saved Editors", "View", [], ShowInPalette: true),
        CloseAll => new CommandDescriptor(CloseAll, "Close All Editors", "View", [], ShowInPalette: true),
        MoveTabLeft => new CommandDescriptor(MoveTabLeft, "Move Editor Left", "View", ["CtrlCmd+Shift+PageUp"], ShowInPalette: true),
        MoveTabRight => new CommandDescriptor(MoveTabRight, "Move Editor Right", "View", ["CtrlCmd+Shift+PageDown"], ShowInPalette: true),
        PreviousTab => new CommandDescriptor(PreviousTab, "Previous Editor", "View", ["CtrlCmd+PageUp"], ShowInPalette: true),
        NextTab => new CommandDescriptor(NextTab, "Next Editor", "View", ["CtrlCmd+PageDown"], ShowInPalette: true),
        PinTab => new CommandDescriptor(PinTab, "Keep Open", "View", [], ShowInPalette: true),
        OpenWorkspace => new CommandDescriptor(OpenWorkspace, "Open Folder…", "Workspace", ["CtrlCmd+Shift+O"], ShowInPalette: true),
        CloseWorkspace => new CommandDescriptor(CloseWorkspace, "Close Folder", "Workspace", [], ShowInPalette: true),
        RefreshWorkspace => new CommandDescriptor(RefreshWorkspace, "Refresh Explorer", "Workspace", [], ShowInPalette: true),
        ToggleExplorer => new CommandDescriptor(ToggleExplorer, "Toggle Explorer", "View", ["CtrlCmd+B"], ShowInPalette: true),
        RevealActiveFile => new CommandDescriptor(RevealActiveFile, "Reveal Active File in Explorer", "Workspace", [], ShowInPalette: true),
        ShowCommandPalette => new CommandDescriptor(ShowCommandPalette, "Show Command Palette", "View", ["CtrlCmd+Shift+P"], ShowInPalette: true),
        TogglePanel => new CommandDescriptor(TogglePanel, "Toggle Bottom Panel", "View", ["CtrlCmd+J"], ShowInPalette: true),
        ChooseEditorFont => new CommandDescriptor(ChooseEditorFont, "Change Editor Font…", "View", [], ShowInPalette: true),
        SplitEditorLeft => new CommandDescriptor(SplitEditorLeft, "Split Editor Left", "View", [], ShowInPalette: true),
        SplitEditorRight => new CommandDescriptor(SplitEditorRight, "Split Editor Right", "View", ["CtrlCmd+Alt+RightArrow"], ShowInPalette: true),
        SplitEditorUp => new CommandDescriptor(SplitEditorUp, "Split Editor Up", "View", [], ShowInPalette: true),
        SplitEditorDown => new CommandDescriptor(SplitEditorDown, "Split Editor Down", "View", ["CtrlCmd+Alt+DownArrow"], ShowInPalette: true),
        FocusPreviousGroup => new CommandDescriptor(FocusPreviousGroup, "Focus Previous Editor Group", "View", [], ShowInPalette: true),
        FocusNextGroup => new CommandDescriptor(FocusNextGroup, "Focus Next Editor Group", "View", [], ShowInPalette: true),
        MoveEditorToNextGroup => new CommandDescriptor(MoveEditorToNextGroup, "Move Editor into Next Group", "View", [], ShowInPalette: true),
        CopyEditorToNextGroup => new CommandDescriptor(CopyEditorToNextGroup, "Copy Editor into Next Group", "View", [], ShowInPalette: true),
        CloseEditorGroup => new CommandDescriptor(CloseEditorGroup, "Close Editor Group", "View", [], ShowInPalette: true),
        DistributeEditorGroups => new CommandDescriptor(DistributeEditorGroups, "Distribute Editor Groups Evenly", "View", [], ShowInPalette: true),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "There is no descriptor for this command."),
    };

    /// <summary>Every command phase 2 defines, in the order the workbench presents them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Open, Save, SaveAs, Reload, Compare, EndCompare, KeepEditorText, ChooseEncoding, ChooseLineEnding,
        Close, CloseOthers, CloseRight, CloseSaved, CloseAll, MoveTabLeft, MoveTabRight, PreviousTab, NextTab, PinTab,
        OpenWorkspace, CloseWorkspace, RefreshWorkspace, ToggleExplorer, RevealActiveFile,
        ShowCommandPalette, TogglePanel, ChooseEditorFont,
        SplitEditorLeft, SplitEditorRight, SplitEditorUp, SplitEditorDown,
        FocusPreviousGroup, FocusNextGroup, MoveEditorToNextGroup, CopyEditorToNextGroup,
        CloseEditorGroup, DistributeEditorGroups,
    ];
}
