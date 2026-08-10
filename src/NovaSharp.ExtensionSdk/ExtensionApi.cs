namespace NovaSharp.Extensions;

public interface INovaSharpExtension
{
    ValueTask ActivateAsync(IExtensionContext context, CancellationToken cancellationToken);
    ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public interface IExtensionContext
{
    ICommandRegistry Commands { get; }
    IExtensionSettings Settings { get; }
    IDiagnosticPublisher Diagnostics { get; }
    IReadOnlyWorkspace Workspace { get; }
}

public interface ICommandRegistry
{
    IDisposable Register(string commandId, Func<CancellationToken, ValueTask> handler);
}

public interface IExtensionSettings
{
    T? Get<T>(string settingId);
}

public interface IDiagnosticPublisher
{
    void Publish(string source, IReadOnlyList<ExtensionDiagnostic> diagnostics);
    void Clear(string source);
}

public interface IReadOnlyWorkspace
{
    string? Name { get; }
    IReadOnlyList<WorkspaceProject> Projects { get; }
}

public sealed record WorkspaceProject(string Name, string Language, IReadOnlyList<string> TargetFrameworks);
public sealed record ExtensionDiagnostic(string Code, string Message, DiagnosticSeverity Severity,
    string? DocumentPath = null, int? StartLine = null, int? StartColumn = null,
    int? EndLine = null, int? EndColumn = null);
public enum DiagnosticSeverity { Information, Warning, Error }
