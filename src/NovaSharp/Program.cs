using PhotinoEx.Blazor;
using PhotinoEx.Core.Models;

namespace NovaSharp;

internal static class Program
{
    internal static PhotinoExBlazorApp App { get; private set; } = null!;
    internal static EditorDocumentState? ActiveDocument { get; set; }
    internal static ConfigurationService Configuration { get; private set; } = null!;

    [STAThread]
    private static void Main(string[] args)
    {
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
