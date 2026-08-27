using Microsoft.CodeAnalysis;

namespace NovaSharp.Solutions;

public enum SolutionLoadState
{
    Closed,
    Loading,
    Ready,
    Failed,
}

public enum SolutionReferenceKind
{
    Project,
    Assembly,
    Analyzer,
}

public sealed record SolutionReferenceSnapshot(SolutionReferenceKind Kind, string Name, string? Path = null);

public sealed record SolutionDocumentSnapshot(string Id, string Name, string Path, string DocumentUri, bool IsGenerated = false);

public sealed record ProjectContextSnapshot(
    string Id,
    string Name,
    string Path,
    string? TargetFramework,
    bool IsActive,
    IReadOnlyList<SolutionDocumentSnapshot> Documents,
    IReadOnlyList<SolutionReferenceSnapshot> References,
    IReadOnlyList<string> Defines,
    string LanguageVersion,
    string Nullable,
    bool DocumentsTruncated = false);

public sealed record ProjectLoadStatusSnapshot(
    string ProjectPath,
    string Operation,
    string? TargetFramework,
    TimeSpan Elapsed);

public sealed record ProjectContextChange(
    string ProjectPath,
    string? TargetFramework,
    string Kind);

public sealed record SolutionWorkspaceMetrics(
    int MutationQueueCapacity = 128,
    int ReplicaCapacity = 1_024,
    int PendingMutations = 0,
    int RetainedReplicas = 0,
    int DroppedReplicaSignals = 0,
    int DroppedReplicaSources = 0,
    int CanceledLoads = 0,
    int RetainedRoslynSnapshots = 0,
    TimeSpan LastLoadDuration = default);

public sealed record SolutionWorkspaceSnapshot(
    SolutionLoadState State = SolutionLoadState.Closed,
    string? Path = null,
    string? Name = null,
    IReadOnlyList<ProjectContextSnapshot>? Projects = null,
    IReadOnlyList<ProjectLoadStatusSnapshot>? Progress = null,
    IReadOnlyList<ProjectContextChange>? ContextChanges = null,
    IReadOnlyList<string>? LoadDiagnostics = null,
    string? Error = null,
    long SourceVersion = 0,
    long Version = 0,
    SolutionWorkspaceMetrics? Metrics = null)
{
    public IReadOnlyList<ProjectContextSnapshot> Projects { get; init; } = Projects ?? [];
    public IReadOnlyList<ProjectLoadStatusSnapshot> Progress { get; init; } = Progress ?? [];
    public IReadOnlyList<ProjectContextChange> ContextChanges { get; init; } = ContextChanges ?? [];
    public IReadOnlyList<string> LoadDiagnostics { get; init; } = LoadDiagnostics ?? [];
    public SolutionWorkspaceMetrics Metrics { get; init; } = Metrics ?? new SolutionWorkspaceMetrics();
}

public sealed record RoslynDocumentContext(
    ProjectId ProjectId,
    DocumentId DocumentId,
    string ProjectName,
    string? TargetFramework,
    bool IsActive);
