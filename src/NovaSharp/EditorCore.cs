using System.Text.RegularExpressions;

namespace NovaSharp;

internal readonly record struct TextEdit(int Start, int Length, string NewText)
{
    internal string Apply(string text)
    {
        if (Start < 0 || Length < 0 || Start + Length > text.Length)
            throw new ArgumentOutOfRangeException(nameof(Start));
        return string.Concat(text.AsSpan(0, Start), NewText, text.AsSpan(Start + Length));
    }
}

public readonly record struct TextRange(int Start, int Length);
public readonly record struct EditorLine(int Number, string Text, IReadOnlyList<ClassifiedSpan> Spans);
public readonly record struct ClassifiedSpan(int Start, int Length, TokenKind Kind);

public enum TokenKind { Text, Keyword, String, Comment, Number, Type, Method, Property, Field, Namespace }

internal static partial class CSharpTokenizer
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "record",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "var", "virtual", "void", "volatile", "while", "yield"
    ];

    internal static IReadOnlyList<EditorLine> Tokenize(string text)
    {
        var result = new List<EditorLine>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inBlockComment = false;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var spans = new List<ClassifiedSpan>();
            for (var i = 0; i < line.Length;)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    var length = end < 0 ? line.Length - i : end + 2 - i;
                    spans.Add(new(i, length, TokenKind.Comment));
                    i += length;
                    if (end < 0) break;
                    inBlockComment = false;
                }
                else if (line.AsSpan(i).StartsWith("//"))
                {
                    spans.Add(new(i, line.Length - i, TokenKind.Comment));
                    break;
                }
                else if (line.AsSpan(i).StartsWith("/*"))
                {
                    inBlockComment = true;
                    continue;
                }
                else if (line[i] is '"' or '\'')
                {
                    var quote = line[i];
                    var start = i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\') i += Math.Min(2, line.Length - i);
                        else if (line[i++] == quote) break;
                    }
                    spans.Add(new(start, i - start, TokenKind.String));
                }
                else if (char.IsDigit(line[i]))
                {
                    var start = i++;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or '_')) i++;
                    spans.Add(new(start, i - start, TokenKind.Number));
                }
                else if (char.IsLetter(line[i]) || line[i] == '_')
                {
                    var start = i++;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    var word = line[start..i];
                    if (Keywords.Contains(word)) spans.Add(new(start, i - start, TokenKind.Keyword));
                    else if (char.IsUpper(word[0])) spans.Add(new(start, i - start, TokenKind.Type));
                }
                else i++;
            }
            result.Add(new(lineIndex + 1, line, spans));
        }
        return result;
    }

    internal static IReadOnlyList<EditorLine> Tokenize(string text, IReadOnlyList<SemanticSpan> semanticSpans)
    {
        var lines = Tokenize(text).ToArray();
        if (semanticSpans.Count == 0) return lines;
        var lineStart = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineEnd = lineStart + line.Text.Length;
            var semantic = semanticSpans.Where(span => span.Start >= lineStart && span.Start + span.Length <= lineEnd)
                .Select(span => new ClassifiedSpan(span.Start - lineStart, span.Length, SemanticKind(span.Classification))).ToArray();
            if (semantic.Length > 0)
            {
                var baseline = line.Spans.Where(span => !semantic.Any(item => item.Start < span.Start + span.Length
                    && item.Start + item.Length > span.Start));
                lines[index] = line with { Spans = baseline.Concat(semantic).OrderBy(span => span.Start).ToArray() };
            }
            lineStart = lineEnd + NewLineLength(text, lineEnd);
        }
        return lines;
    }

    private static int NewLineLength(string text, int position) => position >= text.Length ? 0
        : text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n' ? 2 : 1;

    private static TokenKind SemanticKind(string classification) => classification switch
    {
        var value when value.Contains("method", StringComparison.OrdinalIgnoreCase) => TokenKind.Method,
        var value when value.Contains("property", StringComparison.OrdinalIgnoreCase) => TokenKind.Property,
        var value when value.Contains("field", StringComparison.OrdinalIgnoreCase) => TokenKind.Field,
        var value when value.Contains("namespace", StringComparison.OrdinalIgnoreCase) => TokenKind.Namespace,
        var value when value.Contains("class", StringComparison.OrdinalIgnoreCase)
            || value.Contains("struct", StringComparison.OrdinalIgnoreCase)
            || value.Contains("interface", StringComparison.OrdinalIgnoreCase)
            || value.Contains("type", StringComparison.OrdinalIgnoreCase) => TokenKind.Type,
        _ => TokenKind.Text
    };
}

internal static class TextSearch
{
    internal static IReadOnlyList<TextRange> Find(string text, string query, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query)) return [];
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<TextRange>();
        for (var offset = 0; offset <= text.Length - query.Length;)
        {
            var index = text.IndexOf(query, offset, comparison);
            if (index < 0) break;
            results.Add(new(index, query.Length));
            offset = index + Math.Max(1, query.Length);
        }
        return results;
    }

    internal static string ReplaceAll(string text, string query, string replacement, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query)) return text;
        return matchCase
            ? text.Replace(query, replacement, StringComparison.Ordinal)
            : Regex.Replace(text, Regex.Escape(query), _ => replacement, RegexOptions.IgnoreCase);
    }
}
