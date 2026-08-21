namespace NovaSharp.Editing;

/// <summary>Reads and writes the bytes behind a document.</summary>
/// <remarks>
/// Bytes, not text. Decoding is a document-lifecycle decision that depends on the encoding catalogue and the user's
/// settings, so it does not belong to whatever happens to be holding the file.
/// </remarks>
public interface IDocumentFileStore
{
    /// <summary>Reads the whole file at <paramref name="path"/>.</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> so that an interrupted write cannot damage what is
    /// already there.
    /// </summary>
    Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Reads the file metadata behind <paramref name="path"/>.</summary>
    /// <remarks>
    /// Synchronous because the framework exposes no asynchronous metadata call. Callers run it on a background worker
    /// with everything else in this interface; it is never called from a UI or Monaco callback.
    /// </remarks>
    DiskState GetState(string path);
}
