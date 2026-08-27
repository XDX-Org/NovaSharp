using NovaSharp.Platform;
using NovaSharp.Solutions;
using Xunit;

namespace NovaSharp.Tests;

public sealed class SolutionExplorerTreeTests
{
    private readonly WorkspacePaths _paths = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NovaSharp.SolutionTree.Tests");

    [Fact]
    public void Create_GroupsProjectDocumentsByFolderAndRetainsDependencies()
    {
        var projectDirectory = Path.Combine(_root, "src", "Application");
        var project = Project(projectDirectory,
            [
                Document("root", Path.Combine(projectDirectory, "Program.cs")),
                Document("nested", Path.Combine(projectDirectory, "Features", "Editing", "Session.cs")),
                Document("folder", Path.Combine(projectDirectory, "Models", "Document.cs")),
            ],
            [new SolutionReferenceSnapshot(SolutionReferenceKind.Project, "Shared")]);

        var tree = SolutionExplorerTree.Create(Snapshot(project), _paths);

        Assert.Equal("Workspace.slnx", tree.Name);
        var projectNode = Assert.Single(tree.Children);
        Assert.Equal("net10.0", projectNode.Detail);
        Assert.Equal(["Dependencies", "Features", "Models", "Program.cs"],
            projectNode.Children.Select(static node => node.Name));
        Assert.Equal("Shared", Assert.Single(projectNode.Children[0].Children).Name);
        Assert.Equal("Session.cs", Assert.Single(Assert.Single(projectNode.Children[1].Children).Children).Name);
    }

    [Fact]
    public void Create_SeparatesLinkedFilesAndHidesIntermediateBuildDirectories()
    {
        var projectDirectory = Path.Combine(_root, "src", "Application");
        var linked = Path.Combine(_root, "shared", "Common.cs");
        var project = Project(projectDirectory,
            [
                Document("linked", linked),
                Document("intermediate", Path.Combine(projectDirectory, "obj", "Debug", "Generated.cs")),
                Document("output", Path.Combine(projectDirectory, "bin", "Debug", "Output.cs")),
            ]);

        var projectNode = Assert.Single(SolutionExplorerTree.Create(Snapshot(project), _paths).Children);

        var linkedFolder = Assert.Single(projectNode.Children);
        Assert.Equal("Linked files", linkedFolder.Name);
        Assert.Equal(linked, Assert.Single(linkedFolder.Children).Path);
    }

    [Fact]
    public void Create_DoesNotMergeFoldersThatDifferOnlyByCase()
    {
        var projectDirectory = Path.Combine(_root, "src", "Application");
        var project = Project(projectDirectory,
            [
                Document("upper", Path.Combine(projectDirectory, "Models", "First.cs")),
                Document("lower", Path.Combine(projectDirectory, "models", "Second.cs")),
            ]);

        var projectNode = Assert.Single(SolutionExplorerTree.Create(Snapshot(project), _paths).Children);

        Assert.Equal(2, projectNode.Children.Count);
        Assert.Contains(projectNode.Children, static node => node.Name == "Models");
        Assert.Contains(projectNode.Children, static node => node.Name == "models");
    }

    private SolutionWorkspaceSnapshot Snapshot(ProjectContextSnapshot project) => new(
        SolutionLoadState.Ready,
        Path.Combine(_root, "Workspace.slnx"),
        "Workspace.slnx",
        [project]);

    private SolutionDocumentSnapshot Document(string id, string path) =>
        new(id, Path.GetFileName(path), path, _paths.ToDocumentUri(path).AbsoluteUri);

    private static ProjectContextSnapshot Project(
        string directory,
        IReadOnlyList<SolutionDocumentSnapshot> documents,
        IReadOnlyList<SolutionReferenceSnapshot>? references = null) => new(
            "application",
            "Application",
            Path.Combine(directory, "Application.csproj"),
            "net10.0",
            true,
            documents,
            references ?? [],
            [],
            "13.0",
            "Enable");
}
