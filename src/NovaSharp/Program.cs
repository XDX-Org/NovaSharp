using NovaSharp.Platform;
using NovaSharp.Verification;
using Photino.Blazor;
using Photino.NET;

namespace NovaSharp;

internal static class Program
{
    private static int _closeConfirmed;

    internal static PhotinoBlazorApp App { get; private set; } = null!;

    /// <remarks>
    /// This must stay a synchronous method. The compiler does not carry <see cref="STAThreadAttribute"/> onto the
    /// entry point it synthesises for an <c>async Task Main</c>, so making this asynchronous starts the process in a
    /// multi-threaded apartment and the window host fails with <c>RPC_E_CHANGED_MODE</c>.
    /// </remarks>
    [STAThread]
    private static void Main(string[] args)
    {
        var builder = PhotinoBlazorAppBuilder.CreateDefault(NativeSmokeTest.Configure(args));
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

        if (NativeSmokeTest.ProfilePath is { } profilePath)
        {
            App.MainWindow.SetTemporaryFilesPath(profilePath);
        }

        App.MainWindow.WindowClosing += OnWindowClosing;

        try
        {
            App.Run();
        }
        finally
        {
            // Run returns once the window has closed. Shutdown cancels producers and awaits owned consumers rather
            // than leaving the process to tear them down.
            Workbench.Shutdown();
        }
    }

    /// <summary>Stops a close that would discard unsaved work, and asks instead.</summary>
    /// <remarks>
    /// The callback is synchronous and its answer is needed immediately, so it cannot await a dialog. It cancels the
    /// close, asks in the background, and closes again once the user has said to — which is the only shape that both
    /// keeps the prompt honest and avoids blocking the window's own message loop on it.
    /// </remarks>
    private static bool OnWindowClosing(object sender, EventArgs e)
    {
        var dirty = Workbench.Documents?.Snapshot.DirtyTabs ?? [];
        if (dirty.Count == 0 || Volatile.Read(ref _closeConfirmed) != 0)
        {
            return false;
        }

        _ = ConfirmCloseAsync(dirty);
        return true;
    }

    private static async Task ConfirmCloseAsync(IReadOnlyList<Editing.DocumentTabSnapshot> dirty)
    {
        var names = string.Join("\n", dirty.Select(tab => $"• {tab.Label}"));
        var answer = await App.MainWindow.ShowMessageAsync(
            "NovaSharp",
            $"Close and discard changes in these documents?\n\n{names}",
            PhotinoDialogButtons.YesNo,
            PhotinoDialogIcon.Warning).ConfigureAwait(false);

        if (answer != PhotinoDialogResult.Yes)
        {
            return;
        }

        Interlocked.Exchange(ref _closeConfirmed, 1);
        App.MainWindow.Close();
    }
}
