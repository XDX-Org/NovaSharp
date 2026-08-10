namespace NovaSharp;

internal sealed record WorkspaceEditPreview(string Title, IReadOnlyList<WorkspaceDocumentEdit> Documents,
    int ChangedFileCount, int ChangedCharacterCount, IReadOnlyList<WorkspaceResourceEdit>? Resources = null);

internal sealed class WorkspaceEditTransaction
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal WorkspaceEditPreview Preview(WorkspaceEdit edit) => new(edit.Title, edit.Documents,
        edit.Documents.Count + (edit.Resources?.Count ?? 0),
        edit.Documents.Sum(item => CharacterChanges(item.ExpectedText, item.NewText)), edit.Resources);

    internal async Task ApplyAsync(WorkspaceEdit edit, IEnumerable<EditorDocumentState> openDocuments,
        CancellationToken cancellationToken = default)
    {
        var openList = openDocuments.ToArray();
        foreach (var deletion in (edit.Resources ?? []).Where(item => item.Kind == "delete"))
            if (openList.Any(document => document.FilePath is { } path && Paths.Equals(Path.GetFullPath(path), Path.GetFullPath(deletion.Path))))
                throw new InvalidOperationException("Close an open document before deleting it.");
        var resourceRollbacks = new Stack<Action>();
        var resourceBackups = new List<string>();
        var relocated = new List<(EditorDocumentState Document, string OldPath, string NewPath)>();
        try
        {
            foreach (var operation in edit.Resources ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyResource(operation, resourceRollbacks, resourceBackups);
                if (operation.Kind == "rename" && operation.NewPath is { } destination)
                    foreach (var document in openList.Where(document => document.FilePath is { } current
                                 && Paths.Equals(Path.GetFullPath(current), Path.GetFullPath(operation.Path))))
                    {
                        document.Relocate(operation.Path, destination);
                        relocated.Add((document, operation.Path, destination));
                    }
            }
            await ApplyTextAsync(edit, openList, cancellationToken);
        }
        catch
        {
            foreach (var item in relocated.AsEnumerable().Reverse()) item.Document.Relocate(item.NewPath, item.OldPath);
            while (resourceRollbacks.TryPop(out var rollback)) rollback();
            throw;
        }
        foreach (var backup in resourceBackups)
            if (File.Exists(backup)) File.Delete(backup); else if (Directory.Exists(backup)) Directory.Delete(backup, true);
    }

    private async Task ApplyTextAsync(WorkspaceEdit edit, IEnumerable<EditorDocumentState> openDocuments,
        CancellationToken cancellationToken)
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
            if (document is null && item.ExpectedDiskStamp is { } stamp && DiskStamp.Read(path) != stamp)
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
                await File.WriteAllBytesAsync(stage, EncodeLikeFile(item.DocumentPath, item.NewText), cancellationToken);
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

    private static void ApplyResource(WorkspaceResourceEdit operation, Stack<Action> rollbacks, List<string> backups)
    {
        var path = Path.GetFullPath(operation.Path);
        switch (operation.Kind)
        {
            case "create":
                if (File.Exists(path))
                {
                    if (operation.IgnoreIfExists) return;
                    if (!operation.Overwrite) throw new IOException($"{Path.GetFileName(path)} already exists.");
                    var backup = Backup(path); File.Move(path, backup); backups.Add(backup);
                    rollbacks.Push(() => { if (File.Exists(path)) File.Delete(path); File.Move(backup, path); });
                }
                else rollbacks.Push(() => { if (File.Exists(path)) File.Delete(path); });
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
                break;
            case "rename" when operation.NewPath is { } destination:
                var target = Path.GetFullPath(destination);
                if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Rename source is missing.", path);
                string? replaced = null;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    if (operation.IgnoreIfExists) return;
                    if (!operation.Overwrite) throw new IOException($"{Path.GetFileName(target)} already exists.");
                    replaced = Backup(target); Move(target, replaced); backups.Add(replaced);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); Move(path, target);
                rollbacks.Push(() => { Move(target, path); if (replaced is not null) Move(replaced, target); });
                break;
            case "delete":
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    if (operation.IgnoreIfExists) return;
                    throw new FileNotFoundException("Delete target is missing.", path);
                }
                if (Directory.Exists(path) && !operation.Recursive && Directory.EnumerateFileSystemEntries(path).Any())
                    throw new IOException("A non-recursive delete cannot remove a non-empty directory.");
                var deleted = Backup(path); Move(path, deleted); backups.Add(deleted);
                rollbacks.Push(() => Move(deleted, path));
                break;
            default: throw new InvalidDataException($"Unsupported workspace resource operation '{operation.Kind}'.");
        }
    }

    private static string Backup(string path) => path + $".{Guid.NewGuid():N}.novasharp-backup";
    private static void Move(string source, string target)
    {
        if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
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

    private static byte[] EncodeLikeFile(string path, string text)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> bom = stackalloc byte[3];
        var count = stream.Read(bom);
        if (count >= 2 && bom[0] == 0xff && bom[1] == 0xfe)
            return WithPreamble(System.Text.Encoding.Unicode, text);
        if (count >= 2 && bom[0] == 0xfe && bom[1] == 0xff)
            return WithPreamble(System.Text.Encoding.BigEndianUnicode, text);
        if (count == 3 && bom.SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }))
            return WithPreamble(new System.Text.UTF8Encoding(true), text);
        return new System.Text.UTF8Encoding(false).GetBytes(text);
    }

    private static byte[] WithPreamble(System.Text.Encoding encoding, string text)
    {
        var preamble = encoding.GetPreamble();
        var content = encoding.GetBytes(text);
        return [.. preamble, .. content];
    }
}
