namespace NovaSharp.Platform;

/// <inheritdoc cref="IWorkspacePaths"/>
public sealed class WorkspacePaths : IWorkspacePaths
{
    /// <inheritdoc />
    public Uri ToDocumentUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // GetFullPath resolves relative segments and the current directory using the rules of the running platform;
        // the Uri constructor then applies that platform's file-URI form. Neither step needs an operating-system check.
        var absolute = Path.GetFullPath(path);
        return new Uri(absolute, UriKind.Absolute);
    }

    /// <inheritdoc />
    public string ToDisplayName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Identity is exact. Deciding when two differently cased paths are one document depends on the behavior of the
    /// file system actually holding them, which NovaSharp cannot know until it watches that file system. That question
    /// belongs to the workspace explorer and its watcher, not to a guess made here.
    /// </remarks>
    public bool IsSameDocument(Uri left, Uri right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);
    }
}
