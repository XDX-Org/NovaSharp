using Porta.Pty;

namespace NovaSharp;

public enum TerminalSessionState { Starting, Running, Exited, Failed }

public sealed record TerminalProfile(string Id, string Name, string Executable, IReadOnlyList<string> Arguments)
{
    public static IReadOnlyList<TerminalProfile> Defaults()
    {
        if (OperatingSystem.IsWindows())
        {
            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            return [new("powershell", "PowerShell", powerShell, ["-NoLogo"]),
                new("cmd", "Command Prompt", Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", [])];
        }
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell)) shell = "/bin/sh";
        return [new("default", Path.GetFileName(shell), shell, [])];
    }
}

internal sealed record TerminalOutputChunk(long Sequence, byte[] Data);

internal sealed class TerminalTranscript(int maxBytes = 4 * 1024 * 1024)
{
    private readonly object _gate = new();
    private readonly Queue<TerminalOutputChunk> _chunks = new();
    private long _sequence;
    private int _bytes;

    internal IReadOnlyList<TerminalOutputChunk> Chunks { get { lock (_gate) return _chunks.ToArray(); } }

    internal TerminalOutputChunk Append(ReadOnlySpan<byte> data)
    {
        var chunk = new TerminalOutputChunk(Interlocked.Increment(ref _sequence), data.ToArray());
        lock (_gate)
        {
            _chunks.Enqueue(chunk); _bytes += chunk.Data.Length;
            while (_bytes > maxBytes && _chunks.Count > 1) _bytes -= _chunks.Dequeue().Data.Length;
        }
        return chunk;
    }
}

public sealed class TerminalSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private IPtyConnection? _connection;
    private Task? _reader;

    internal TerminalSession(string name, TerminalProfile profile, string workingDirectory,
        int maxBytes = 4 * 1024 * 1024)
    {
        Id = Guid.NewGuid(); Name = name; Profile = profile; WorkingDirectory = Path.GetFullPath(workingDirectory);
        Transcript = new(maxBytes);
    }

    internal Guid Id { get; }
    internal string Name { get; set; }
    internal TerminalProfile Profile { get; }
    internal string WorkingDirectory { get; }
    internal TerminalSessionState State { get; private set; } = TerminalSessionState.Starting;
    internal int? ExitCode { get; private set; }
    internal string? Error { get; private set; }
    internal TerminalTranscript Transcript { get; }
    internal event Action? Changed;
    internal event Action<TerminalSession, TerminalOutputChunk>? OutputReceived;

    internal async Task StartAsync(int columns = 80, int rows = 24, CancellationToken cancellationToken = default)
    {
        try
        {
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString() ?? "");
            environment["TERM"] = "xterm-256color";
            _connection = await PtyProvider.SpawnAsync(new PtyOptions
            {
                Name = Name, App = Profile.Executable, CommandLine = Profile.Arguments.ToArray(),
                Cwd = WorkingDirectory, Cols = Math.Max(2, columns), Rows = Math.Max(1, rows), Environment = environment
            }, cancellationToken);
            _connection.ProcessExited += ProcessExited;
            State = TerminalSessionState.Running;
            Changed?.Invoke();
            _reader = ReadAsync(_lifetime.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            State = TerminalSessionState.Failed; Error = exception.Message; Changed?.Invoke();
        }
    }

    internal async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (_connection is null || State != TerminalSessionState.Running) return;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _connection.WriterStream.WriteAsync(bytes, cancellationToken);
            await _connection.WriterStream.FlushAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    internal void Resize(int columns, int rows)
    {
        if (_connection is not null && State == TerminalSessionState.Running)
            _connection.Resize(Math.Max(2, columns), Math.Max(1, rows));
    }

    internal void Stop()
    {
        _lifetime.Cancel();
        try { _connection?.Kill(); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException) { }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = new byte[16 * 1024];
        try
        {
            while (_connection is not null)
            {
                var count = await _connection.ReaderStream.ReadAsync(bytes, cancellationToken);
                if (count == 0) break;
                var chunk = Transcript.Append(bytes.AsSpan(0, count));
                OutputReceived?.Invoke(this, chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (State != TerminalSessionState.Running) { }
    }

    private void ProcessExited(object? sender, PtyExitedEventArgs args)
    {
        ExitCode = args.ExitCode;
        _ = CompleteExitAsync();
    }

    private async Task CompleteExitAsync()
    {
        await Task.Yield();
        if (_reader is not null) try { await _reader; } catch (IOException) { }
        State = TerminalSessionState.Exited; Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_reader is not null) try { await _reader; } catch (IOException) { }
        if (_connection is not null) { _connection.ProcessExited -= ProcessExited; _connection.Dispose(); }
        _lifetime.Dispose(); _writeGate.Dispose();
    }
}

public sealed class TerminalService : IAsyncDisposable
{
    private readonly List<TerminalSession> _sessions = [];
    internal IReadOnlyList<TerminalProfile> Profiles { get; } = TerminalProfile.Defaults();
    internal IReadOnlyList<TerminalSession> Sessions => _sessions.ToArray();
    internal TerminalSession? ActiveSession { get; private set; }
    internal bool HasRunningSessions => _sessions.Any(session => session.State == TerminalSessionState.Running);
    internal event Action? Changed;
    internal event Action<TerminalSession, TerminalOutputChunk>? OutputReceived;

    internal async Task<TerminalSession> CreateAsync(string? profileId, string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId) ?? Profiles[0];
        var directory = Directory.Exists(workingDirectory) ? workingDirectory! : Environment.CurrentDirectory;
        var session = new TerminalSession($"{profile.Name} {_sessions.Count + 1}", profile, directory);
        session.Changed += SessionChanged; session.OutputReceived += SessionOutputReceived;
        _sessions.Add(session); ActiveSession = session; Changed?.Invoke();
        await session.StartAsync(cancellationToken: cancellationToken);
        return session;
    }

    internal void Activate(Guid id)
    {
        ActiveSession = _sessions.FirstOrDefault(item => item.Id == id) ?? ActiveSession;
        Changed?.Invoke();
    }

    internal void Rename(Guid id, string name)
    {
        if (_sessions.FirstOrDefault(item => item.Id == id) is { } session && !string.IsNullOrWhiteSpace(name))
        { session.Name = name.Trim(); Changed?.Invoke(); }
    }

    internal async Task RestartAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var old = _sessions.FirstOrDefault(item => item.Id == id);
        if (old is null) return;
        var index = _sessions.IndexOf(old);
        var replacement = new TerminalSession(old.Name, old.Profile, old.WorkingDirectory);
        replacement.Changed += SessionChanged; replacement.OutputReceived += SessionOutputReceived;
        _sessions[index] = replacement; ActiveSession = replacement;
        old.Changed -= SessionChanged; old.OutputReceived -= SessionOutputReceived;
        await old.DisposeAsync(); Changed?.Invoke(); await replacement.StartAsync(cancellationToken: cancellationToken);
    }

    internal async Task CloseAsync(Guid id, bool force = false)
    {
        var session = _sessions.FirstOrDefault(item => item.Id == id);
        if (session is null || (!force && session.State == TerminalSessionState.Running)) return;
        session.Changed -= SessionChanged; session.OutputReceived -= SessionOutputReceived;
        _sessions.Remove(session); await session.DisposeAsync();
        if (ReferenceEquals(ActiveSession, session)) ActiveSession = _sessions.LastOrDefault();
        Changed?.Invoke();
    }

    private void SessionChanged() => Changed?.Invoke();
    private void SessionOutputReceived(TerminalSession session, TerminalOutputChunk chunk) =>
        OutputReceived?.Invoke(session, chunk);

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.ToArray())
        {
            session.Changed -= SessionChanged; session.OutputReceived -= SessionOutputReceived;
            await session.DisposeAsync();
        }
        _sessions.Clear(); ActiveSession = null;
    }
}
