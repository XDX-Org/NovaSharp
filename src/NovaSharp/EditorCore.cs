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

public enum TokenKind { Text, Keyword, String, Comment, Number, Type, EnumType, Parameter, Method, Property, Field, Event, Namespace,
    HtmlTag, HtmlAttribute, RazorTransition, RegexEscape, RegexGroup, RegexCharacterClass, RegexQuantifier }

internal static partial class CSharpTokenizer
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "partial", "private", "protected", "public", "readonly", "record",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "var", "virtual", "void", "volatile", "while", "yield", "get", "set", "add", "remove"
    ];

    internal static IReadOnlyList<EditorLine> Tokenize(string text)
    {
        var result = new List<EditorLine>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inBlockComment = false;
        var inEnum = false;
        var enumDepth = 0;
        var awaitingEnumName = false;
        var awaitingEnumBody = false;
        var awaitingDeclarationType = false;
        var awaitingEventType = false;
        var awaitingEventName = false;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var spans = new List<ClassifiedSpan>();
            var namespaceContext = false;
            var inAttributeList = false;
            var attributeArgumentDepth = 0;
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
                    if (quote == '"' && line[..start].Contains("GeneratedRegex", StringComparison.Ordinal))
                        AddRegexSpans(spans, line, start, i);
                    else spans.Add(new(start, i - start, TokenKind.String));
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
                    if (Keywords.Contains(word))
                    {
                        spans.Add(new(start, i - start, TokenKind.Keyword));
                        if (word == "enum") { awaitingEnumName = true; awaitingEnumBody = true; }
                        if (word == "record") awaitingDeclarationType = true;
                        if (word == "event") awaitingEventType = true;
                        if (word is "using" or "namespace") namespaceContext = true;
                    }
                    else if (awaitingEnumName)
                    {
                        spans.Add(new(start, i - start, TokenKind.EnumType));
                        awaitingEnumName = false;
                    }
                    else if (awaitingDeclarationType)
                    {
                        spans.Add(new(start, i - start, TokenKind.Type));
                        awaitingDeclarationType = false;
                    }
                    else if (inAttributeList && attributeArgumentDepth == 0)
                        spans.Add(new(start, i - start, TokenKind.Type));
                    else if (inEnum) spans.Add(new(start, i - start, TokenKind.Field));
                    else if (awaitingEventType)
                    {
                        spans.Add(new(start, i - start, TokenKind.Type));
                        awaitingEventType = false;
                        awaitingEventName = true;
                    }
                    else if (awaitingEventName)
                    {
                        spans.Add(new(start, i - start, TokenKind.Event));
                        awaitingEventName = false;
                    }
                    else if (namespaceContext) spans.Add(new(start, i - start, TokenKind.Namespace));
                    else if (NextNonWhitespace(line, i) == '(' && PreviousNonWhitespace(line, start) == '[')
                        spans.Add(new(start, i - start, TokenKind.Type));
                    else if (NextNonWhitespace(line, i) == '(') spans.Add(new(start, i - start, TokenKind.Method));
                    else if (word[0] == '_') spans.Add(new(start, i - start, TokenKind.Field));
                    else if (PreviousNonWhitespace(line, start) == '.') spans.Add(new(start, i - start, TokenKind.Property));
                    else if (NextNonWhitespace(line, i) is ',' or '=' or ')')
                        spans.Add(new(start, i - start, TokenKind.Parameter));
                    else if (char.IsUpper(word[0])) spans.Add(new(start, i - start, TokenKind.Type));
                }
                else
                {
                    if (line[i] == '[' && PreviousNonWhitespace(line, i) is null or ']') inAttributeList = true;
                    else if (inAttributeList && line[i] == '(') attributeArgumentDepth++;
                    else if (inAttributeList && line[i] == ')' && attributeArgumentDepth > 0) attributeArgumentDepth--;
                    else if (inAttributeList && line[i] == ']' && attributeArgumentDepth == 0) inAttributeList = false;
                    if (line[i] is ';' or '=') namespaceContext = false;
                    if (line[i] == '{')
                    {
                        if (awaitingEnumBody) { inEnum = true; enumDepth = 1; awaitingEnumBody = false; }
                        else if (inEnum) enumDepth++;
                    }
                    else if (line[i] == '}' && inEnum && --enumDepth == 0) inEnum = false;
                    i++;
                }
            }
            AddRazorMarkupSpans(line, spans);
            result.Add(new(lineIndex + 1, line, spans));
        }
        return result;
    }

    private static void AddRazorMarkupSpans(string line, List<ClassifiedSpan> spans)
    {
        var markup = new List<ClassifiedSpan>();
        foreach (Match directive in RazorBlockDirectiveRegex().Matches(line))
            markup.Add(new(directive.Groups["name"].Index, directive.Groups["name"].Length, TokenKind.Keyword));
        foreach (Match tag in HtmlTagRegex().Matches(line))
        {
            var name = tag.Groups["name"];
            markup.Add(new(name.Index, name.Length, TokenKind.HtmlTag));
            foreach (Match attribute in HtmlAttributeRegex().Matches(tag.Value))
            {
                var localStart = tag.Index + attribute.Groups["name"].Index;
                var length = attribute.Groups["name"].Length;
                if (line[localStart] == '@')
                {
                    markup.Add(new(localStart, 1, TokenKind.RazorTransition));
                    localStart++;
                    length--;
                }
                if (length > 0) markup.Add(new(localStart, length, TokenKind.HtmlAttribute));
            }
        }
        for (var start = line.IndexOf("@(", StringComparison.Ordinal); start >= 0;
             start = line.IndexOf("@(", start + 2, StringComparison.Ordinal))
        {
            markup.Add(new(start, 2, TokenKind.RazorTransition));
            var end = line.IndexOf(')', start + 2);
            if (end >= 0) markup.Add(new(end, 1, TokenKind.RazorTransition));
        }
        if (markup.Count > 0) spans.InsertRange(0, markup.OrderBy(span => span.Start));
    }

    [GeneratedRegex("</?(?<name>[A-Za-z][\\w-]*)(?:\\s[^<>]*)?/?>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("(?:^|\\s)(?<name>@?[A-Za-z_:][\\w:.-]*)(?=\\s*=)")]
    private static partial Regex HtmlAttributeRegex();

    [GeneratedRegex("@(?<name>code)\\b")]
    private static partial Regex RazorBlockDirectiveRegex();

    private static void AddRegexSpans(List<ClassifiedSpan> spans, string text, int start, int end)
    {
        Add(start, 1, TokenKind.String);
        var contentEnd = end > start + 1 && text[end - 1] == '"' ? end - 1 : end;
        for (var position = start + 1; position < contentEnd;)
        {
            var tokenStart = position;
            TokenKind kind;
            if (text[position] == '\\')
            {
                position += Math.Min(2, contentEnd - position);
                kind = TokenKind.RegexEscape;
            }
            else
            {
                kind = text[position] switch
                {
                    '(' or ')' or '<' or '>' => TokenKind.RegexGroup,
                    '[' or ']' => TokenKind.RegexCharacterClass,
                    '*' or '+' or '?' or '{' or '}' or '^' or '$' or '|' or '.' => TokenKind.RegexQuantifier,
                    _ => TokenKind.String
                };
                position++;
                while (position < contentEnd && kind == TokenKind.String && text[position] != '\\'
                    && text[position] is not ('(' or ')' or '<' or '>' or '[' or ']' or '*' or '+' or '?'
                        or '{' or '}' or '^' or '$' or '|' or '.')) position++;
            }
            Add(tokenStart, position - tokenStart, kind);
        }
        if (contentEnd < end) Add(contentEnd, 1, TokenKind.String);
        return;

        void Add(int tokenStart, int length, TokenKind kind)
        {
            if (length > 0) spans.Add(new(tokenStart, length, kind));
        }
    }

    private static char? NextNonWhitespace(string text, int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        return position < text.Length ? text[position] : null;
    }

    private static char? PreviousNonWhitespace(string text, int position)
    {
        while (position > 0 && char.IsWhiteSpace(text[position - 1])) position--;
        return position > 0 ? text[position - 1] : null;
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
                .Select(span => new ClassifiedSpan(span.Start - lineStart, span.Length, SemanticKind(span.Classification)))
                .Where(span => span.Kind != TokenKind.Text).DistinctBy(span => (span.Start, span.Length))
                .OrderBy(span => span.Start).ThenByDescending(span => span.Length).ToArray();
            semantic = semantic.Where(span => !line.Spans.Any(baseline => baseline.Kind == TokenKind.EnumType
                && span.Start < baseline.Start + baseline.Length && span.Start + span.Length > baseline.Start)).ToArray();
            semantic = NonOverlapping(semantic);
            if (semantic.Length > 0)
            {
                var baseline = line.Spans.SelectMany(span => Exclude(span, semantic));
                lines[index] = line with { Spans = baseline.Concat(semantic).OrderBy(span => span.Start).ToArray() };
            }
            lineStart = lineEnd + NewLineLength(text, lineEnd);
        }
        return lines;
    }

    private static IEnumerable<ClassifiedSpan> Exclude(ClassifiedSpan span, IReadOnlyList<ClassifiedSpan> exclusions)
    {
        var position = span.Start;
        var end = span.Start + span.Length;
        foreach (var exclusion in exclusions.Where(item => item.Start < end && item.Start + item.Length > position))
        {
            if (exclusion.Start > position)
                yield return new(position, exclusion.Start - position, span.Kind);
            position = Math.Max(position, exclusion.Start + exclusion.Length);
            if (position >= end) yield break;
        }
        if (position < end) yield return new(position, end - position, span.Kind);
    }

    private static ClassifiedSpan[] NonOverlapping(IEnumerable<ClassifiedSpan> spans)
    {
        var result = new List<ClassifiedSpan>();
        foreach (var span in spans)
            if (result.Count == 0 || span.Start >= result[^1].Start + result[^1].Length) result.Add(span);
        return result.ToArray();
    }

    private static int NewLineLength(string text, int position) => position >= text.Length ? 0
        : text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n' ? 2 : 1;

    private static TokenKind SemanticKind(string classification) => classification switch
    {
        var value when value.Contains("method", StringComparison.OrdinalIgnoreCase) => TokenKind.Method,
        var value when value.Contains("property", StringComparison.OrdinalIgnoreCase) => TokenKind.Property,
        var value when value.Contains("field", StringComparison.OrdinalIgnoreCase) => TokenKind.Field,
        var value when value.Contains("parameter", StringComparison.OrdinalIgnoreCase) => TokenKind.Parameter,
        var value when value.Contains("enumMember", StringComparison.OrdinalIgnoreCase)
            || value.Contains("enum", StringComparison.OrdinalIgnoreCase)
                && value.Contains("member", StringComparison.OrdinalIgnoreCase)
            => TokenKind.Field,
        var value when value.Contains("event", StringComparison.OrdinalIgnoreCase) => TokenKind.Event,
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
