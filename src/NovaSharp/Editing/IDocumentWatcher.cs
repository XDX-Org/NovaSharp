namespace NovaSharp.Editing;

/// <summary>
/// Advisory notification that the file behind the open document may have changed.
/// </summary>
/// <remarks>
/// Advisory is the whole contract. A watcher can miss an event, report one that did not happen, or report several for
/// one write, and it behaves differently on every file system. Nothing NovaSharp does in response may destroy the
/// user's text: a notification prompts a check, and a dirty document wins until the user says otherwise.
/// </remarks>
public interface IDocumentWatcher : IAsyncDisposable
{
    /// <summary>Starts watching <paramref name="path"/>, replacing whatever was being watched before.</summary>
    void Watch(string path);

    /// <summary>Stops watching.</summary>
    void Stop();

    /// <summary>Raised on a background thread after a burst of file-system events has settled.</summary>
    event Action? Changed;
}
