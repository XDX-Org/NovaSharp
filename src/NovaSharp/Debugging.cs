using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace NovaSharp;

internal enum DebugSessionState { Idle, Starting, Configuring, Running, Paused, Terminated, Failed, Disconnected }
internal enum DebugBreakpointState { Pending, Verified, Moved, Rejected }
internal sealed record DebugBreakpoint(string SourcePath, int Line, string? Condition = null, string? HitCondition = null,
    string? LogMessage = null, DebugBreakpointState State = DebugBreakpointState.Pending, int? BoundLine = null, string? Message = null);
internal sealed record DebugLaunchConfiguration(string Program, string WorkingDirectory, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null, bool StopAtEntry = false);

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
            FailPending(new DebugProtocolException("Debug adapter disconnected."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { FailPending(ex); }
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

internal sealed record DebugStackFrame(int Id, string Name, string? SourcePath, int Line, int Column);
internal sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference, int? NamedVariables, int? IndexedVariables);

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
    internal IReadOnlyList<DebugBreakpoint> ForSource(string sourcePath) => _bySource.TryGetValue(Path.GetFullPath(sourcePath), out var values) ? values : [];

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
