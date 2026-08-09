using System.Diagnostics;
using System.Text;

namespace NovaSharp.LanguageServers;

internal sealed record LanguageServerLaunchOptions(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null, int StderrCapacity = 64 * 1024);

internal sealed class LanguageServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly int _stderrCapacity;
    private readonly Task _stderrPump;

    private LanguageServerProcess(Process process, int stderrCapacity)
    {
        _process = process;
        _stderrCapacity = stderrCapacity;
        _stderrPump = PumpStderrAsync(process.StandardError);
    }

    internal Stream Input => _process.StandardOutput.BaseStream;
    internal Stream Output => _process.StandardInput.BaseStream;
    internal int Id => _process.Id;
    internal bool HasExited => _process.HasExited;
    internal int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    internal Task Exited => _process.WaitForExitAsync();
    internal string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

    internal static LanguageServerProcess Start(LanguageServerLaunchOptions options)
    {
        if (!Path.IsPathFullyQualified(options.Executable))
            throw new ArgumentException("Language-server executable paths must be absolute.", nameof(options));
        var start = new ProcessStartInfo(options.Executable)
        {
            WorkingDirectory = Path.GetFullPath(options.WorkingDirectory),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        foreach (var item in SafeEnvironment()) start.Environment[item.Key] = item.Value;
        foreach (var item in options.Environment ?? new Dictionary<string, string>()) start.Environment[item.Key] = item.Value;
        foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("The language server did not start.");
        return new(process, Math.Max(1024, options.StderrCapacity));
    }

    internal async Task StopAsync(Func<CancellationToken, Task>? gracefulShutdown, TimeSpan deadline,
        CancellationToken cancellationToken = default)
    {
        if (_process.HasExited) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        try
        {
            if (gracefulShutdown is not null) await gracefulShutdown(timeout.Token);
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillTree();
        }
    }

    private async Task PumpStderrAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) is var count && count > 0)
        {
            lock (_stderr)
            {
                _stderr.Append(buffer, 0, count);
                if (_stderr.Length > _stderrCapacity) _stderr.Remove(0, _stderr.Length - _stderrCapacity);
            }
        }
    }

    private void KillTree()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
    }

    private static IReadOnlyDictionary<string, string> SafeEnvironment()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_CLI_HOME", "USERPROFILE", "NUGET_PACKAGES"]
            : ["PATH", "LANG", "LC_ALL", "TMPDIR", "DOTNET_ROOT", "DOTNET_CLI_HOME", "HOME", "NUGET_PACKAGES"];
        return names.Select(name => (name, value: Environment.GetEnvironmentVariable(name)))
            .Where(item => item.value is not null).ToDictionary(item => item.name, item => item.value!);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) KillTree();
        try { await _stderrPump; } catch (IOException) { }
        _process.Dispose();
    }
}
