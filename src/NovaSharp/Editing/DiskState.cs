namespace NovaSharp.Editing;

/// <summary>
/// What was last observed about the file behind a document, used to notice that something else changed it.
/// </summary>
/// <param name="Exists">Whether the file was there.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="LastWriteTimeUtc">Its last write time.</param>
/// <param name="ReadOnly">Whether it was marked read-only.</param>
public sealed record DiskState(bool Exists, long Length, DateTimeOffset LastWriteTimeUtc, bool ReadOnly)
{
    /// <summary>The state of a path with no file at it.</summary>
    public static DiskState Missing { get; } = new(Exists: false, Length: 0, DateTimeOffset.MinValue, ReadOnly: false);

    /// <summary>
    /// Returns whether <paramref name="other"/> is the same file NovaSharp last saw.
    /// </summary>
    /// <remarks>
    /// Length and write time, not content. This decides whether to <em>ask</em> the user about an external change, and
    /// hashing every file on every watcher event to avoid an occasional unnecessary question would cost far more than
    /// the question does. A change that keeps both length and timestamp identical is indistinguishable to any watcher
    /// and is left to the explicit reload command.
    /// </remarks>
    public bool Matches(DiskState other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Exists == other.Exists
            && Length == other.Length
            && LastWriteTimeUtc == other.LastWriteTimeUtc;
    }
}
