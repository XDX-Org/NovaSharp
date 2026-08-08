namespace NovaSharp;

public enum QuickAccessKind { File, Command }

public sealed record QuickAccessItem(QuickAccessKind Kind, string Id, string Label, string Detail,
    string? Keybinding = null, int Score = 0);

internal sealed class QuickAccessService(CommandRegistry commands)
{
    internal IReadOnlyList<QuickAccessItem> FindCommands(string query, int limit = 100) => commands.Commands
        .Where(command => command.CanExecute())
        .Select(command => new QuickAccessItem(QuickAccessKind.Command, command.Id, command.Title,
            command.Id, command.Keybinding, Rank(command.Title, query)))
        .Where(item => item.Score >= 0)
        .OrderByDescending(item => item.Score).ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
        .Take(limit).ToArray();

    internal async Task<IReadOnlyList<QuickAccessItem>> FindFilesAsync(string root, string query,
        IEnumerable<string>? ignoredNames = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var service = new WorkspaceSearchService(root, ignoredNames);
        var files = await service.ListFilesAsync(cancellationToken);
        return files.Select(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                return new QuickAccessItem(QuickAccessKind.File, path, Path.GetFileName(path), relative,
                    Score: Math.Max(Rank(Path.GetFileName(path), query), Rank(relative, query) - 10));
            }).Where(item => item.Score >= 0)
            .OrderByDescending(item => item.Score).ThenBy(item => item.Detail, StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToArray();
    }

    private static int Rank(string candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var exact = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0) return 10_000 - exact * 10 - candidate.Length;
        var score = 0;
        var position = 0;
        foreach (var character in query)
        {
            var found = candidate.IndexOf(character.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            score += found == position ? 10 : 1;
            position = found + 1;
        }
        return score - candidate.Length;
    }
}
