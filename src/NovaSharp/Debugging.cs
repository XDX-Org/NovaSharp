using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace NovaSharp;

internal enum DebugSessionState { Idle, Starting, Configuring, Running, Paused, Terminated, Failed, Disconnected }
public enum DebugBreakpointState { Pending, Verified, Moved, Rejected }
public sealed record DebugBreakpoint(string SourcePath, int Line, string? Condition = null, string? HitCondition = null,
    string? LogMessage = null, DebugBreakpointState State = DebugBreakpointState.Pending, int? BoundLine = null, string? Message = null);
internal sealed record DebugLaunchConfiguration(string Program, string WorkingDirectory, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null, bool StopAtEntry = false,
    IReadOnlyList<DebugBreakpoint>? Breakpoints = null, IReadOnlyDictionary<string, string>? SourceMap = null,
    IReadOnlyList<DebugExceptionFilter>? ExceptionFilters = null);
internal sealed record DebugCapabilities(bool SupportsFunctionBreakpoints, bool SupportsConditionalBreakpoints,
    bool SupportsHitConditionalBreakpoints, bool SupportsLogPoints, bool SupportsExceptionOptions,
    bool SupportsRestartRequest, bool SupportsStepBack, bool SupportsExceptionBreakpoints,
    bool SupportsCompletionsRequest = false);
internal sealed record DebugThread(int Id, string Name);
internal sealed record DebugFunctionBreakpoint(string Name, string? Condition = null, string? HitCondition = null,
    DebugBreakpointState State = DebugBreakpointState.Pending, string? Message = null);
internal sealed record DebugExceptionFilter(string Filter, bool Enabled, string? Condition = null);
public sealed record DebugOutputEntry(string Text, string Category);
public sealed record BreakpointToggleRequest(string SourcePath, int Line);

internal sealed class DebugProtocolException(string message) : Exception(message);

internal sealed class DebugProtocolClient(Stream input, Stream output, int maxMessageBytes = 8 * 1024 * 1024,
    int maxPendingRequests = 128) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _sequence;
    private Task? _reader;
    internal event Action<string, JsonElement>? EventReceived;
    internal event Action<Exception>? Disconnected;

    internal void Start() => _reader ??= ReadLoopAsync(_shutdown.Token);

    internal async Task<JsonElement> RequestAsync(string command, object? arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_pending.Count >= maxPendingRequests) throw new DebugProtocolException("Too many pending debug requests.");
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(sequence, completion)) throw new DebugProtocolException("Duplicate debug request sequence.");
        try
        {
            Start();
            await WriteAsync(new { seq = sequence, type = "request", command, arguments }, cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeoutSource.CancelAfter(timeout);
            return await completion.Task.WaitAsync(timeoutSource.Token);
        }
        finally { _pending.TryRemove(sequence, out _); }
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > maxMessageBytes) throw new DebugProtocolException("Debug message exceeds the size limit.");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(payload, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var length = await ReadHeaderAsync(cancellationToken);
                if (length is null) break;
                if (length is < 0 || length > maxMessageBytes) throw new DebugProtocolException("Invalid debug message length.");
                var rented = ArrayPool<byte>.Shared.Rent(length.Value);
                try
                {
                    await input.ReadExactlyAsync(rented.AsMemory(0, length.Value), cancellationToken);
                    using var document = JsonDocument.Parse(rented.AsMemory(0, length.Value));
                    Dispatch(document.RootElement);
                }
                finally { ArrayPool<byte>.Shared.Return(rented); }
            }
            Disconnect(new DebugProtocolException("Debug adapter disconnected."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Disconnect(ex); }
    }

    private async Task<int?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (bytes.Count < 4096)
        {
            if (await input.ReadAsync(one, cancellationToken) == 0) return bytes.Count == 0 ? null : throw new EndOfStreamException();
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
            {
                var header = Encoding.ASCII.GetString([.. bytes]);
                var line = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .SingleOrDefault(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                return line is not null && int.TryParse(line["Content-Length:".Length..].Trim(), out var length)
                    ? length : throw new DebugProtocolException("Missing Content-Length header.");
            }
        }
        throw new DebugProtocolException("Debug header exceeds the size limit.");
    }

    private void Dispatch(JsonElement message)
    {
        var type = message.GetProperty("type").GetString();
        if (type == "event")
        {
            var eventPayload = message.TryGetProperty("body", out var eventBody) ? eventBody.Clone() : default;
            EventReceived?.Invoke(message.GetProperty("event").GetString()!, eventPayload);
            return;
        }
        if (type != "response" || !message.TryGetProperty("request_seq", out var requestSequence)
            || !_pending.TryGetValue(requestSequence.GetInt32(), out var completion)) return;
        if (message.TryGetProperty("success", out var success) && !success.GetBoolean())
        {
            var error = message.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : "Debug request failed.";
            completion.TrySetException(new DebugProtocolException(error ?? "Debug request failed."));
            return;
        }
        completion.TrySetResult(message.TryGetProperty("body", out var body) ? body.Clone() : default);
    }

    private void FailPending(Exception exception)
    {
        foreach (var request in _pending.Values) request.TrySetException(exception);
    }

    private void Disconnect(Exception exception)
    {
        FailPending(exception);
        Disconnected?.Invoke(exception);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_reader is not null) try { await _reader; } catch (OperationCanceledException) { }
        FailPending(new ObjectDisposedException(nameof(DebugProtocolClient)));
        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}

internal sealed class DebugSessionCoordinator
{
    private static readonly IReadOnlyDictionary<DebugSessionState, DebugSessionState[]> Allowed =
        new Dictionary<DebugSessionState, DebugSessionState[]>
        {
            [DebugSessionState.Idle] = [DebugSessionState.Starting],
            [DebugSessionState.Starting] = [DebugSessionState.Configuring, DebugSessionState.Failed, DebugSessionState.Disconnected],
            [DebugSessionState.Configuring] = [DebugSessionState.Running, DebugSessionState.Paused, DebugSessionState.Failed, DebugSessionState.Disconnected],
            [DebugSessionState.Running] = [DebugSessionState.Paused, DebugSessionState.Terminated, DebugSessionState.Failed, DebugSessionState.Disconnected],
            [DebugSessionState.Paused] = [DebugSessionState.Running, DebugSessionState.Terminated, DebugSessionState.Failed, DebugSessionState.Disconnected],
            [DebugSessionState.Terminated] = [DebugSessionState.Starting],
            [DebugSessionState.Failed] = [DebugSessionState.Starting],
            [DebugSessionState.Disconnected] = [DebugSessionState.Starting]
        };

    internal DebugSessionState State { get; private set; }
    internal long PauseEpoch { get; private set; }
    internal event Action<DebugSessionState>? StateChanged;

    internal void Transition(DebugSessionState next)
    {
        if (!Allowed[State].Contains(next)) throw new InvalidOperationException($"Cannot transition debug session from {State} to {next}.");
        State = next;
        if (next == DebugSessionState.Paused) PauseEpoch++;
        StateChanged?.Invoke(next);
    }

    internal bool IsCurrentPause(long epoch) => State == DebugSessionState.Paused && PauseEpoch == epoch;
}

internal static class DebugAdapterCatalog
{
    internal static string Resolve(string? baseDirectory = null, string? developmentAssetRoot = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var executable = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var packaged = Path.Combine(baseDirectory, "DebugAdapters", "netcoredbg", executable);
        if (File.Exists(packaged)) return packaged;
        developmentAssetRoot ??= typeof(DebugAdapterCatalog).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DebugAdapterDevelopmentAssetRoot")?.Value;
        if (developmentAssetRoot is not null)
        {
            var development = Path.Combine(developmentAssetRoot,
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "netcoredbg", executable);
            if (File.Exists(development)) return development;
        }
        throw new FileNotFoundException("The packaged managed debug adapter is unavailable.", packaged);
    }
}

public sealed class DebugAdapterSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly DebugProtocolClient _protocol;
    private readonly bool _ownsTarget;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DebugSourceMapper _sourceMapper;
    internal DebugSessionCoordinator Coordinator { get; } = new();
    internal DebugInspectionStore Inspection { get; } = new();
    internal IReadOnlyList<DebugBreakpoint> Breakpoints { get; private set; } = [];
    internal IReadOnlyList<DebugOutputEntry> Output { get; private set; } = [];
    internal event Action? OutputReceived;
    internal int? CurrentThreadId { get; private set; }
    internal string? StopReason { get; private set; }
    internal DebugCapabilities Capabilities { get; private set; } = new(false, false, false, false, false, false, false, false);

    private DebugAdapterSession(Process process, DebugProtocolClient protocol, bool ownsTarget = true,
        IReadOnlyDictionary<string, string>? sourceMap = null)
    {
        _process = process;
        _protocol = protocol;
        _ownsTarget = ownsTarget;
        _sourceMapper = new(sourceMap);
        protocol.EventReceived += OnEvent;
        protocol.Disconnected += OnDisconnected;
    }

    internal static async Task<DebugAdapterSession> LaunchAsync(DebugLaunchConfiguration configuration,
        string? adapterPath = null, CancellationToken cancellationToken = default)
    {
        var program = Path.GetFullPath(configuration.Program);
        var workingDirectory = Path.GetFullPath(configuration.WorkingDirectory);
        if (!File.Exists(program)) throw new FileNotFoundException("Debug target does not exist.", program);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Debug working directory does not exist.");
        adapterPath ??= DebugAdapterCatalog.Resolve();
        var start = new ProcessStartInfo(adapterPath) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--interpreter=vscode");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Debug adapter did not start.");
        var protocol = new DebugProtocolClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var session = new DebugAdapterSession(process, protocol, sourceMap: configuration.SourceMap);
        session.Coordinator.Transition(DebugSessionState.Starting);
        try
        {
            var capabilities = await protocol.RequestAsync("initialize", new { clientID = "novasharp", clientName = "NovaSharp",
                adapterID = "coreclr", pathFormat = "path", linesStartAt1 = true, columnsStartAt1 = true,
                supportsVariableType = true, supportsRunInTerminalRequest = false }, TimeSpan.FromSeconds(5), cancellationToken);
            session.Capabilities = ParseCapabilities(capabilities);
            session.Coordinator.Transition(DebugSessionState.Configuring);
            var targetEnvironment = BuildRunService.CreateInheritedEnvironment();
            foreach (var item in configuration.Environment ?? new Dictionary<string, string>()) targetEnvironment[item.Key] = item.Value;
            var launched = protocol.RequestAsync("launch", new { name = "NovaSharp", type = "coreclr", request = "launch",
                program, cwd = workingDirectory, args = configuration.Arguments,
                env = targetEnvironment, stopAtEntry = configuration.StopAtEntry, justMyCode = false }, TimeSpan.FromSeconds(10), cancellationToken);
            await session._initialized.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (configuration.Breakpoints is { Count: > 0 })
                foreach (var sourceBreakpoints in configuration.Breakpoints.GroupBy(item => Path.GetFullPath(item.SourcePath), DebugSourceMapper.PathComparer))
                    await session.SetBreakpointsAsync(sourceBreakpoints.ToArray(), cancellationToken);
            if (session.Capabilities.SupportsExceptionBreakpoints && configuration.ExceptionFilters is { } exceptionFilters)
                await session.SetExceptionBreakpointsAsync(exceptionFilters, cancellationToken);
            await protocol.RequestAsync("configurationDone", null, TimeSpan.FromSeconds(10), cancellationToken);
            await launched;
            if (session.Coordinator.State == DebugSessionState.Configuring) session.Coordinator.Transition(DebugSessionState.Running);
            return session;
        }
        catch
        {
            if (session.Coordinator.State != DebugSessionState.Disconnected)
                session.Coordinator.Transition(DebugSessionState.Failed);
            await session.DisposeAsync();
            throw;
        }
    }

    internal static async Task<DebugAdapterSession> AttachAsync(int processId, string? adapterPath = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        adapterPath ??= DebugAdapterCatalog.Resolve();
        var start = new ProcessStartInfo(adapterPath) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--interpreter=vscode");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Debug adapter did not start.");
        var protocol = new DebugProtocolClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var session = new DebugAdapterSession(process, protocol, ownsTarget: false);
        session.Coordinator.Transition(DebugSessionState.Starting);
        try
        {
            var capabilities = await protocol.RequestAsync("initialize", new { clientID = "novasharp", adapterID = "coreclr",
                pathFormat = "path", linesStartAt1 = true, columnsStartAt1 = true }, TimeSpan.FromSeconds(5), cancellationToken);
            session.Capabilities = ParseCapabilities(capabilities);
            session.Coordinator.Transition(DebugSessionState.Configuring);
            var attached = protocol.RequestAsync("attach", new { name = "NovaSharp attach", type = "coreclr",
                request = "attach", processId }, TimeSpan.FromSeconds(10), cancellationToken);
            await session._initialized.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await protocol.RequestAsync("configurationDone", null, TimeSpan.FromSeconds(5), cancellationToken);
            await attached;
            if (session.Coordinator.State == DebugSessionState.Configuring) session.Coordinator.Transition(DebugSessionState.Running);
            return session;
        }
        catch
        {
            if (session.Coordinator.State != DebugSessionState.Disconnected)
                session.Coordinator.Transition(DebugSessionState.Failed);
            await session.DisposeAsync();
            throw;
        }
    }

    internal Task ContinueAsync(int threadId, CancellationToken cancellationToken = default) => ControlAsync("continue", new { threadId }, cancellationToken);
    internal Task PauseAsync(int threadId, CancellationToken cancellationToken = default) => ControlAsync("pause", new { threadId }, cancellationToken);
    internal Task StepAsync(string command, int threadId, CancellationToken cancellationToken = default) =>
        command is "next" or "stepIn" or "stepOut" ? ControlAsync(command, new { threadId }, cancellationToken)
            : throw new ArgumentOutOfRangeException(nameof(command));

    internal async Task<IReadOnlyList<DebugThread>> LoadThreadsAsync(CancellationToken cancellationToken = default)
    {
        if (Coordinator.State != DebugSessionState.Paused) return [];
        var epoch = Coordinator.PauseEpoch;
        var body = await _protocol.RequestAsync("threads", null, TimeSpan.FromSeconds(5), cancellationToken);
        if (!Coordinator.IsCurrentPause(epoch)) return [];
        return body.TryGetProperty("threads", out var values) ? values.EnumerateArray().Take(1024).Select(value =>
            new DebugThread(value.GetProperty("id").GetInt32(), value.GetProperty("name").GetString() ?? "Thread")).ToArray() : [];
    }

    internal void SelectThread(int threadId)
    {
        if (Coordinator.State != DebugSessionState.Paused) throw new InvalidOperationException("A thread can only be selected while paused.");
        CurrentThreadId = threadId > 0 ? threadId : throw new ArgumentOutOfRangeException(nameof(threadId));
    }

    internal async Task<IReadOnlyList<DebugFunctionBreakpoint>> SetFunctionBreakpointsAsync(IReadOnlyList<DebugFunctionBreakpoint> breakpoints,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsFunctionBreakpoints) throw new NotSupportedException("The debug adapter does not support function breakpoints.");
        var body = await _protocol.RequestAsync("setFunctionBreakpoints", new { breakpoints = breakpoints.Select(item =>
            new { name = item.Name, condition = item.Condition, hitCondition = item.HitCondition }) }, TimeSpan.FromSeconds(5), cancellationToken);
        var bound = body.TryGetProperty("breakpoints", out var values) ? values.EnumerateArray().ToArray() : [];
        return breakpoints.Select((item, index) => index < bound.Length && bound[index].TryGetProperty("verified", out var verified) && verified.GetBoolean()
                ? breakpoints[index] with { State = DebugBreakpointState.Verified }
                : breakpoints[index] with { State = DebugBreakpointState.Rejected, Message = index < bound.Length && bound[index].TryGetProperty("message", out var message) ? message.GetString() : "Adapter omitted breakpoint binding." }).ToArray();
    }

    internal Task SetExceptionBreakpointsAsync(IReadOnlyList<DebugExceptionFilter> filters, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsExceptionBreakpoints)
            throw new NotSupportedException("Exception breakpoints are unavailable with this debug adapter.");
        var enabled = filters.Where(item => item.Enabled).ToArray();
        return _protocol.RequestAsync("setExceptionBreakpoints", new { filters = enabled.Select(item => item.Filter),
            filterOptions = enabled.Where(item => item.Condition is not null).Select(item => new { filterId = item.Filter, condition = item.Condition }) },
            TimeSpan.FromSeconds(5), cancellationToken);
    }

    internal async Task RunToCursorAsync(string sourcePath, int line, CancellationToken cancellationToken = default)
    {
        if (line < 1) throw new ArgumentOutOfRangeException(nameof(line));
        if (Coordinator.State != DebugSessionState.Paused || CurrentThreadId is not { } threadId)
            throw new InvalidOperationException("Run to cursor requires a paused debug session.");
        await SetBreakpointsAsync([.. Breakpoints.Where(item => PathsEqual(item.SourcePath, sourcePath)), new(sourcePath, line)], cancellationToken);
        await ContinueAsync(threadId, cancellationToken);
    }

    internal async Task SetBreakpointsAsync(IReadOnlyList<DebugBreakpoint> breakpoints, CancellationToken cancellationToken = default)
    {
        if (breakpoints.Count == 0) throw new ArgumentException("At least one breakpoint is required.", nameof(breakpoints));
        if (breakpoints.Any(item => item.Condition is not null) && !Capabilities.SupportsConditionalBreakpoints)
            throw new NotSupportedException("The debug adapter does not support conditional breakpoints.");
        if (breakpoints.Any(item => item.HitCondition is not null) && !Capabilities.SupportsHitConditionalBreakpoints)
            throw new NotSupportedException("The debug adapter does not support hit-count breakpoints.");
        if (breakpoints.Any(item => item.LogMessage is not null) && !Capabilities.SupportsLogPoints)
            throw new NotSupportedException("The debug adapter does not support log points.");
        var sourcePath = breakpoints.Select(item => Path.GetFullPath(item.SourcePath)).Distinct(DebugSourceMapper.PathComparer).Single();
        var adapterPath = _sourceMapper.ToAdapter(sourcePath);
        var checksum = File.Exists(sourcePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant() : null;
        var requested = breakpoints.Select(item =>
        {
            var value = new Dictionary<string, object?> { ["line"] = item.Line };
            if (item.Condition is not null) value["condition"] = item.Condition;
            if (item.HitCondition is not null) value["hitCondition"] = item.HitCondition;
            if (item.LogMessage is not null) value["logMessage"] = item.LogMessage;
            return value;
        }).ToArray();
        var response = await _protocol.RequestAsync("setBreakpoints", new { source = new { path = adapterPath,
                checksums = checksum is null ? null : new[] { new { algorithm = "SHA256", checksum } } },
            breakpoints = requested }, TimeSpan.FromSeconds(5), cancellationToken);
        var bound = response.TryGetProperty("breakpoints", out var items) ? items.EnumerateArray().ToArray() : [];
        var updated = breakpoints.Select((item, index) => index >= bound.Length ? item with { State = DebugBreakpointState.Rejected, Message = "Adapter omitted breakpoint binding." }
            : item with { State = bound[index].TryGetProperty("verified", out var verified) && verified.GetBoolean()
                    ? (bound[index].TryGetProperty("line", out var line) && line.GetInt32() != item.Line ? DebugBreakpointState.Moved : DebugBreakpointState.Verified)
                    : DebugBreakpointState.Rejected,
                BoundLine = bound[index].TryGetProperty("line", out var boundLine) ? boundLine.GetInt32() : null,
                Message = bound[index].TryGetProperty("message", out var message) ? message.GetString() : null }).ToArray();
        Breakpoints = [.. Breakpoints.Where(item => !PathsEqual(item.SourcePath, sourcePath)), .. updated];
    }

    internal async Task ClearBreakpointsAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        await _protocol.RequestAsync("setBreakpoints", new { source = new { path = _sourceMapper.ToAdapter(sourcePath) },
            breakpoints = Array.Empty<object>() }, TimeSpan.FromSeconds(5), cancellationToken);
        Breakpoints = Breakpoints.Where(item => !PathsEqual(item.SourcePath, sourcePath)).ToArray();
    }

    internal async Task<IReadOnlyList<DebugStackFrame>> LoadStackAsync(CancellationToken cancellationToken = default)
    {
        if (Coordinator.State != DebugSessionState.Paused || CurrentThreadId is not { } threadId) return [];
        var epoch = Coordinator.PauseEpoch;
        var body = await _protocol.RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 256 }, TimeSpan.FromSeconds(5), cancellationToken);
        if (!Coordinator.IsCurrentPause(epoch)) return [];
        var frames = body.TryGetProperty("stackFrames", out var values) ? values.EnumerateArray().Select(frame => new DebugStackFrame(
            frame.GetProperty("id").GetInt32(), frame.GetProperty("name").GetString() ?? "frame",
            frame.TryGetProperty("source", out var source) && source.TryGetProperty("path", out var path) ? _sourceMapper.ToClient(path.GetString()!) : null,
            frame.TryGetProperty("line", out var line) ? line.GetInt32() : 0,
            frame.TryGetProperty("column", out var column) ? column.GetInt32() : 0)).ToArray() : [];
        Inspection.SetFrames(epoch, frames);
        return Inspection.Frames;
    }

    internal async Task<IReadOnlyList<DebugVariable>> LoadVariablesAsync(int variablesReference, int start = 0, int count = 100,
        CancellationToken cancellationToken = default)
    {
        if (variablesReference <= 0) return [];
        if (Coordinator.State != DebugSessionState.Paused) return [];
        var epoch = Coordinator.PauseEpoch;
        var body = await _protocol.RequestAsync("variables", new { variablesReference, start, count }, TimeSpan.FromSeconds(5), cancellationToken);
        if (!Coordinator.IsCurrentPause(epoch)) return [];
        var variables = body.TryGetProperty("variables", out var values) ? values.EnumerateArray().Select(value => new DebugVariable(
            value.GetProperty("name").GetString() ?? "", value.GetProperty("value").GetString() ?? "",
            value.TryGetProperty("type", out var type) ? type.GetString() : null,
            value.TryGetProperty("variablesReference", out var reference) ? reference.GetInt32() : 0,
            value.TryGetProperty("namedVariables", out var named) ? named.GetInt32() : null,
            value.TryGetProperty("indexedVariables", out var indexed) ? indexed.GetInt32() : null)).ToArray() : [];
        Inspection.SetVariables(epoch, variablesReference, variables);
        return variables;
    }

    internal async Task<IReadOnlyList<DebugScope>> LoadScopesAsync(int frameId, CancellationToken cancellationToken = default)
    {
        if (Coordinator.State != DebugSessionState.Paused) return [];
        var epoch = Coordinator.PauseEpoch;
        var body = await _protocol.RequestAsync("scopes", new { frameId }, TimeSpan.FromSeconds(5), cancellationToken);
        if (!Coordinator.IsCurrentPause(epoch)) return [];
        return body.TryGetProperty("scopes", out var values) ? values.EnumerateArray().Take(64).Select(value => new DebugScope(
            value.GetProperty("name").GetString() ?? "Scope", value.GetProperty("variablesReference").GetInt32(),
            value.TryGetProperty("expensive", out var expensive) && expensive.GetBoolean(),
            value.TryGetProperty("namedVariables", out var named) ? named.GetInt32() : null,
            value.TryGetProperty("indexedVariables", out var indexed) ? indexed.GetInt32() : null)).ToArray() : [];
    }

    internal async Task<DebugEvaluation?> EvaluateAsync(string expression, int? frameId, CancellationToken cancellationToken = default,
        string context = "watch")
    {
        if (Coordinator.State != DebugSessionState.Paused || string.IsNullOrWhiteSpace(expression)) return null;
        if (context is not ("watch" or "repl" or "hover")) throw new ArgumentOutOfRangeException(nameof(context));
        var epoch = Coordinator.PauseEpoch;
        var arguments = new Dictionary<string, object?> { ["expression"] = expression, ["context"] = context };
        if (frameId is not null) arguments["frameId"] = frameId;
        var body = await _protocol.RequestAsync("evaluate", arguments, TimeSpan.FromSeconds(5), cancellationToken);
        if (!Coordinator.IsCurrentPause(epoch)) return null;
        return new(body.TryGetProperty("result", out var result) ? result.GetString() ?? "" : "",
            body.TryGetProperty("type", out var type) ? type.GetString() : null,
            body.TryGetProperty("variablesReference", out var reference) ? reference.GetInt32() : 0);
    }

    internal async Task<IReadOnlyList<string>> CompleteAsync(string text, int? frameId,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsCompletionsRequest || Coordinator.State != DebugSessionState.Paused) return [];
        var arguments = new Dictionary<string, object?> { ["text"] = text, ["column"] = text.Length + 1 };
        if (frameId is not null) arguments["frameId"] = frameId;
        var body = await _protocol.RequestAsync("completions", arguments, TimeSpan.FromSeconds(3), cancellationToken);
        if (!body.TryGetProperty("targets", out var targets)) return [];
        return targets.EnumerateArray().Take(100).Select(target =>
        {
            var insert = target.TryGetProperty("text", out var replacement) ? replacement.GetString()
                : target.TryGetProperty("label", out var label) ? label.GetString() : null;
            if (string.IsNullOrEmpty(insert)) return null;
            var start = target.TryGetProperty("start", out var startValue) ? startValue.GetInt32() : text.Length;
            var length = target.TryGetProperty("length", out var lengthValue) ? lengthValue.GetInt32() : 0;
            start = Math.Clamp(start, 0, text.Length);
            length = Math.Clamp(length, 0, text.Length - start);
            return text[..start] + insert + text[(start + length)..];
        }).Where(value => value is not null).Distinct(StringComparer.Ordinal).Cast<string>().ToArray();
    }

    private async Task ControlAsync(string command, object arguments, CancellationToken cancellationToken)
    {
        await _protocol.RequestAsync(command, arguments, TimeSpan.FromSeconds(5), cancellationToken);
        if (command is "continue" or "next" or "stepIn" or "stepOut" && Coordinator.State == DebugSessionState.Paused)
            Coordinator.Transition(DebugSessionState.Running);
    }

    private void OnEvent(string name, JsonElement body)
    {
        if (name == "initialized") _initialized.TrySetResult();
        else if (name == "output" && body.TryGetProperty("output", out var output))
        {
            var text = output.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                var category = body.TryGetProperty("category", out var value) ? value.GetString() ?? "console" : "console";
                Output = Output.Append(new(text, category)).TakeLast(2000).ToArray();
                OutputReceived?.Invoke();
            }
        }
        else if (name == "breakpoint" && body.TryGetProperty("breakpoint", out var changed))
        {
            var changedLine = changed.TryGetProperty("line", out var line) ? line.GetInt32() : (int?)null;
            var sourcePath = changed.TryGetProperty("source", out var source) && source.TryGetProperty("path", out var path) ? path.GetString() : null;
            Breakpoints = Breakpoints.Select(item => (sourcePath is not null ? PathsEqual(item.SourcePath, sourcePath) : Breakpoints.Count == 1)
                && (item.BoundLine == changedLine || item.Line == changedLine) ? item with
                {
                    State = changed.TryGetProperty("verified", out var verified) && verified.GetBoolean() ? DebugBreakpointState.Verified : DebugBreakpointState.Rejected,
                    BoundLine = changedLine,
                    Message = changed.TryGetProperty("message", out var message) ? message.GetString() : null
                } : item).ToArray();
        }
        else if (name == "stopped" && Coordinator.State is DebugSessionState.Running or DebugSessionState.Configuring)
        {
            CurrentThreadId = body.TryGetProperty("threadId", out var thread) ? thread.GetInt32() : null;
            StopReason = body.TryGetProperty("description", out var description) ? description.GetString()
                : body.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
            Coordinator.Transition(DebugSessionState.Paused);
            Inspection.BeginPause(Coordinator.PauseEpoch);
        }
        else if (name == "continued" && Coordinator.State == DebugSessionState.Paused)
        {
            CurrentThreadId = null;
            StopReason = null;
            Inspection.Resume();
            Coordinator.Transition(DebugSessionState.Running);
        }
        else if ((name is "terminated" or "exited")
            && (Coordinator.State is DebugSessionState.Configuring or DebugSessionState.Running or DebugSessionState.Paused))
        {
            if (name == "exited")
            {
                var exitCode = body.TryGetProperty("exitCode", out var code) ? code.GetInt32() : 0;
                Output = Output.Append(new($"Debug target exited with code {exitCode}.{Environment.NewLine}",
                    exitCode == 0 ? "console" : "stderr")).TakeLast(2000).ToArray();
                OutputReceived?.Invoke();
            }
            Coordinator.Transition(DebugSessionState.Terminated);
        }
    }

    private void OnDisconnected(Exception exception)
    {
        Output = Output.Append(new($"Debug adapter disconnected: {exception.Message}{Environment.NewLine}", "stderr"))
            .TakeLast(2000).ToArray();
        OutputReceived?.Invoke();
        if (Coordinator.State is DebugSessionState.Starting or DebugSessionState.Configuring
            or DebugSessionState.Running or DebugSessionState.Paused)
            Coordinator.Transition(DebugSessionState.Disconnected);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static DebugCapabilities ParseCapabilities(JsonElement body) => new(
        Flag(body, "supportsFunctionBreakpoints"), Flag(body, "supportsConditionalBreakpoints"),
        Flag(body, "supportsHitConditionalBreakpoints"), Flag(body, "supportsLogPoints"),
        Flag(body, "supportsExceptionOptions"), Flag(body, "supportsRestartRequest"), Flag(body, "supportsStepBack"),
        !OperatingSystem.IsMacOS() && body.TryGetProperty("exceptionBreakpointFilters", out var filters)
            && filters.ValueKind == JsonValueKind.Array && filters.GetArrayLength() > 0,
        Flag(body, "supportsCompletionsRequest"));
    private static bool Flag(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public async ValueTask DisposeAsync()
    {
        _protocol.EventReceived -= OnEvent;
        _protocol.Disconnected -= OnDisconnected;
        try { await _protocol.RequestAsync("disconnect", new { terminateDebuggee = _ownsTarget }, TimeSpan.FromSeconds(3)); } catch { }
        await _protocol.DisposeAsync();
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
    }
}

public sealed record DebugStackFrame(int Id, string Name, string? SourcePath, int Line, int Column);
internal sealed record DebugScope(string Name, int VariablesReference, bool Expensive, int? NamedVariables, int? IndexedVariables);
internal sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference, int? NamedVariables, int? IndexedVariables);
internal sealed record DebugEvaluation(string Result, string? Type, int VariablesReference);

internal sealed class DebugInspectionStore(int maxFrames = 256, int maxVariables = 10_000)
{
    private long _epoch;
    private IReadOnlyList<DebugStackFrame> _frames = [];
    private readonly Dictionary<int, IReadOnlyList<DebugVariable>> _variables = [];
    internal IReadOnlyList<DebugStackFrame> Frames => _frames;

    internal void BeginPause(long epoch)
    {
        _epoch = epoch;
        _frames = [];
        _variables.Clear();
    }

    internal bool SetFrames(long epoch, IEnumerable<DebugStackFrame> frames)
    {
        if (epoch != _epoch) return false;
        _frames = frames.Take(maxFrames).ToArray();
        return true;
    }

    internal bool SetVariables(long epoch, int reference, IEnumerable<DebugVariable> variables)
    {
        if (epoch != _epoch || reference <= 0) return false;
        var retained = variables.Take(maxVariables).ToArray();
        _variables[reference] = retained;
        return true;
    }

    internal IReadOnlyList<DebugVariable> Variables(int reference, int start = 0, int count = 100)
    {
        if (start < 0 || count is < 1 or > 1000) throw new ArgumentOutOfRangeException();
        return _variables.TryGetValue(reference, out var values) ? values.Skip(start).Take(count).ToArray() : [];
    }

    internal void Resume()
    {
        _epoch = -1;
        _frames = [];
        _variables.Clear();
    }
}

internal sealed class BreakpointStore
{
    private readonly Dictionary<string, List<DebugBreakpoint>> _bySource = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<DebugBreakpoint> All => _bySource.Values.SelectMany(values => values).ToArray();
    internal IReadOnlyList<DebugBreakpoint> ForSource(string sourcePath) => _bySource.TryGetValue(Path.GetFullPath(sourcePath), out var values) ? values : [];

    internal void Restore(IEnumerable<PersistedBreakpoint>? breakpoints)
    {
        _bySource.Clear();
        foreach (var group in breakpoints?.GroupBy(item => Path.GetFullPath(item.Path)) ?? [])
            Replace(group.Key, group.Select(item => new DebugBreakpoint(group.Key, item.Line,
                item.Condition, item.HitCondition, item.LogMessage)));
    }

    internal bool Toggle(string sourcePath, int line)
    {
        if (line < 1) throw new ArgumentOutOfRangeException(nameof(line));
        var normalized = Path.GetFullPath(sourcePath);
        if (!_bySource.TryGetValue(normalized, out var values)) _bySource[normalized] = values = [];
        var existing = values.FindIndex(item => item.Line == line);
        if (existing >= 0) { values.RemoveAt(existing); return false; }
        values.Add(new(normalized, line));
        values.Sort((left, right) => left.Line.CompareTo(right.Line));
        return true;
    }

    internal bool Remove(string sourcePath, int line)
    {
        var normalized = Path.GetFullPath(sourcePath);
        if (!_bySource.TryGetValue(normalized, out var values)) return false;
        var removed = values.RemoveAll(item => item.Line == line) > 0;
        if (values.Count == 0) _bySource.Remove(normalized);
        return removed;
    }

    internal void Replace(string sourcePath, IEnumerable<DebugBreakpoint> breakpoints)
    {
        var normalized = Path.GetFullPath(sourcePath);
        var values = breakpoints.Select(item => item with { SourcePath = normalized }).OrderBy(item => item.Line).ToList();
        if (values.Any(item => item.Line < 1)) throw new ArgumentOutOfRangeException(nameof(breakpoints));
        _bySource[normalized] = values;
    }

    internal void ApplyLineEdit(string sourcePath, int startLine, int removedLineCount, int insertedLineCount)
    {
        var normalized = Path.GetFullPath(sourcePath);
        if (!_bySource.TryGetValue(normalized, out var values)) return;
        var delta = insertedLineCount - removedLineCount;
        for (var index = 0; index < values.Count; index++)
        {
            var breakpoint = values[index];
            if (breakpoint.Line < startLine) continue;
            var nextLine = breakpoint.Line < startLine + removedLineCount ? startLine : Math.Max(1, breakpoint.Line + delta);
            values[index] = breakpoint with { Line = nextLine, State = DebugBreakpointState.Pending, BoundLine = null, Message = null };
        }
    }
}

internal sealed class DebugSourceMapper
{
    private readonly (string Client, string Adapter)[] _mappings;
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal DebugSourceMapper(IReadOnlyDictionary<string, string>? mappings)
    {
        _mappings = mappings?.Select(pair => (NormalizeRoot(pair.Key), NormalizeRoot(pair.Value)))
            .OrderByDescending(pair => pair.Item1.Length).ToArray() ?? [];
        if (_mappings.Select(pair => pair.Item1).Distinct(PathComparer).Count() != _mappings.Length)
            throw new ArgumentException("Source-map client roots must be unique.", nameof(mappings));
    }

    internal string ToAdapter(string path) => Map(path, clientToAdapter: true);
    internal string ToClient(string path) => Map(path, clientToAdapter: false);

    private string Map(string path, bool clientToAdapter)
    {
        var full = Path.GetFullPath(path);
        foreach (var mapping in _mappings)
        {
            var source = clientToAdapter ? mapping.Client : mapping.Adapter;
            var target = clientToAdapter ? mapping.Adapter : mapping.Client;
            if (PathComparer.Equals(full, source)) return target;
            if (full.StartsWith(source + Path.DirectorySeparatorChar, PathComparer == StringComparer.OrdinalIgnoreCase
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Path.Combine(target, Path.GetRelativePath(source, full));
        }
        return full;
    }

    private static string NormalizeRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Source-map roots must be absolute.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
