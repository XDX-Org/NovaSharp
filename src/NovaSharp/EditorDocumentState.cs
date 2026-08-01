namespace NovaSharp;

internal sealed class EditorDocumentState
{
    internal string? FilePath { get; private set; }
    internal string? Content { get; set; }
    internal string? Error { get; private set; }

    internal async Task OpenAsync(
        string? path,
        Func<string, Task<string>>? readTextAsync = null)
    {
        if (path is null)
        {
            return;
        }

        readTextAsync ??= path => File.ReadAllTextAsync(path);

        try
        {
            var content = await readTextAsync(path);
            FilePath = path;
            Content = content;
            Error = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"Could not open {Path.GetFileName(path)}: {exception.Message}";
        }
    }
}
