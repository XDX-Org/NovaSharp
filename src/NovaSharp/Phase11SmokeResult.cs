namespace NovaSharp;

internal sealed record Phase11SmokeResult(bool TerminalPresent, bool InputRoundTrip,
    bool ResizeValid, bool ProcessExited, string? Error = null);
