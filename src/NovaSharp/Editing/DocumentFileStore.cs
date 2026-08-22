namespace NovaSharp.Editing;

/// <inheritdoc cref="IDocumentFileStore"/>
public sealed class DocumentFileStore : IDocumentFileStore
{
    /// <inheritdoc />
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes through a temporary sibling and then replaces the original in one step.
    /// </summary>
    /// <remarks>
    /// A sibling rather than a file in a temporary directory, because a rename is only atomic within one volume and a
    /// temporary directory is frequently on another one. The replacement itself is attempted in the order that
    /// preserves the most: <see cref="File.Replace(string, string, string?)"/> keeps the original's attributes and
    /// access control where the file system supports it, and a plain overwriting move is the documented degradation
    /// where it does not. Either way the original is whole until the moment it is the new file.
    /// </remarks>
    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)
            ?? throw new IOException($"'{path}' has no containing directory to write into.");

        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.novasharp-{Guid.NewGuid():N}.tmp");

        try
        {
            var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            });

            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Replace(temporary, full);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <inheritdoc />
    public DiskState GetState(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);
        return info.Exists
            ? new DiskState(
                Exists: true,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.IsReadOnly)
            : DiskState.Missing;
    }

    private static void Replace(string temporary, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporary, destination);
            return;
        }

        try
        {
            File.Replace(temporary, destination, destinationBackupFileName: null);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            // Some file systems cannot replace in place. An overwriting move is still a single rename, so the original
            // is never observed truncated; only its attributes and access control come from the temporary file.
            File.Move(temporary, destination, overwrite: true);
        }
    }
}
