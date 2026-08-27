using NovaSharp.Commands;
using Xunit;

namespace NovaSharp.Tests;

public sealed class SolutionWorkbenchContractTests
{
    [Fact]
    public void Explorer_ExposesAccessibleProjectProgressAndFileActions()
    {
        var explorer = Read("WorkspaceExplorer.razor");
        var tree = Read("SolutionTree.razor");
        var node = Read("SolutionTreeNode.razor");

        Assert.Contains("aria-label=\"Open solution or project\"", explorer, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", tree, StringComparison.Ordinal);
        Assert.Contains("aria-busy", tree, StringComparison.Ordinal);
        Assert.Contains("OnOpen.InvokeAsync", node, StringComparison.Ordinal);
        Assert.Contains("role=\"treeitem\"", node, StringComparison.Ordinal);
        Assert.Contains("class=\"tree-row\"", node, StringComparison.Ordinal);
        Assert.Contains("Show next @Math.Min(250", node, StringComparison.Ordinal);
    }

    [Fact]
    public void Explorer_SwitchesBetweenAccessibleFolderAndSolutionViews()
    {
        var explorer = Read("WorkspaceExplorer.razor");

        Assert.Contains("aria-label=\"Explorer view\"", explorer, StringComparison.Ordinal);
        Assert.Contains(">Folder view</option>", explorer, StringComparison.Ordinal);
        Assert.Contains(">Solution view</option>", explorer, StringComparison.Ordinal);
        Assert.Contains("_view == ExplorerView.Solution", explorer, StringComparison.Ordinal);
        Assert.Contains("_solutionTree.CollapseAllAsync()", explorer, StringComparison.Ordinal);
    }

    [Fact]
    public void Explorer_ExposesAccessibleRoslynCancellationAndAWorkbenchCommand()
    {
        var explorer = Read("WorkspaceExplorer.razor");
        var tree = Read("SolutionTree.razor");

        Assert.Contains("aria-label=\"Cancel solution loading\"", tree, StringComparison.Ordinal);
        Assert.Contains("CancelLoad.InvokeAsync", tree, StringComparison.Ordinal);
        Assert.Contains("CancelLoad=\"CancelSolutionLoadAsync\"", explorer, StringComparison.Ordinal);
        Assert.Contains(WorkbenchCommands.CancelSolutionLoad, WorkbenchCommands.All);
        Assert.True(WorkbenchCommands.Describe(WorkbenchCommands.CancelSolutionLoad).ShowInPalette);
    }

    [Fact]
    public void Explorer_DiscardsOutOfOrderSolutionSnapshotsAndReadsTheLatestState()
    {
        var explorer = Read("WorkspaceExplorer.razor");

        Assert.Contains("snapshot.Version <= _solution.Version", explorer, StringComparison.Ordinal);
        Assert.Contains("_solution = Workbench.Solutions.Snapshot;", explorer, StringComparison.Ordinal);
    }

    private static string Read(string file)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", file));
    }
}
