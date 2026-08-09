namespace NovaSharp.LanguageServers;

internal static class LspConverters
{
    internal static Uri FileUri(string path) => new(Path.GetFullPath(path));

    internal static LspPosition ToPosition(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < offset && text[index + 1] == '\n') index++;
                line++;
                lineStart = index + 1;
            }
            else if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }
        return new(line, offset - lineStart);
    }

    internal static int ToOffset(string text, LspPosition position)
    {
        if (position.Line < 0 || position.Character < 0) throw new ArgumentOutOfRangeException(nameof(position));
        var line = 0;
        var index = 0;
        while (line < position.Line && index < text.Length)
        {
            if (text[index++] == '\n') line++;
        }
        if (line != position.Line) throw new ArgumentOutOfRangeException(nameof(position));
        var lineEnd = index;
        while (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n') lineEnd++;
        return Math.Min(index + position.Character, lineEnd);
    }

    internal static TextRange ToRange(string text, LspRange range)
    {
        var start = ToOffset(text, range.Start);
        var end = ToOffset(text, range.End);
        if (end < start) throw new InvalidDataException("LSP range ends before it starts.");
        return new(start, end - start);
    }
}
