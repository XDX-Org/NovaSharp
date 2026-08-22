namespace NovaSharp.Platform;

/// <inheritdoc cref="IWorkspacePaths"/>
public sealed class WorkspacePaths : IWorkspacePaths
{
    public string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <inheritdoc />
    public Uri ToDocumentUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // GetFullPath resolves relative segments and the current directory using the rules of the running platform;
        // the Uri constructor then applies that platform's file-URI form. Neither step needs an operating-system check.
        var absolute = Canonicalize(path);
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

    public bool IsSamePath(string left, string right) =>
        string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    public bool IsDescendantOrSelf(string root, string path)
    {
        var relative = Path.GetRelativePath(Canonicalize(root), Canonicalize(path));
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    public string ToWorkspaceRelativePath(string root, string path)
    {
        if (!IsDescendantOrSelf(root, path))
        {
            throw new ArgumentException("The path is outside the workspace.", nameof(path));
        }

        var relative = Path.GetRelativePath(Canonicalize(root), Canonicalize(path));
        return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public string ResolveWorkspaceRelativePath(string root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var platformRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var resolved = Canonicalize(Path.Combine(Canonicalize(root), platformRelative));
        if (!IsDescendantOrSelf(root, resolved))
        {
            throw new ArgumentException("The persisted path escapes the workspace.", nameof(relativePath));
        }

        return resolved;
    }
}
