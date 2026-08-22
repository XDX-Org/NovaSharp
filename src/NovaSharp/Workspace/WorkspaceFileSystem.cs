using NovaSharp.Async;
using NovaSharp.Platform;

namespace NovaSharp.Workspace;

public sealed record WorkspaceEntry(string Path, string Name, WorkspaceNodeKind Kind, bool IsDirectoryLink = false);

public interface IWorkspaceFileSystem
{
    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken);
    Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceEntry>> EnumerateAsync(
        string root,
        string directory,
        IReadOnlyList<string> ignoredPaths,
        string? explicitlyVisiblePath,
        CancellationToken cancellationToken);

    Task CreateAsync(string path, bool directory, CancellationToken cancellationToken);
    Task MoveAsync(string source, string target, bool directory, CancellationToken cancellationToken);
    Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken);
}

public sealed class WorkspaceFileSystem : IWorkspaceFileSystem
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".html", ".htm", ".css", ".json", ".sln", ".slnx", ".csproj",
        ".props", ".targets", ".xml", ".md", ".txt", ".yaml", ".yml",
    };

    private readonly IWorkspacePaths _paths;
    private readonly BoundedWorkQueue _queue;

    public WorkspaceFileSystem(IWorkspacePaths paths, BoundedWorkQueue queue)
    {
        _paths = paths;
        _queue = queue;
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Directory.Exists(path));
        }, cancellationToken);

    public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(File.Exists(path) || Directory.Exists(path));
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceEntry>> EnumerateAsync(
        string root,
        string directory,
        IReadOnlyList<string> ignoredPaths,
        string? explicitlyVisiblePath,
        CancellationToken cancellationToken) =>
        _queue.EnqueueAsync<IReadOnlyList<WorkspaceEntry>>(token =>
        {
            token.ThrowIfCancellationRequested();
            var entries = new List<WorkspaceEntry>();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (IsIgnored(root, path, ignoredPaths, explicitlyVisiblePath))
                {
                    continue;
                }

                var attributes = File.GetAttributes(path);
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                var kind = isLink
                    ? WorkspaceNodeKind.SymbolicLink
                    : (attributes & FileAttributes.Directory) != 0
                        ? WorkspaceNodeKind.Directory
                        : SupportedExtensions.Contains(Path.GetExtension(path))
                            ? WorkspaceNodeKind.SupportedFile
                            : WorkspaceNodeKind.UnknownFile;
                entries.Add(new WorkspaceEntry(
                    _paths.Canonicalize(path),
                    Path.GetFileName(path),
                    kind,
                    isLink && (attributes & FileAttributes.Directory) != 0));
            }

            entries.Sort(static (left, right) =>
            {
                var directories = (left.Kind == WorkspaceNodeKind.Directory ? 0 : 1)
                    .CompareTo(right.Kind == WorkspaceNodeKind.Directory ? 0 : 1);
                return directories != 0
                    ? directories
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });
            return Task.FromResult<IReadOnlyList<WorkspaceEntry>>(entries);
        }, cancellationToken);

    public Task CreateAsync(string path, bool directory, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException("The target already exists.");
            }
            if (directory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken);

    public Task MoveAsync(string source, string target, bool directory, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            if (directory)
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }

            return Task.FromResult(true);
        }, cancellationToken);

    public Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            if (directory || Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            return Task.FromResult(true);
        }, cancellationToken);

    private bool IsIgnored(string root, string path, IReadOnlyList<string> patterns, string? explicitlyVisiblePath)
    {
        if (explicitlyVisiblePath is not null
            && (_paths.IsSamePath(path, explicitlyVisiblePath)
                || _paths.IsDescendantOrSelf(path, explicitlyVisiblePath)))
        {
            return false;
        }

        var relative = _paths.ToWorkspaceRelativePath(root, path);
        var name = Path.GetFileName(path);
        return patterns.Any(pattern =>
            string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase)
            || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true)
            || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
    }
}
