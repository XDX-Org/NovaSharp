using NovaSharp.Diagnostics;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DiagnosticStoreTests
{
    [Fact]
    public void Replacement_IsScopedByProducerAndContext()
    {
        var store = new DiagnosticStore();
        store.Replace("loader", "project-a", 1, [Diagnostic("loader", "project-a", 1, "a")]);
        store.Replace("loader", "project-b", 1, [Diagnostic("loader", "project-b", 1, "b")]);
        store.Replace("loader", "project-a", 2, [Diagnostic("loader", "project-a", 2, "new")]);

        Assert.Equal(["b", "new"], store.Snapshot.Diagnostics.Select(static item => item.Identity).Order().ToArray());
    }

    [Fact]
    public void Capacity_DropsOldestSourceVersion()
    {
        var store = new DiagnosticStore(capacity: 2);
        store.Replace("loader", "one", 1, [Diagnostic("loader", "one", 1, "one")]);
        store.Replace("loader", "two", 2, [Diagnostic("loader", "two", 2, "two")]);
        store.Replace("loader", "three", 3, [Diagnostic("loader", "three", 3, "three")]);

        Assert.Equal(1, store.Snapshot.DroppedCount);
        Assert.DoesNotContain(store.Snapshot.Diagnostics, static item => item.Identity == "one");
    }

    private static WorkbenchDiagnostic Diagnostic(string producer, string context, long version, string identity)
    {
        return new(producer, context, version, identity, DiagnosticSeverity.Warning, identity);
    }
}
