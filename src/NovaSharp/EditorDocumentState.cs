using System.Text;
using System.Security.Cryptography;

namespace NovaSharp;

internal enum DocumentEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian
}

internal enum LineEnding
{
    Lf,
    CrLf,
    Cr
}

public readonly record struct DiskStamp(long Length, DateTime LastWriteUtc, string ContentHash)
{
    internal static DiskStamp Read(string path)
    {
        var info = new FileInfo(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
    }
}

internal sealed class SaveConflictException(string path)
    : IOException($"'{Path.GetFileName(path)}' changed on disk. Reload it or use Save As.");

public sealed class EditorViewState
{
    internal int SelectionStart { get; private set; }
    internal int SelectionEnd { get; private set; }
    internal double ScrollTop { get; private set; }
    internal double ScrollLeft { get; private set; }

    internal void SetSelection(int start, int end, int textLength)
    {
        SelectionStart = Math.Clamp(start, 0, textLength);
        SelectionEnd = Math.Clamp(end, SelectionStart, textLength);
    }

    internal void Restore(int start, int end, double scrollTop, double scrollLeft, int textLength)
    {
        SetSelection(start, end, textLength);
        ScrollTop = Math.Max(0, scrollTop);
        ScrollLeft = Math.Max(0, scrollLeft);
    }

    internal void SetScroll(double top, double left)
    {
        ScrollTop = Math.Max(0, top);
        ScrollLeft = Math.Max(0, left);
    }

    internal void ApplyTextChange(string oldText, string newText)
    {
        var prefix = 0;
        var sharedLength = Math.Min(oldText.Length, newText.Length);
        while (prefix < sharedLength && oldText[prefix] == newText[prefix]) prefix++;

        var suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix
            && oldText[oldText.Length - suffix - 1] == newText[newText.Length - suffix - 1]) suffix++;

        var oldEnd = oldText.Length - suffix;
        var newEnd = newText.Length - suffix;
        SelectionStart = MapPosition(SelectionStart, prefix, oldEnd, newEnd);
        SelectionEnd = MapPosition(SelectionEnd, prefix, oldEnd, newEnd);
    }

    private static int MapPosition(int position, int start, int oldEnd, int newEnd) => position <= start
        ? position : position >= oldEnd ? position + newEnd - oldEnd : newEnd;
}

public sealed class EditorDocumentState : IDisposable
{
    private string? _content;
    private long _savedVersion;
    private DiskStamp? _diskStamp;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private FileSystemWatcher? _watcher;
    private bool _saving;
    private CancellationTokenSource? _watcherDebounce;

    internal event Action? ExternalChangeDetected;
    internal event Action<EditorSnapshot>? ContentChanged;

    internal string? FilePath { get; private set; }
    internal string DisplayName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);
    internal string? Error { get; private set; }
    internal DocumentEncoding Encoding { get; private set; } = DocumentEncoding.Utf8;
    internal LineEnding LineEnding { get; private set; } = LineEnding.Lf;
    internal long Version { get; private set; }
    internal bool IsDirty => Version != _savedVersion;
    internal bool IsReadOnly => FilePath is not null && File.Exists(FilePath) && new FileInfo(FilePath).IsReadOnly;
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal bool IsDeletedOnDisk => FilePath is not null && !File.Exists(FilePath);
    internal EditorSnapshot CreateSnapshot() => new(FilePath ?? string.Empty, _content ?? string.Empty, Version, IsDirty);
    internal string? Content
    {
        get => _content;
        set => SetContent(value ?? string.Empty, recordUndo: true);
    }

    internal async Task OpenAsync(string? path, Func<string, Task<string>>? readTextAsync = null)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (readTextAsync is not null)
            {
                Load(Path.GetFullPath(path), await readTextAsync(path), DocumentEncoding.Utf8,
                    File.Exists(path) ? DiskStamp.Read(path) : null);
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var (text, encoding) = Decode(bytes);
                Load(Path.GetFullPath(path), text, encoding, DiskStamp.Read(path));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Error = $"Could not open {Path.GetFileName(path)}: {exception.Message}";
        }
    }

    internal void OpenMissing(string path)
    {
        FilePath = Path.GetFullPath(path);
        _content = string.Empty;
        Version++;
        _savedVersion = Version;
        _diskStamp = null;
        _undo.Clear();
        _redo.Clear();
        Error = $"{DisplayName} is missing from disk.";
    }

    internal async Task SaveAsync(string? destination = null, bool overwriteConflict = false)
    {
        var path = destination is null ? FilePath : Path.GetFullPath(destination);
        if (path is null)
        {
            throw new InvalidOperationException("A destination is required for an untitled document.");
        }

        if (destination is null && !overwriteConflict && HasChangedOnDisk())
        {
            throw new SaveConflictException(path);
        }

        if (File.Exists(path) && new FileInfo(path).IsReadOnly)
        {
            throw new UnauthorizedAccessException($"'{Path.GetFileName(path)}' is read-only.");
        }

        var versionToSave = Version;
        var bytes = Encode(NormalizeLineEndings(_content ?? string.Empty, LineEnding), Encoding);
        _saving = true;
        try
        {
            await AtomicFile.WriteAsync(path, bytes);
            FilePath = path;
            _diskStamp = DiskStamp.Read(path);
            _savedVersion = versionToSave;
            Error = null;
            StartWatching(path);
        }
        finally { _saving = false; }
    }

    internal async Task ReloadAsync()
    {
        if (FilePath is not null)
        {
            await OpenAsync(FilePath);
        }
    }

    internal bool HasChangedOnDisk()
    {
        if (FilePath is null || _diskStamp is null)
        {
            return false;
        }

        return !File.Exists(FilePath) || DiskStamp.Read(FilePath) != _diskStamp;
    }

    internal void KeepBuffer()
    {
        _diskStamp = FilePath is not null && File.Exists(FilePath) ? DiskStamp.Read(FilePath) : null;
    }

    internal void Relocate(string oldPath, string newPath)
    {
        if (FilePath is null) return;
        var oldCanonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string relocated;
        if (string.Equals(oldCanonical, FilePath, comparison)) relocated = Path.GetFullPath(newPath);
        else if (FilePath.StartsWith(oldCanonical + Path.DirectorySeparatorChar, comparison))
            relocated = Path.Combine(Path.GetFullPath(newPath), Path.GetRelativePath(oldCanonical, FilePath));
        else return;
        FilePath = relocated;
        _diskStamp = File.Exists(FilePath) ? DiskStamp.Read(FilePath) : null;
        StartWatching(FilePath);
        ContentChanged?.Invoke(CreateSnapshot());
    }

    internal void Undo()
    {
        if (_undo.TryPop(out var value))
        {
            _redo.Push(_content ?? string.Empty);
            SetContent(value, recordUndo: false);
        }
    }

    internal void Redo()
    {
        if (_redo.TryPop(out var value))
        {
            _undo.Push(_content ?? string.Empty);
            SetContent(value, recordUndo: false);
        }
    }

    internal void ApplyEdit(TextEdit edit) => SetContent(edit.Apply(_content ?? string.Empty), recordUndo: true);

    internal void ReplaceAll(string query, string replacement, bool matchCase = false) =>
        SetContent(TextSearch.ReplaceAll(_content ?? string.Empty, query, replacement, matchCase), recordUndo: true);

    internal IReadOnlyList<TextRange> Find(string query, bool matchCase = false) =>
        TextSearch.Find(_content ?? string.Empty, query, matchCase);

    internal IReadOnlyList<EditorLine> CreatePresentationSnapshot() => CSharpTokenizer.Tokenize(_content ?? string.Empty);

    private void Load(string path, string content, DocumentEncoding encoding, DiskStamp? stamp)
    {
        FilePath = path;
        _content = content;
        Encoding = encoding;
        LineEnding = DetectLineEnding(content);
        Version++;
        _savedVersion = Version;
        _diskStamp = stamp;
        _undo.Clear();
        _redo.Clear();
        Error = null;
        StartWatching(path);
        ContentChanged?.Invoke(CreateSnapshot());
    }

    private void SetContent(string value, bool recordUndo)
    {
        if (value == _content)
        {
            return;
        }

        if (recordUndo && _content is not null)
        {
            _undo.Push(_content);
            _redo.Clear();
        }

        _content = value;
        Version++;
        if (FilePath is not null) ContentChanged?.Invoke(CreateSnapshot());
    }

    private static (string Text, DocumentEncoding Encoding) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3), DocumentEncoding.Utf8Bom);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2), DocumentEncoding.Utf16LittleEndian);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2), DocumentEncoding.Utf16BigEndian);
        return (new UTF8Encoding(false, true).GetString(bytes), DocumentEncoding.Utf8);
    }

    private static byte[] Encode(string text, DocumentEncoding encoding)
    {
        Encoding codec = encoding switch
        {
            DocumentEncoding.Utf8 => new UTF8Encoding(false, true),
            DocumentEncoding.Utf8Bom => new UTF8Encoding(true, true),
            DocumentEncoding.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
            DocumentEncoding.Utf16BigEndian => new UnicodeEncoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };
        var body = codec.GetBytes(text);
        if (encoding is not (DocumentEncoding.Utf8Bom or DocumentEncoding.Utf16LittleEndian or DocumentEncoding.Utf16BigEndian))
            return body;
        var preamble = codec.GetPreamble();
        return [.. preamble, .. body];
    }

    private static LineEnding DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        return crlf >= lf && crlf >= cr && crlf > 0 ? LineEnding.CrLf : cr > lf ? LineEnding.Cr : LineEnding.Lf;
    }

    private static string NormalizeLineEndings(string text, LineEnding ending)
    {
        var newline = ending switch { LineEnding.CrLf => "\r\n", LineEnding.Cr => "\r", _ => "\n" };
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newline);
    }

    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) return;
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += OnDiskChanged;
        _watcher.Deleted += OnDiskChanged;
        _watcher.Renamed += OnDiskChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnDiskChanged(object sender, FileSystemEventArgs args)
    {
        if (_saving) return;
        _watcherDebounce?.Cancel();
        _watcherDebounce?.Dispose();
        _watcherDebounce = new();
        _ = NotifyExternalChangeAsync(_watcherDebounce.Token);
    }

    private async Task NotifyExternalChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(100, cancellationToken);
            if (!_saving && HasChangedOnDisk()) ExternalChangeDetected?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { ExternalChangeDetected?.Invoke(); }
        catch (UnauthorizedAccessException) { ExternalChangeDetected?.Invoke(); }
    }

    public void Dispose()
    {
        _watcherDebounce?.Cancel();
        _watcherDebounce?.Dispose();
        _watcherDebounce = null;
        _watcher?.Dispose();
        _watcher = null;
        ExternalChangeDetected = null;
    }
}

internal static class AtomicFile
{
    internal static async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
