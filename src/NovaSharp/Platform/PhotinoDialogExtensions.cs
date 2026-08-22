using Photino.NET;

namespace NovaSharp.Platform;

internal static class PhotinoDialogExtensions
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<PhotinoDialogResult> ShowMessageAsync(
        this PhotinoWindow window,
        string title,
        string message,
        PhotinoDialogButtons buttons,
        PhotinoDialogIcon icon)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => window.ShowMessage(title, message, buttons, icon)).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
