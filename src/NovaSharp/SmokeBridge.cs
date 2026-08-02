using Microsoft.JSInterop;

namespace NovaSharp;

public static class SmokeBridge
{
    [JSInvokable("CompletePhase4SmokeReport")]
    public static Task CompletePhase4SmokeAsync(Phase4SmokeResult result) => Program.CompletePhase4SmokeAsync(result);

    [JSInvokable("CompletePhase5SmokeReport")]
    public static Task CompletePhase5SmokeAsync(Phase5SmokeResult result) => Program.CompletePhase5SmokeAsync(result);
}
