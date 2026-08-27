namespace NovaSharp.Editing;

/// <summary>
/// What the editor host reports about itself, so the phase-1 gates can be asserted by a test instead of claimed.
/// </summary>
/// <param name="MonacoVersion">The Monaco version actually loaded in the page.</param>
/// <param name="DedicatedWorker">
/// Whether the editor worker started as a real dedicated worker. <see langword="false"/> means Monaco fell back to
/// the browser thread, which fails the phase.
/// </param>
/// <param name="ModelCount">Live Monaco text models, used to prove disposal actually released them.</param>
/// <param name="DocumentLength">UTF-16 length of the attached model, read without copying its text.</param>
/// <param name="ExternalRequestCount">Requests the page made to an origin other than its own. Must stay zero.</param>
/// <param name="ReplicationCapacity">Maximum edit batches retained by the browser-side pump.</param>
/// <param name="ReplicationQueueDepth">Edit batches currently waiting for the next interop send.</param>
/// <param name="ReplicationMaximumQueueDepth">Largest observed browser-side queue depth.</param>
/// <param name="ReplicationOverflowCount">Times the browser queue recovered through a full snapshot.</param>
public sealed record EditorRuntimeInfo(
    string MonacoVersion,
    bool DedicatedWorker,
    int ModelCount,
    int ExternalRequestCount,
    int DocumentLength = 0,
    int ReplicationCapacity = 256,
    int ReplicationQueueDepth = 0,
    int ReplicationMaximumQueueDepth = 0,
    int ReplicationOverflowCount = 0,
    int LanguageProviderCount = 0,
    int LanguageRequestCount = 0,
    double LanguageRequestP95Milliseconds = 0);
