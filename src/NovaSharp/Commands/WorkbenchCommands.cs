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
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "There is no descriptor for this command."),
    };

    /// <summary>Every command phase 2 defines, in the order the workbench presents them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Open, Save, SaveAs, Reload, Compare, EndCompare, KeepEditorText, ChooseEncoding, ChooseLineEnding,
    ];
}
