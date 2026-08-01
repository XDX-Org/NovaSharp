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

internal readonly record struct TextRange(int Start, int Length);
public readonly record struct EditorLine(int Number, string Text, IReadOnlyList<ClassifiedSpan> Spans);
public readonly record struct ClassifiedSpan(int Start, int Length, TokenKind Kind);

public enum TokenKind { Text, Keyword, String, Comment, Number, Type }

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
