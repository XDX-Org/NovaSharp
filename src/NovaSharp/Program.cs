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
    internal static string? Phase2SmokeFile { get; private set; }
    internal static string? Phase3Workspace { get; private set; }
    internal static string? Phase4Workspace { get; private set; }
    internal static string? Phase5Workspace { get; private set; }
    internal static string? Phase6Solution { get; private set; }
    internal static string? Phase7Solution { get; private set; }
    internal static string? Phase8Solution { get; private set; }
    internal static string? Phase9Solution { get; private set; }
    internal static string? Phase11Workspace { get; private set; }
    internal static string? Phase15Solution { get; private set; }
    internal static bool UseMonacoEditor { get; private set; }
    internal static Func<bool>? ConfirmWorkbenchClose { get; set; }
    private static string? SmokeReport { get; set; }

    [STAThread]
    private static void Main(string[] args)
    {
        Phase2SmokeFile = ReadOption(args, "--phase2-smoke");
        Phase3Workspace = ReadOption(args, "--phase3-smoke");
        Phase4Workspace = ReadOption(args, "--phase4-smoke");
        Phase5Workspace = ReadOption(args, "--phase5-smoke");
        Phase6Solution = ReadOption(args, "--phase6-smoke");
        Phase7Solution = ReadOption(args, "--phase7-smoke");
        Phase8Solution = ReadOption(args, "--phase8-smoke");
        Phase9Solution = ReadOption(args, "--phase9-smoke");
        Phase11Workspace = ReadOption(args, "--phase11-smoke");
        Phase15Solution = ReadOption(args, "--phase15-smoke");
        UseMonacoEditor = !args.Contains("--legacy-editor", StringComparer.Ordinal);
        SmokeFile = Phase2SmokeFile ?? (Phase3Workspace is null ? null : Path.Combine(Phase3Workspace, "active.cs"))
            ?? (Phase15Solution is { } webSolution ? Path.Combine(Path.GetDirectoryName(webSolution)!, "Index.razor")
                : (Phase9Solution ?? Phase8Solution ?? Phase7Solution) is not { } languageSolution ? null
                : Path.Combine(Path.GetDirectoryName(languageSolution)!, "Program.cs"));
        SmokeReport = ReadOption(args, "--smoke-report");
        var builder = PhotinoExBlazorAppBuilder.CreateDefault("com.xdxorg.nova", args: args);
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
            .SetMinHeight(480)
            .SetZoom(Configuration.Current.Zoom);
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
        await CloseSmokeWindowAsync();
    }

    internal static async Task CompletePhase3SmokeAsync(Phase3SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase4SmokeAsync(Phase4SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase5SmokeAsync(Phase5SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase6SmokeAsync(Phase6SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase7SmokeAsync(Phase7SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase8SmokeAsync(Phase8SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase9SmokeAsync(Phase9SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase11SmokeAsync(Phase11SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    internal static async Task CompletePhase15SmokeAsync(Phase15SmokeResult result)
    {
        if (SmokeReport is null) return;
        await WriteSmokeAndCloseAsync(result);
    }

    private static async Task WriteSmokeAndCloseAsync<T>(T result)
    {
        await File.WriteAllTextAsync(SmokeReport!, JsonSerializer.Serialize(result));
        await CloseSmokeWindowAsync();
    }

    private static async Task CloseSmokeWindowAsync()
    {
        await Task.Delay(250);
        App.MainWindow.Close();
    }

    private static string? ReadOption(string[] args, string name)
    {
        var prefix = name + "=";
        return args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    private static bool ConfirmClose(object? sender, EventArgs? args)
    {
        if (ConfirmWorkbenchClose is not null) return ConfirmWorkbenchClose();
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
