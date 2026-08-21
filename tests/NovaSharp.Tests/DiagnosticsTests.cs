using NovaSharp.Diagnostics;
using Xunit;

namespace NovaSharp.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void Path_KeepsTheFileNameAndHidesTheDirectory()
    {
        var redacted = Redaction.Path(Path.Combine("home", "someone", "Secret Project", "Widget.cs"));

        Assert.EndsWith("/Widget.cs", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Project", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_IsStableSoTwoEntriesAboutOneFolderStillLookRelated()
    {
        var first = Redaction.Path(Path.Combine("workspace", "a.cs"));
        var second = Redaction.Path(Path.Combine("workspace", "b.cs"));

        Assert.Equal(first.Split('/')[0], second.Split('/')[0]);
    }

    [Fact]
    public void Path_DistinguishesDifferentDirectories()
    {
        var first = Redaction.Path(Path.Combine("one", "a.cs"));
        var second = Redaction.Path(Path.Combine("two", "a.cs"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Path_HandlesTheAbsentCases()
    {
        Assert.Equal("[no path]", Redaction.Path(null));
        Assert.Equal("[no path]", Redaction.Path("   "));
        Assert.Equal("Widget.cs", Redaction.Path("Widget.cs"));
    }

    [Fact]
    public void Text_ReportsTheLengthAndNothingElse()
    {
        var redacted = Redaction.Text("private const string ApiKey = \"hunter2\";");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("40 characters", redacted, StringComparison.Ordinal);
        Assert.Equal("[none]", Redaction.Text(null));
    }
}

public sealed class BoundedWorkbenchLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Write_RecordsWhatHappened()
    {
        var log = new BoundedWorkbenchLog(timeProvider: new FakeTimeProvider(Noon));

        log.Write(LogLevel.Warning, "documents", "A save was superseded.");

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("documents", entry.Category);
        Assert.Equal(Noon, entry.Timestamp);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void Write_KeepsAnExceptionsTypeAndMessageButNotItsStackTrace()
    {
        // A stack trace carries file paths from the machine NovaSharp was built on, which must not reach a shipped
        // binary's output any more than it reaches the binary itself.
        var log = new BoundedWorkbenchLog();

        try
        {
            throw new InvalidOperationException("the queue was full");
        }
        catch (InvalidOperationException exception)
        {
            log.Write(LogLevel.Error, "documents", "Replication failed.", exception);
        }

        var entry = Assert.Single(log.Entries);
        Assert.Equal("InvalidOperationException: the queue was full", entry.Exception);
        Assert.DoesNotContain(".cs:line", entry.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_DropsTheOldestRatherThanGrowing()
    {
        // A failure that repeats is exactly when an unbounded log does the most damage, so the bound is structural
        // rather than a periodic trim.
        var log = new BoundedWorkbenchLog(capacity: 3);

        for (var i = 0; i < 10; i++)
        {
            log.Write(LogLevel.Information, "test", i.ToString());
        }

        Assert.Equal(3, log.Entries.Count);
        Assert.Equal(["7", "8", "9"], log.Entries.Select(entry => entry.Message));
        Assert.Equal(7, log.DroppedCount);
    }
}

public sealed class NotificationServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly BoundedWorkbenchLog _log = new();

    private NotificationService Create(int capacity = 20) =>
        new(_log, capacity, new FakeTimeProvider(Noon));

    [Fact]
    public void Raise_ShowsAndLogsTheNotification()
    {
        var service = Create();

        service.Raise("test.one", NotificationSeverity.Error, "It did not work.");

        var notification = Assert.Single(service.Active);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal(Noon, notification.Raised);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message == "It did not work.");
    }

    [Fact]
    public void Raise_ReplacesAnEarlierNotificationWithTheSameIdentifier()
    {
        // A watcher firing three times for one save is one thing to tell the user, not three.
        var service = Create();

        service.Raise("test.same", NotificationSeverity.Warning, "First.");
        service.Raise("test.same", NotificationSeverity.Warning, "Second.");
        service.Raise("test.same", NotificationSeverity.Warning, "Third.");

        Assert.Equal("Third.", Assert.Single(service.Active).Message);
    }

    [Fact]
    public void Raise_DropsTheOldestOnceItIsFull()
    {
        var service = Create(capacity: 2);

        service.Raise("a", NotificationSeverity.Information, "A");
        service.Raise("b", NotificationSeverity.Information, "B");
        service.Raise("c", NotificationSeverity.Information, "C");

        Assert.Equal(["B", "C"], service.Active.Select(notification => notification.Message));
    }

    [Fact]
    public void Raise_CarriesActionsAsCommandIdentifiers()
    {
        // Commands rather than callbacks, so the button a notification offers is the same command the palette and the
        // toolbar invoke, with the same enablement.
        var service = Create();

        service.Raise(new Notification(
            "test.actionable",
            NotificationSeverity.Warning,
            "The file changed on disk.",
            [new NotificationAction("novasharp.document.reload", "Reload from disk")],
            Noon));

        var action = Assert.Single(Assert.Single(service.Active).Actions);
        Assert.Equal("novasharp.document.reload", action.CommandId);
    }

    [Fact]
    public void Dismiss_RemovesOnlyThatNotification()
    {
        var service = Create();
        service.Raise("a", NotificationSeverity.Information, "A");
        service.Raise("b", NotificationSeverity.Information, "B");

        service.Dismiss("a");

        Assert.Equal("B", Assert.Single(service.Active).Message);
    }

    [Fact]
    public void Changed_IsRaisedOnlyWhenSomethingChanged()
    {
        var service = Create();
        var changes = 0;
        service.Changed += _ => changes++;

        service.Raise("a", NotificationSeverity.Information, "A");
        service.Dismiss("a");
        service.Dismiss("a");

        Assert.Equal(2, changes);
    }
}

/// <summary>A clock that does not move, so timestamps are asserted rather than tolerated.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
