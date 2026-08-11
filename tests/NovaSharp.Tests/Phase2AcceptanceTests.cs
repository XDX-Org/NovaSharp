namespace NovaSharp.Tests;

[TestClass]
public sealed class Phase2AcceptanceTests
{
    [TestMethod]
    public async Task SaveDoesNotReportItsOwnDelayedWatcherEventAsExternal()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "class Before;");
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        var externalChanges = 0;
        document.ExternalChangeDetected += () => Interlocked.Increment(ref externalChanges);

        document.Content = "class After;";
        await document.SaveAsync();
        await Task.Delay(400);

        Assert.AreEqual(0, externalChanges);
        Assert.IsFalse(document.HasChangedOnDisk());
    }

    [TestMethod]
    public async Task InterruptedAtomicWriteLeavesOriginalAndNoTemporarySibling()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "original");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => AtomicFile.WriteAsync(path, new byte[1024], cancellation.Token));

        Assert.AreEqual("original", await File.ReadAllTextAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*.tmp").Length);
    }

    [TestMethod]
    public async Task ExternalChangeNotificationIsDebouncedAndVersionChecked()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "class Before;");
        using var document = new EditorDocumentState();
        await document.OpenAsync(path);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        document.ExternalChangeDetected += () => changed.TrySetResult();

        await File.WriteAllTextAsync(path, "class External;");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(document.HasChangedOnDisk());
        Assert.AreEqual("class Before;", document.Content);
    }

    [TestMethod]
    public async Task SettingsSupportValidatedUserAndWorkspaceScopes()
    {
        Assert.IsTrue(KeyGesture.IsValid("LeftShift+LeftShift"));
        var user = TempPath();
        var workspace = TempPath();
        var service = new ConfigurationService(user);
        Assert.IsFalse(service.Current.BraceGuides);
        await service.SaveUserAsync(new(Zoom: 110));
        await service.UseWorkspaceAsync(workspace);
        Assert.AreEqual(110, service.Current.Zoom);
        await service.SaveWorkspaceAsync(service.Current with
            { Zoom = 130, Keybindings = new() { ["workbench.action.files.save"] = "Ctrl+Alt+S" } });
        await service.LoadAsync();
        Assert.AreEqual(130, service.Current.Zoom);
        Assert.AreEqual("Ctrl+Alt+S", service.Current.Keybindings!["workbench.action.files.save"]);
        await File.WriteAllTextAsync(workspace, "{\"Zoom\":130,\"Keybindings\":{\"save\":\"Ctrl+Ctrl+S\"}}");
        await service.LoadAsync();
        Assert.AreEqual(110, service.Current.Zoom);
    }

    private static string TempPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "document.cs");
    }
}
