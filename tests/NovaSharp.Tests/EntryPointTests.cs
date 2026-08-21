using System.Reflection;
using NovaSharp.Platform;
using Xunit;

namespace NovaSharp.Tests;

/// <summary>
/// Guards the apartment state the window host depends on.
/// </summary>
/// <remarks>
/// Making <c>Main</c> asynchronous is a silent break: the code compiles, the attribute stays on a method that is no
/// longer the entry point, and the failure only appears as <c>RPC_E_CHANGED_MODE</c> when a window is opened on a
/// machine that has one. Asserting on the built entry point catches it at test time instead.
/// </remarks>
public sealed class EntryPointTests
{
    private static MethodInfo EntryPoint =>
        typeof(WorkspacePaths).Assembly.EntryPoint
        ?? throw new InvalidOperationException("The application assembly has no entry point.");

    [Fact]
    public void EntryPoint_RunsInASingleThreadedApartment()
    {
        Assert.NotNull(EntryPoint.GetCustomAttribute<STAThreadAttribute>());
    }

    [Fact]
    public void EntryPoint_IsSynchronous()
    {
        // An async entry point is exactly what strips the attribute above, so the return type is the root cause worth
        // naming rather than only its symptom.
        Assert.Equal(typeof(void), EntryPoint.ReturnType);
        Assert.Equal("Main", EntryPoint.Name);
    }
}
