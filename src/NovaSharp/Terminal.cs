using Porta.Pty;
using System.Text;

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

public sealed record TerminalStyle(string? Foreground = null, string? Background = null,
    bool Bold = false, bool Underline = false, string? Link = null);
public sealed record TerminalRun(string Text, TerminalStyle Style);
public sealed record TerminalLine(IReadOnlyList<TerminalRun> Runs);

internal sealed class TerminalBuffer(int maxLines = 5_000, int maxBytes = 4 * 1024 * 1024)
{
    private readonly object _gate = new();
    private readonly List<List<TerminalRun>> _lines = [[]];
    private readonly StringBuilder _escape = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private TerminalStyle _style = new();
    private int _bytes;
    private int _cursorRow, _cursorColumn, _savedColumn;
    private bool _inEscape;

    internal IReadOnlyList<TerminalLine> Lines
    {
        get { lock (_gate) return _lines.Select(line => new TerminalLine(line.ToArray())).ToArray(); }
    }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        int count;
        lock (_gate)
        {
            count = _decoder.GetChars(bytes, chars, flush: false);
            foreach (var character in chars.AsSpan(0, count)) Consume(character);
            _bytes += bytes.Length;
            Trim();
        }
    }

    private void Consume(char value)
    {
        if (_inEscape)
        {
            _escape.Append(value);
            if (_escape.Length > 2_048) { _escape.Clear(); _inEscape = false; return; }
            if (value == '\a' || (_escape.Length > 1 && _escape[^2] == '\x1b' && value == '\\'))
                CompleteOsc();
            else if (_escape.Length > 1 && _escape[0] == '[' && value is >= '@' and <= '~') CompleteCsi();
            return;
        }
        if (value == '\x1b') { _inEscape = true; _escape.Clear(); return; }
        if (value == '\r') { _cursorColumn = 0; return; }
        if (value == '\n') { _cursorRow++; _cursorColumn = 0; EnsureRow(); return; }
        if (value == '\b') { Backspace(); return; }
        if (!char.IsControl(value)) AddText(value.ToString());
    }

    private void CompleteCsi()
    {
        var sequence = _escape.ToString();
        _escape.Clear(); _inEscape = false;
        var command = sequence[^1];
        var parameters = sequence[1..^1].TrimStart('?', '>', '!');
        var values = parameters.Split(';').Select(value => int.TryParse(value, out var parsed) ? parsed : 0).ToArray();
        if (values.Length == 0) values = [0];
        var amount = Math.Max(1, values[0]);
        switch (command)
        {
            case 'C': _cursorColumn += amount; return;
            case 'D': _cursorColumn = Math.Max(0, _cursorColumn - amount); return;
            case 'G': _cursorColumn = Math.Max(0, amount - 1); return;
            case 's': _savedColumn = _cursorColumn; return;
            case 'u': _cursorColumn = _savedColumn; return;
            case 'K': EraseLine(values[0]); return;
            case 'P': DeleteCharacters(amount); return;
            case 'm': break;
            default: return;
        }
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value == 0) _style = new(Link: _style.Link);
            else if (value == 1) _style = _style with { Bold = true };
            else if (value == 4) _style = _style with { Underline = true };
            else if (value == 22) _style = _style with { Bold = false };
            else if (value == 24) _style = _style with { Underline = false };
            else if (value is >= 30 and <= 37) _style = _style with { Foreground = Color(value - 30) };
            else if (value == 39) _style = _style with { Foreground = null };
            else if (value is >= 40 and <= 47) _style = _style with { Background = Color(value - 40) };
            else if (value == 49) _style = _style with { Background = null };
            else if (value is 38 or 48 && i + 2 < values.Length && values[i + 1] == 5)
            {
                var color = Color256(Math.Clamp(values[i + 2], 0, 255));
                _style = value == 38 ? _style with { Foreground = color } : _style with { Background = color };
                i += 2;
            }
        }
    }

    private void CompleteOsc()
    {
        var sequence = _escape.ToString().TrimEnd('\a');
        if (sequence.EndsWith("\x1b\\", StringComparison.Ordinal)) sequence = sequence[..^2];
        _escape.Clear(); _inEscape = false;
        if (!sequence.StartsWith("]8;", StringComparison.Ordinal)) return;
        var separator = sequence.IndexOf(';', 3);
        var link = separator < 0 ? null : sequence[(separator + 1)..];
        _style = _style with { Link = Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null };
    }

    private void AddText(string text)
    {
        EnsureRow();
        foreach (var character in text)
        {
            var cells = Cells(_lines[_cursorRow]);
            while (cells.Count < _cursorColumn) cells.Add((' ', new()));
            if (_cursorColumn < cells.Count) cells[_cursorColumn] = (character, _style);
            else cells.Add((character, _style));
            _lines[_cursorRow] = Runs(cells);
            _cursorColumn++;
        }
    }

    private void Backspace() => _cursorColumn = Math.Max(0, _cursorColumn - 1);

    private void EraseLine(int mode)
    {
        EnsureRow();
        var cells = Cells(_lines[_cursorRow]);
        if (mode == 2) cells.Clear();
        else if (mode == 1 && cells.Count > 0) cells.RemoveRange(0, Math.Min(cells.Count, _cursorColumn + 1));
        else if (_cursorColumn < cells.Count) cells.RemoveRange(_cursorColumn, cells.Count - _cursorColumn);
        _lines[_cursorRow] = Runs(cells);
    }

    private void DeleteCharacters(int count)
    {
        EnsureRow();
        var cells = Cells(_lines[_cursorRow]);
        if (_cursorColumn < cells.Count) cells.RemoveRange(_cursorColumn, Math.Min(count, cells.Count - _cursorColumn));
        _lines[_cursorRow] = Runs(cells);
    }

    private void EnsureRow()
    {
        while (_lines.Count <= _cursorRow) _lines.Add([]);
    }

    private static List<(char Character, TerminalStyle Style)> Cells(IEnumerable<TerminalRun> runs) =>
        runs.SelectMany(run => run.Text.Select(character => (character, run.Style))).ToList();

    private static List<TerminalRun> Runs(IEnumerable<(char Character, TerminalStyle Style)> cells)
    {
        var runs = new List<TerminalRun>();
        foreach (var cell in cells)
            if (runs.Count > 0 && runs[^1].Style == cell.Style)
                runs[^1] = runs[^1] with { Text = runs[^1].Text + cell.Character };
            else runs.Add(new(cell.Character.ToString(), cell.Style));
        return runs;
    }

    private void Trim()
    {
        while (_lines.Count > maxLines || (_bytes > maxBytes && _lines.Count > 1))
        {
            _bytes = Math.Max(0, _bytes - Encoding.UTF8.GetByteCount(string.Concat(_lines[0].Select(run => run.Text))) - 1);
            _lines.RemoveAt(0);
            _cursorRow = Math.Max(0, _cursorRow - 1);
        }
    }

    private static string Color(int index) => index switch
    {
        0 => "#2e3440", 1 => "#bf616a", 2 => "#a3be8c", 3 => "#ebcb8b",
        4 => "#81a1c1", 5 => "#b48ead", 6 => "#88c0d0", _ => "#eceff4"
    };

    private static string Color256(int index)
    {
        if (index < 8) return Color(index);
        if (index < 16) return index switch
        {
            8 => "#4c566a", 9 => "#ff6e79", 10 => "#b1d196", 11 => "#f4d88d",
            12 => "#8fbcdf", 13 => "#c895bf", 14 => "#93ccdc", _ => "#ffffff"
        };
        if (index >= 232)
        {
            var gray = 8 + (index - 232) * 10;
            return $"#{gray:x2}{gray:x2}{gray:x2}";
        }
        var value = index - 16;
        var levels = new[] { 0, 95, 135, 175, 215, 255 };
        return $"#{levels[value / 36]:x2}{levels[value / 6 % 6]:x2}{levels[value % 6]:x2}";
    }
}

internal sealed class TerminalQueryResponder
{
    private string _tail = "";

    internal IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> bytes)
    {
        var previousLength = _tail.Length;
        var text = _tail + Encoding.ASCII.GetString(bytes);
        var responses = new List<byte[]>();
        if (EndsInNewInput(text, "\x1b[c", previousLength) || EndsInNewInput(text, "\x1b[0c", previousLength))
            responses.Add(Encoding.ASCII.GetBytes("\x1b[?1;2c"));
        if (EndsInNewInput(text, "\x1b[>c", previousLength) || EndsInNewInput(text, "\x1b[>0c", previousLength))
            responses.Add(Encoding.ASCII.GetBytes("\x1b[>0;10;1c"));
        _tail = text.Length > 8 ? text[^8..] : text;
        return responses;
    }

    private static bool EndsInNewInput(string text, string query, int previousLength)
    {
        var index = text.IndexOf(query, StringComparison.Ordinal);
        return index >= 0 && index + query.Length > previousLength;
    }
}

public sealed class TerminalSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TerminalQueryResponder _queryResponder = new();
    private IPtyConnection? _connection;
    private Task? _reader;

    internal TerminalSession(string name, TerminalProfile profile, string workingDirectory, int maxLines = 5_000,
        int maxBytes = 4 * 1024 * 1024)
    {
        Id = Guid.NewGuid(); Name = name; Profile = profile; WorkingDirectory = Path.GetFullPath(workingDirectory);
        Buffer = new(maxLines, maxBytes);
    }

    internal Guid Id { get; }
    internal string Name { get; set; }
    internal TerminalProfile Profile { get; }
    internal string WorkingDirectory { get; }
    internal TerminalSessionState State { get; private set; } = TerminalSessionState.Starting;
    internal int? ExitCode { get; private set; }
    internal string? Error { get; private set; }
    internal TerminalBuffer Buffer { get; }
    internal event Action? Changed;

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
        try { await _connection.WriterStream.WriteAsync(bytes, cancellationToken); await _connection.WriterStream.FlushAsync(cancellationToken); }
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
        try { _connection?.Kill(); } catch (Exception exception) when (exception is InvalidOperationException or IOException) { }
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
                var output = bytes.AsSpan(0, count);
                Buffer.Append(output);
                foreach (var response in _queryResponder.Feed(output)) await SendAsync(response, cancellationToken);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (State != TerminalSessionState.Running) { }
    }

    private void ProcessExited(object? sender, PtyExitedEventArgs args)
    {
        ExitCode = args.ExitCode; State = TerminalSessionState.Exited; Changed?.Invoke();
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

    internal async Task<TerminalSession> CreateAsync(string? profileId, string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId) ?? Profiles[0];
        var directory = Directory.Exists(workingDirectory) ? workingDirectory! : Environment.CurrentDirectory;
        var session = new TerminalSession($"{profile.Name} {_sessions.Count + 1}", profile, directory);
        session.Changed += SessionChanged; _sessions.Add(session); ActiveSession = session; Changed?.Invoke();
        await session.StartAsync(cancellationToken: cancellationToken);
        return session;
    }

    internal void Activate(Guid id) { ActiveSession = _sessions.FirstOrDefault(item => item.Id == id) ?? ActiveSession; Changed?.Invoke(); }
    internal void Rename(Guid id, string name)
    {
        if (_sessions.FirstOrDefault(item => item.Id == id) is { } session && !string.IsNullOrWhiteSpace(name))
        { session.Name = name.Trim(); Changed?.Invoke(); }
    }

    internal async Task RestartAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var old = _sessions.FirstOrDefault(item => item.Id == id);
        if (old is null) return;
        var index = _sessions.IndexOf(old); var replacement = new TerminalSession(old.Name, old.Profile, old.WorkingDirectory);
        replacement.Changed += SessionChanged; _sessions[index] = replacement; ActiveSession = replacement;
        old.Changed -= SessionChanged; await old.DisposeAsync(); Changed?.Invoke(); await replacement.StartAsync(cancellationToken: cancellationToken);
    }

    internal async Task CloseAsync(Guid id, bool force = false)
    {
        var session = _sessions.FirstOrDefault(item => item.Id == id);
        if (session is null || (!force && session.State == TerminalSessionState.Running)) return;
        session.Changed -= SessionChanged; _sessions.Remove(session); await session.DisposeAsync();
        if (ReferenceEquals(ActiveSession, session)) ActiveSession = _sessions.LastOrDefault();
        Changed?.Invoke();
    }

    private void SessionChanged() => Changed?.Invoke();
    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.ToArray()) { session.Changed -= SessionChanged; await session.DisposeAsync(); }
        _sessions.Clear(); ActiveSession = null;
    }
}
