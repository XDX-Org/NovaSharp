using System.Text;
using System.Text.Json;
using NovaSharp.Async;
using NovaSharp.Configuration;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Text;
using Xunit;

namespace NovaSharp.Tests;

public sealed class SettingsResolverTests
{
    private static SettingsResolution Resolve(SettingsDocument? user, SettingsDocument? workspace = null) =>
        SettingsResolver.Resolve(user, "user.json", workspace, "workspace.json");

    [Fact]
    public void Resolve_UsesTheDefaultsWhenNothingIsConfigured()
    {
        var resolution = Resolve(user: null);

        Assert.True(resolution.IsClean);
        Assert.Equal(WorkbenchSettings.Defaults, resolution.Settings);
    }

    [Fact]
    public void Resolve_LetsAWorkspaceOverrideTheUserKeyByKey()
    {
        // Absent and "set to the default" are different. A workspace file that says nothing about the encoding must
        // leave the user's choice alone rather than reset it.
        var user = new SettingsDocument { DefaultEncoding = "utf-8-bom", DefaultLineEnding = LineEndingStyle.CrLf };
        var workspace = new SettingsDocument { DefaultLineEnding = LineEndingStyle.Lf };

        var resolution = Resolve(user, workspace);

        Assert.True(resolution.IsClean);
        Assert.Equal("utf-8-bom", resolution.Settings.DefaultEncoding.Id);
        Assert.Equal(LineEndingStyle.Lf, resolution.Settings.DefaultLineEnding);
    }

    [Fact]
    public void Resolve_ReportsAnEncodingThisPlatformDoesNotHave()
    {
        var resolution = Resolve(new SettingsDocument { DefaultEncoding = "not-an-encoding" });

        var problem = Assert.Single(resolution.Problems);
        Assert.Equal(SettingsScope.User, problem.Scope);
        Assert.Contains("not-an-encoding", problem.Message);
        Assert.Equal(WorkbenchSettings.Defaults.DefaultEncoding, resolution.Settings.DefaultEncoding);
    }

    [Fact]
    public void Resolve_RefusesAFallbackThatCouldNotRoundTripEveryByte()
    {
        // The fallback exists to open bytes nothing else accepted. One that cannot represent every byte value would
        // turn an unreadable file into a corrupted one the moment it was saved.
        var resolution = Resolve(new SettingsDocument { FallbackEncoding = "utf-8" });

        Assert.Contains(resolution.Problems, problem => problem.Message.Contains("round-trip"));
        Assert.Equal(TextEncodings.Latin1, resolution.Settings.FallbackEncoding);
    }

    [Fact]
    public void Resolve_AcceptsAFallbackThatCan()
    {
        var resolution = Resolve(new SettingsDocument { FallbackEncoding = "iso-8859-1" });

        Assert.True(resolution.IsClean);
        Assert.Equal("iso-8859-1", resolution.Settings.FallbackEncoding.Id);
    }

    [Fact]
    public void Resolve_IgnoresAFileWrittenByANewerNovaSharp()
    {
        // Guessing at what a newer version's keys mean is how a settings file gets silently rewritten into something
        // the version that wrote it no longer understands.
        var resolution = Resolve(new SettingsDocument
        {
            SchemaVersion = WorkbenchSettings.CurrentSchemaVersion + 1,
            DefaultEncoding = "utf-8-bom",
        });

        Assert.Contains(resolution.Problems, problem => problem.Message.Contains("newer"));
        Assert.Equal(WorkbenchSettings.Defaults.DefaultEncoding, resolution.Settings.DefaultEncoding);
    }

    [Fact]
    public void Resolve_ReportsAnUndefinedLineEnding()
    {
        var resolution = Resolve(new SettingsDocument { DefaultLineEnding = (LineEndingStyle)42 });

        Assert.Single(resolution.Problems);
        Assert.Equal(WorkbenchSettings.Defaults.DefaultLineEnding, resolution.Settings.DefaultLineEnding);
    }

    [Fact]
    public void Resolve_KeepsTheGoodKeysFromAFileWithABadOne()
    {
        var resolution = Resolve(new SettingsDocument
        {
            DefaultEncoding = "not-an-encoding",
            DefaultLineEnding = LineEndingStyle.CrLf,
            ReloadUnmodifiedFiles = false,
        });

        Assert.Single(resolution.Problems);
        Assert.Equal(LineEndingStyle.CrLf, resolution.Settings.DefaultLineEnding);
        Assert.False(resolution.Settings.ReloadUnmodifiedFiles);
    }

    [Fact]
    public void Resolve_ValidatesWorkspaceIgnorePatterns()
    {
        var resolution = Resolve(new SettingsDocument
        {
            WorkspaceIgnoredPaths = ["generated", "artifacts/*.tmp", "../outside"],
        });

        Assert.Equal(["generated", "artifacts/*.tmp"], resolution.Settings.WorkspaceIgnoredPaths);
        Assert.Contains(resolution.Problems, problem => problem.Message.Contains("../outside"));
    }
}

public sealed class ConfigurationServiceTests : IAsyncDisposable
{
    private readonly BoundedWorkQueue _queue = new(capacity: 8, workerCount: 1);
    private readonly DocumentFileStore _store = new();
    private readonly string _root = Directory.CreateTempSubdirectory("novasharp-config").FullName;
    private readonly ConfigurationService _service;

    public ConfigurationServiceTests() =>
        _service = new ConfigurationService(new FakeApplicationPaths(Path.Combine(_root, "user")), _store, _queue);

    private sealed class FakeApplicationPaths(string directory) : IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = directory;
    }

    private async Task WriteAsync(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
    }

    private string WorkspaceRoot
    {
        get
        {
            var path = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    [Fact]
    public async Task LoadAsync_UsesTheDefaultsWhenNoFileExists()
    {
        // A missing file is an empty scope, not a failure; reporting one would mean every first run started with a
        // problem the user cannot act on.
        var resolution = await _service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(resolution.IsClean);
        Assert.Equal(WorkbenchSettings.Defaults, resolution.Settings);
    }

    [Fact]
    public async Task LoadAsync_ReadsBothScopes()
    {
        await WriteAsync(_service.UserFilePath, """{ "defaultEncoding": "utf-8-bom", "defaultLineEnding": "CrLf" }""");
        _service.SetWorkspaceRoot(WorkspaceRoot);
        await WriteAsync(_service.WorkspaceFilePath!, """{ "defaultLineEnding": "Lf" }""");

        var resolution = await _service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(resolution.IsClean);
        Assert.Equal("utf-8-bom", resolution.Settings.DefaultEncoding.Id);
        Assert.Equal(LineEndingStyle.Lf, resolution.Settings.DefaultLineEnding);
    }

    [Fact]
    public async Task LoadAsync_KeepsAFileItCannotParseAndCopiesItAside()
    {
        // Rewriting a file somebody hand-edited would throw away the work that broke it.
        await WriteAsync(_service.UserFilePath, "{ this is not json");

        var resolution = await _service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(resolution.IsClean);
        Assert.Equal(WorkbenchSettings.Defaults, resolution.Settings);
        Assert.True(File.Exists(_service.UserFilePath));
        Assert.True(File.Exists(_service.UserFilePath + ".invalid"));
    }

    [Fact]
    public async Task SaveUserAsync_WritesAVersionedFileAndReloadsIt()
    {
        var resolution = await _service.SaveUserAsync(
            new SettingsDocument { DefaultEncoding = "utf-8-bom" },
            TestContext.Current.CancellationToken);

        Assert.True(resolution.IsClean);
        Assert.Equal("utf-8-bom", resolution.Settings.DefaultEncoding.Id);

        var written = await File.ReadAllTextAsync(_service.UserFilePath, TestContext.Current.CancellationToken);
        var document = JsonSerializer.Deserialize<SettingsDocument>(
            Encoding.UTF8.GetBytes(written),
            SettingsDocument.SerializerOptions);

        // Versioned from the first write rather than when a migration first needs it: a file with no version is one
        // no later migration can identify.
        Assert.Equal(WorkbenchSettings.CurrentSchemaVersion, document?.SchemaVersion);
        Assert.Contains("\"defaultEncoding\"", written, StringComparison.Ordinal);

        // Enums are written as names, because a number in a hand-edited file is unreadable and silently changes
        // meaning if the enum is ever reordered.
        await _service.SaveUserAsync(
            new SettingsDocument { DefaultLineEnding = LineEndingStyle.CrLf },
            TestContext.Current.CancellationToken);
        var reread = await File.ReadAllTextAsync(_service.UserFilePath, TestContext.Current.CancellationToken);
        Assert.Contains("\"CrLf\"", reread, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveWorkspaceAsync_RefusesWhenNoWorkspaceIsOpen()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SaveWorkspaceAsync(new SettingsDocument(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveWorkspaceAsync_WritesBesideTheWorkspaceRoot()
    {
        _service.SetWorkspaceRoot(WorkspaceRoot);

        await _service.SaveWorkspaceAsync(
            new SettingsDocument { ReloadUnmodifiedFiles = false },
            TestContext.Current.CancellationToken);

        var expected = Path.Combine(WorkspaceRoot, ConfigurationService.WorkspaceFolderName, ConfigurationService.FileName);
        Assert.Equal(expected, _service.WorkspaceFilePath);
        Assert.True(File.Exists(expected));
        Assert.False(_service.Current.Settings.ReloadUnmodifiedFiles);
    }

    [Fact]
    public async Task Changed_PublishesWhatIsNowInForce()
    {
        SettingsResolution? published = null;
        _service.Changed += resolution => published = resolution;

        await _service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(published);
        Assert.Equal(_service.Current.Settings, published.Settings);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
