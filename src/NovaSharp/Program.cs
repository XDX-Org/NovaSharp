using PhotinoEx.Blazor;
using PhotinoEx.Core.Models;
using System.Text.Json;

namespace NovaSharp;

internal static class Program
{
    internal static PhotinoExBlazorApp App { get; private set; } = null!;
    internal static EditorDocumentState? ActiveDocument { get; set; }
    internal static ConfigurationService Configuration { get; private set; } = null!;
    internal static string? SmokeFile { get; private set; }
    private static string? SmokeReport { get; set; }

    [STAThread]
    private static void Main(string[] args)
    {
        SmokeFile = ReadOption(args, "--phase2-smoke");
        SmokeReport = ReadOption(args, "--smoke-report");
        var builder = PhotinoExBlazorAppBuilder.CreateDefault(args);
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaSharp", "settings.json");
        Configuration = new(settingsPath);
        Configuration.LoadAsync().GetAwaiter().GetResult();
        builder.RootComponents.Add<App>("app");

        App = builder.Build();
        App.MainWindow
            .SetLogVerbosity(0)
            .SetTitle("NovaSharp")
            .SetUseOsDefaultSize(false)
            .SetWidth(1200)
            .SetHeight(800)
            .SetMinWidth(640)
            .SetMinHeight(480);
        App.MainWindow.RegisterWindowClosingHandler(ConfirmClose);

        App.Run();
    }

    internal static async Task CompleteSmokeAsync(EditorSmokeResult result)
    {
        if (SmokeReport is null) return;
        var document = ActiveDocument;
        if (document is { IsDirty: true }) await document.ReloadAsync();
        var report = new
        {
            result.InputPresent,
            result.SelectionReplacement,
            result.BracketPairing,
            result.TabInsertion,
            result.CompositionCommittedOnce,
            result.RowsBounded,
            DocumentLoaded = document is { Content: not null } && document.FilePath == SmokeFile,
            DocumentClean = document is { IsDirty: false },
            result.RenderedRows
        };
        await File.WriteAllTextAsync(SmokeReport, JsonSerializer.Serialize(report));
        App.MainWindow.Close();
    }

    private static string? ReadOption(string[] args, string name)
    {
        var prefix = name + "=";
        return args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    private static bool ConfirmClose(object? sender, EventArgs? args)
    {
        var document = ActiveDocument;
        if (document is null || !document.IsDirty) return true;
        var result = App.MainWindow.ShowMessageDialogAsync(
            "Unsaved changes", $"Save changes to {document.DisplayName} before closing?",
            DialogButtons.YesNoCancel, DialogIcon.Warning).GetAwaiter().GetResult();
        if (result == DialogResult.No) return true;
        if (result != DialogResult.Yes) return false;
        try
        {
            document.SaveAsync().GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }
}
