namespace NovaSharp.LanguageServers;

internal sealed class LspDiagnosticPublisher
{
    private readonly string _serverInstance;
    private readonly LanguageDiagnosticStore _store;
    private readonly Func<string, EditorSnapshot?> _snapshot;

    internal LspDiagnosticPublisher(string serverInstance, LanguageDiagnosticStore store,
        Func<string, EditorSnapshot?> snapshot)
    {
        _serverInstance = serverInstance;
        _store = store;
        _snapshot = snapshot;
    }

    internal bool Publish(LspPublishDiagnosticsParams published, long? expectedVersion = null)
    {
        if (!Uri.TryCreate(published.Uri, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        var path = Path.GetFullPath(uri.LocalPath);
        if (_snapshot(path) is not { } snapshot || expectedVersion is { } version && snapshot.Version != version)
            return false;
        var diagnostics = new List<LanguageDiagnostic>();
        foreach (var item in published.Diagnostics)
        {
            TextRange range;
            try { range = LspConverters.ToRange(snapshot.Text, item.Range); }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidDataException) { continue; }
            var severity = item.Severity switch
            {
                1 => LanguageDiagnosticSeverity.Error,
                2 => LanguageDiagnosticSeverity.Warning,
                3 => LanguageDiagnosticSeverity.Information,
                _ => LanguageDiagnosticSeverity.Hidden
            };
            var code = item.Code is { } value ? value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString() ?? string.Empty : value.GetRawText() : string.Empty;
            var related = item.RelatedInformation?.Select(information =>
                $"{information.Message} ({information.Location.Uri}:{information.Location.Range.Start.Line + 1})").ToArray();
            diagnostics.Add(new(code, LanguageDiagnosticSource.LanguageServer, severity, item.Message, path, range,
                item.Range.Start.Line, item.Range.Start.Character, null, item.Range.End.Line, item.Range.End.Character,
                item.Tags, related, item.CodeDescription?.Href, _serverInstance));
        }
        return _store.Replace(path, snapshot.Version, LanguageDiagnosticSource.LanguageServer,
            diagnostics, _serverInstance);
    }

    internal void Clear(string path) => _store.Replace(path, long.MaxValue,
        LanguageDiagnosticSource.LanguageServer, [], _serverInstance);
}
