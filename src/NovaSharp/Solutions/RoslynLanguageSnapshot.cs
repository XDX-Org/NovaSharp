using Microsoft.CodeAnalysis;

namespace NovaSharp.Solutions;

internal sealed record RoslynLanguageSnapshot(
    Solution Solution,
    DocumentId DocumentId,
    ProjectId ProjectId,
    string ProjectName,
    string? TargetFramework,
    string DocumentUri,
    long SourceVersion,
    long Sequence,
    long ReplicaVersion);
