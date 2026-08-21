namespace NovaSharp.Platform;

/// <summary>Where NovaSharp keeps the state that belongs to the user rather than to a workspace.</summary>
/// <remarks>
/// The second half of the platform seam. Product code asks for a directory rather than deciding what a per-user
/// configuration location looks like, because that answer differs on every supported platform and choosing it in
/// feature code is exactly the operating-system branch the parity rule forbids.
/// </remarks>
public interface IApplicationPaths
{
    /// <summary>The directory holding user-scoped configuration. It may not exist yet.</summary>
    string ConfigurationDirectory { get; }
}
