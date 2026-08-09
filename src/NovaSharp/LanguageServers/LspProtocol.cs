using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaSharp.LanguageServers;

internal sealed record LspPosition(int Line, int Character);
internal sealed record LspRange(LspPosition Start, LspPosition End);
internal sealed record LspTextDocumentIdentifier(string Uri);
internal sealed record LspVersionedTextDocumentIdentifier(string Uri, long Version);
internal sealed record LspTextDocumentItem(string Uri, string LanguageId, long Version, string Text);
internal sealed record LspTextDocumentContentChangeEvent(LspRange? Range, int? RangeLength, string Text);
internal sealed record LspDidOpenTextDocumentParams(LspTextDocumentItem TextDocument);
internal sealed record LspDidChangeTextDocumentParams(LspVersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<LspTextDocumentContentChangeEvent> ContentChanges);
internal sealed record LspDidSaveTextDocumentParams(LspTextDocumentIdentifier TextDocument, string? Text = null);
internal sealed record LspDidCloseTextDocumentParams(LspTextDocumentIdentifier TextDocument);
internal sealed record LspTextDocumentPositionParams(LspTextDocumentIdentifier TextDocument, LspPosition Position);
internal sealed record LspWorkspaceFolder(string Uri, string Name);
internal sealed record LspClientInfo(string Name, string? Version = null);
internal sealed record LspInitializeParams(int? ProcessId, string? RootUri, object Capabilities,
    LspClientInfo ClientInfo, IReadOnlyList<LspWorkspaceFolder>? WorkspaceFolders = null,
    object? InitializationOptions = null, string Trace = "off");
internal sealed record LspServerInfo(string Name, string? Version = null);
internal sealed record LspInitializeResult(JsonElement Capabilities, LspServerInfo? ServerInfo = null);
internal sealed record LspRegistration(string Id, string Method, JsonElement? RegisterOptions = null);
internal sealed record LspRegistrationParams(IReadOnlyList<LspRegistration> Registrations);
internal sealed record LspUnregistration(string Id, string Method);
internal sealed record LspUnregistrationParams(
    [property: JsonPropertyName("unregisterations")] IReadOnlyList<LspUnregistration> Unregistrations);
internal sealed record LspLogMessageParams(int Type, string Message);
internal sealed record LspPublishDiagnosticsParams(string Uri, IReadOnlyList<LspDiagnostic> Diagnostics,
    long? Version = null);
internal sealed record LspDiagnostic(LspRange Range, int? Severity, JsonElement? Code, string? Source,
    string Message, IReadOnlyList<int>? Tags = null, IReadOnlyList<LspDiagnosticRelatedInformation>? RelatedInformation = null,
    LspCodeDescription? CodeDescription = null, JsonElement? Data = null);
internal sealed record LspLocation(string Uri, LspRange Range);
internal sealed record LspDiagnosticRelatedInformation(LspLocation Location, string Message);
internal sealed record LspCodeDescription(string Href);

public enum LanguageServerState { Stopped, Starting, LoadingWorkspace, Ready, Restarting, Unavailable }
public sealed record LanguageServerStatus(LanguageServerState State, string? Name = null, string? Version = null,
    string? Detail = null);
