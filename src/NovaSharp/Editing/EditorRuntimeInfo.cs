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
/// <param name="ExternalRequestCount">Requests the page made to an origin other than its own. Must stay zero.</param>
public sealed record EditorRuntimeInfo(
    string MonacoVersion,
    bool DedicatedWorker,
    int ModelCount,
    int ExternalRequestCount);
