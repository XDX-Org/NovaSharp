using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaSharp;

internal sealed record WorkspaceSearchOptions(
    string Query,
    bool UseRegex = false,
    bool MatchCase = false,
    bool MatchWholeWord = false,
    string[]? IncludeGlobs = null,
    string[]? ExcludeGlobs = null,
    int BatchSize = 64,
    int MaxResults = 10_000,
    TimeSpan? RegexTimeout = null);

public sealed record WorkspaceSearchMatch(string Path, string RelativePath, int Start, int Length,
    int Line, int Column, string Preview, long? DocumentVersion);

internal sealed record WorkspaceSearchIssue(string Path, string Message);

internal sealed record WorkspaceSearchBatch(IReadOnlyList<WorkspaceSearchMatch> Matches,
    IReadOnlyList<WorkspaceSearchIssue> Issues, bool IsComplete, bool IsLimitReached = false);

internal sealed class WorkspaceSearchService
{
    private static readonly HashSet<string> DefaultIgnored = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj" };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _root;
    private readonly HashSet<string> _ignored;

    internal WorkspaceSearchService(string root, IEnumerable<string>? ignoredNames = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);
        _ignored = new(DefaultIgnored.Concat(ignoredNames ?? []), StringComparer.OrdinalIgnoreCase);
    }

    internal async IAsyncEnumerable<WorkspaceSearchBatch> SearchAsync(WorkspaceSearchOptions options,
        IEnumerable<EditorDocumentState>? openDocuments = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(options);
        var expression = CreateExpression(options);
        var open = (openDocuments ?? []).Where(item => item.FilePath is not null)
            .ToDictionary(item => Path.GetFullPath(item.FilePath!), PathComparer);
        var matches = new List<WorkspaceSearchMatch>(options.BatchSize);
        var issues = new List<WorkspaceSearchIssue>();
        var count = 0;

        var paths = await Task.Run(() => EnumerateFiles(options).ToArray(), cancellationToken);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issues.Count == options.BatchSize)
            {
                yield return new(matches.ToArray(), issues.ToArray(), false);
                matches.Clear(); issues.Clear();
                await Task.Yield();
            }
            string text;
            long? version = null;
            try
            {
                if (open.TryGetValue(path, out var document))
                {
                    text = document.Content ?? string.Empty;
                    version = document.Version;
                }
                else text = await ReadTextAsync(path, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or DecoderFallbackException or InvalidDataException)
            {
                issues.Add(new(path, exception.Message));
                continue;
            }

            Match[] fileMatches;
            try
            {
                fileMatches = expression.Matches(text).Cast<Match>().Take(options.MaxResults - count).ToArray();
            }
            catch (RegexMatchTimeoutException exception)
            {
                issues.Add(new(path, exception.Message));
                continue;
            }
            foreach (var match in fileMatches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Normalize(Path.GetRelativePath(_root, path));
                var (line, column, preview) = Locate(text, match.Index);
                matches.Add(new(path, relative, match.Index, match.Length, line, column, preview, version));
                count++;
                if (matches.Count == options.BatchSize)
                {
                    yield return new(matches.ToArray(), issues.ToArray(), false);
                    matches.Clear();
                    issues.Clear();
                    await Task.Yield();
                }
                if (count == options.MaxResults)
                {
                    yield return new(matches.ToArray(), issues.ToArray(), true, true);
                    yield break;
                }
            }
        }

        yield return new(matches.ToArray(), issues.ToArray(), true);
    }

    internal async Task<WorkspaceEdit> CreateReplaceEditAsync(WorkspaceSearchOptions options, string replacement,
        IEnumerable<EditorDocumentState>? openDocuments = null, CancellationToken cancellationToken = default)
    {
        Validate(options);
        var expression = CreateExpression(options);
        var documents = (openDocuments ?? []).Where(item => item.FilePath is not null)
            .ToDictionary(item => Path.GetFullPath(item.FilePath!), PathComparer);
        var edits = new List<WorkspaceDocumentEdit>();
        var replacementCount = 0;
        var paths = await Task.Run(() => EnumerateFiles(options).ToArray(), cancellationToken);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.TryGetValue(path, out var document);
            string current;
            try { current = document?.Content ?? await ReadTextAsync(path, cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or DecoderFallbackException or InvalidDataException) { continue; }
            Match[] matches;
            try
            {
                matches = expression.Matches(current).Cast<Match>()
                    .Take(options.MaxResults - replacementCount).ToArray();
            }
            catch (RegexMatchTimeoutException) { continue; }
            if (matches.Length == 0) continue;
            var changed = ReplaceMatches(current, matches, replacement, options.UseRegex);
            edits.Add(new(path, document?.Version, current, changed,
                document is null ? DiskStamp.Read(path) : null,
                matches.Select(match => new TextRange(match.Index, match.Length)).ToArray()));
            replacementCount += matches.Length;
            if (replacementCount == options.MaxResults) break;
        }
        return new($"Replace '{options.Query}'", edits);
    }

    internal Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(() => EnumerateFiles(new("*", UseRegex: true)).Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return path;
        }).ToArray(), cancellationToken);

    private IEnumerable<string> EnumerateFiles(WorkspaceSearchOptions options)
    {
        var pending = new Stack<string>();
        pending.Push(_root);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in entries.OfType<DirectoryInfo>().Reverse())
                if (!_ignored.Contains(child.Name) && !IsLink(child)) pending.Push(child.FullName);
            foreach (var file in entries.OfType<FileInfo>())
            {
                if (IsLink(file)) continue;
                var relative = Normalize(Path.GetRelativePath(_root, file.FullName));
                if (MatchesGlobs(relative, options.IncludeGlobs, defaultValue: true)
                    && !MatchesGlobs(relative, options.ExcludeGlobs, defaultValue: false))
                    yield return Path.GetFullPath(file.FullName);
            }
        }
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.AsSpan().IndexOf((byte)0) >= 0 && !(bytes.Length >= 2 &&
            ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff))))
            throw new InvalidDataException("Binary file skipped.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static Regex CreateExpression(WorkspaceSearchOptions options)
    {
        var pattern = options.UseRegex ? options.Query : Regex.Escape(options.Query);
        if (options.MatchWholeWord) pattern = $@"(?<![\p{{L}}\p{{N}}_])(?:{pattern})(?![\p{{L}}\p{{N}}_])";
        var regexOptions = RegexOptions.CultureInvariant;
        if (!options.MatchCase) regexOptions |= RegexOptions.IgnoreCase;
        return new(pattern, regexOptions, options.RegexTimeout ?? TimeSpan.FromMilliseconds(250));
    }

    private static string ReplaceMatches(string text, IReadOnlyList<Match> matches, string replacement, bool useRegex)
    {
        var changed = new StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            changed.Append(text, position, match.Index - position);
            changed.Append(useRegex ? match.Result(replacement) : replacement);
            position = match.Index + match.Length;
        }
        changed.Append(text, position, text.Length - position);
        return changed.ToString();
    }

    private static void Validate(WorkspaceSearchOptions options)
    {
        if (string.IsNullOrEmpty(options.Query)) throw new ArgumentException("A search query is required.", nameof(options));
        if (options.BatchSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxResults < 1) throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static (int Line, int Column, string Preview) Locate(string text, int position)
    {
        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < position; index++)
            if (text[index] == '\n') { line++; lineStart = index + 1; }
        var lineEnd = text.IndexOfAny(['\r', '\n'], position);
        if (lineEnd < 0) lineEnd = text.Length;
        return (line, position - lineStart + 1, text[lineStart..lineEnd]);
    }

    private static bool MatchesGlobs(string path, string[]? globs, bool defaultValue)
    {
        if (globs is null || globs.Length == 0) return defaultValue;
        return globs.Any(glob => Regex.IsMatch(path, GlobPattern(glob), RegexOptions.CultureInvariant
            | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None)));
    }

    private static string GlobPattern(string glob)
    {
        var pattern = Regex.Escape(Normalize(glob)).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return "^" + pattern + "$";
    }

    private static bool IsLink(FileSystemInfo item) => item.LinkTarget is not null
        || item.Attributes.HasFlag(FileAttributes.ReparsePoint);
    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
