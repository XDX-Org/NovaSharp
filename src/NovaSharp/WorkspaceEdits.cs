namespace NovaSharp;

internal sealed record WorkspaceEditPreview(string Title, IReadOnlyList<WorkspaceDocumentEdit> Documents,
    int ChangedFileCount, int ChangedCharacterCount);

internal sealed class WorkspaceEditTransaction
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal WorkspaceEditPreview Preview(WorkspaceEdit edit) => new(edit.Title, edit.Documents,
        edit.Documents.Count, edit.Documents.Sum(item => CharacterChanges(item.ExpectedText, item.NewText)));

    internal async Task ApplyAsync(WorkspaceEdit edit, IEnumerable<EditorDocumentState> openDocuments,
        CancellationToken cancellationToken = default)
    {
        var open = openDocuments.Where(item => item.FilePath is not null)
            .ToDictionary(item => Path.GetFullPath(item.FilePath!), Paths);
        var pending = new List<(WorkspaceDocumentEdit Edit, EditorDocumentState? Open)>();
        foreach (var item in edit.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(item.DocumentPath);
            open.TryGetValue(path, out var document);
            var current = document?.Content ?? await File.ReadAllTextAsync(path, cancellationToken);
            if (document is not null && item.ExpectedVersion is { } version && document.Version != version)
                throw new InvalidOperationException($"{Path.GetFileName(path)} changed since the edit was computed.");
            if (!string.Equals(current, item.ExpectedText, StringComparison.Ordinal))
                throw new InvalidOperationException($"{Path.GetFileName(path)} no longer matches the edit preview.");
            pending.Add((item with { DocumentPath = path }, document));
        }

        var staged = new List<(string Target, string Stage, string? Backup)>();
        try
        {
            foreach (var (item, document) in pending.Where(item => item.Open is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(item.DocumentPath)!;
                var stage = Path.Combine(directory, $".{Path.GetFileName(item.DocumentPath)}.{Guid.NewGuid():N}.novasharp");
                await File.WriteAllTextAsync(stage, item.NewText, cancellationToken);
                staged.Add((item.DocumentPath, stage, null));
            }
            for (var index = 0; index < staged.Count; index++)
            {
                var item = staged[index];
                var backup = item.Target + $".{Guid.NewGuid():N}.bak";
                File.Move(item.Target, backup);
                try { File.Move(item.Stage, item.Target); }
                catch { File.Move(backup, item.Target); throw; }
                staged[index] = item with { Backup = backup };
            }
            foreach (var (item, document) in pending.Where(item => item.Open is not null))
                document!.Content = item.NewText;
        }
        catch
        {
            foreach (var item in staged.AsEnumerable().Reverse())
                if (item.Backup is not null)
                {
                    if (File.Exists(item.Target)) File.Delete(item.Target);
                    if (File.Exists(item.Backup)) File.Move(item.Backup, item.Target);
                }
            throw;
        }
        finally
        {
            foreach (var item in staged)
            {
                if (File.Exists(item.Stage)) File.Delete(item.Stage);
                if (item.Backup is not null && File.Exists(item.Backup)) File.Delete(item.Backup);
            }
        }
    }

    private static int CharacterChanges(string before, string after)
    {
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix
               && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        return before.Length + after.Length - prefix * 2 - suffix * 2;
    }
}
