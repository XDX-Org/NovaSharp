using NovaSharp.Platform;

namespace NovaSharp.Solutions;

public enum SolutionExplorerNodeKind
{
    Solution,
    Project,
    Folder,
    Document,
    Dependencies,
    Reference,
}

public sealed record SolutionExplorerNode(
    string Id,
    string Name,
    SolutionExplorerNodeKind Kind,
    string? Path = null,
    string? Detail = null,
    IReadOnlyList<SolutionExplorerNode>? Children = null)
{
    public IReadOnlyList<SolutionExplorerNode> Children { get; init; } = Children ?? [];

    public bool CanExpand => Kind is SolutionExplorerNodeKind.Solution
        or SolutionExplorerNodeKind.Project
        or SolutionExplorerNodeKind.Folder
        or SolutionExplorerNodeKind.Dependencies;
}

public static class SolutionExplorerTree
{
    public static SolutionExplorerNode Create(SolutionWorkspaceSnapshot snapshot, IWorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(paths);

        var identity = snapshot.Path is null ? "solution" : paths.ToDocumentUri(snapshot.Path).AbsoluteUri;
        return new SolutionExplorerNode(
            identity,
            snapshot.Name ?? "Solution",
            SolutionExplorerNodeKind.Solution,
            snapshot.Path,
            Children: snapshot.Projects.Select(project => CreateProject(project, paths)).ToArray());
    }

    private static SolutionExplorerNode CreateProject(ProjectContextSnapshot project, IWorkspacePaths paths)
    {
        var identity = $"project:{paths.ToDocumentUri(project.Path).AbsoluteUri}:{project.TargetFramework}";
        var projectDirectory = Path.GetDirectoryName(project.Path)!;
        var root = new FolderBuilder(identity, projectDirectory);

        foreach (var document in project.Documents)
        {
            var inProject = paths.IsDescendantOrSelf(projectDirectory, document.Path);
            var segments = inProject
                ? paths.ToWorkspaceRelativePath(projectDirectory, document.Path).Split('/', StringSplitOptions.RemoveEmptyEntries)
                : ["Linked files", document.Name];
            if (segments.Length == 0 || segments.Any(static segment => segment is "bin" or "obj")) continue;

            var folder = root;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var path = inProject ? Path.Combine(projectDirectory, Path.Combine(segments[..(index + 1)])) : null;
                folder = folder.GetOrAdd(segments[index], path);
            }

            folder.Documents.Add(new SolutionExplorerNode(
                $"{identity}:document:{document.Id}",
                document.Name,
                SolutionExplorerNodeKind.Document,
                document.Path));
        }

        var children = new List<SolutionExplorerNode>();
        if (project.References.Count > 0)
        {
            children.Add(new SolutionExplorerNode(
                $"{identity}:dependencies",
                "Dependencies",
                SolutionExplorerNodeKind.Dependencies,
                Detail: project.References.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Children: project.References.Select((reference, index) => new SolutionExplorerNode(
                    $"{identity}:reference:{index}",
                    reference.Name,
                    SolutionExplorerNodeKind.Reference,
                    reference.Path,
                    reference.Kind.ToString())).ToArray()));
        }
        children.AddRange(root.BuildChildren());

        return new SolutionExplorerNode(identity, project.Name, SolutionExplorerNodeKind.Project, project.Path,
            project.TargetFramework, children);
    }

    private sealed class FolderBuilder(string identity, string? path)
    {
        private readonly Dictionary<string, FolderBuilder> _folders = new(StringComparer.Ordinal);

        public string Identity { get; } = identity;
        public string? Path { get; } = path;
        public List<SolutionExplorerNode> Documents { get; } = [];

        public FolderBuilder GetOrAdd(string name, string? path)
        {
            if (!_folders.TryGetValue(name, out var folder))
            {
                _folders[name] = folder = new FolderBuilder($"{Identity}:folder:{name}", path);
            }
            return folder;
        }

        public IEnumerable<SolutionExplorerNode> BuildChildren()
        {
            foreach (var (name, folder) in _folders.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                yield return new SolutionExplorerNode(folder.Identity, name, SolutionExplorerNodeKind.Folder, folder.Path,
                    Children: folder.BuildChildren().ToArray());
            }

            foreach (var document in Documents.OrderBy(static document => document.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return document;
            }
        }
    }
}
