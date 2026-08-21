using System.Text;

namespace NovaSharp.Editing;

/// <summary>An immutable view of a document at one sequence.</summary>
/// <param name="Text">The document text, using Monaco's line ending rather than the file's.</param>
/// <param name="Sequence">The sequence this text is the result of.</param>
/// <param name="AlternativeSequence">Monaco's alternative version identifier at this sequence.</param>
public sealed record DocumentSnapshot(string Text, long Sequence, long AlternativeSequence);

/// <summary>What happened when a batch was handed to a replica.</summary>
public enum ReplicaApplyOutcome
{
    /// <summary>The batch was applied and the sequence advanced.</summary>
    Applied,

    /// <summary>The batch was rejected and the replica needs a full resynchronization.</summary>
    NeedsResync,
}

/// <summary>
/// The ordered .NET shadow of one Monaco text model.
/// </summary>
/// <remarks>
/// Single writer by construction: exactly one pump applies batches to it. Reads take a snapshot, which is immutable
/// and safe to hand to a background worker, so no reader ever observes a half-applied batch. Nothing here is
/// persisted — ADR 0002 places the durable journal in phase 14 and records the gap.
/// </remarks>
public sealed class DocumentReplica
{
    private readonly Lock _gate = new();
    private readonly List<Waiter> _waiters = [];
    private readonly StringBuilder _text;

    private DocumentSnapshot? _snapshot;
    private long _sequence;
    private long _alternativeSequence;

    /// <summary>Creates a replica holding <paramref name="text"/> at <paramref name="sequence"/>.</summary>
    public DocumentReplica(string text, long sequence, long alternativeSequence)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = new StringBuilder(text);
        _sequence = sequence;
        _alternativeSequence = alternativeSequence;
    }

    /// <summary>The sequence of the most recently applied batch.</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    /// <summary>How many batches have been rejected and resynchronized, so saturation is measurable.</summary>
    public int ResyncCount { get; private set; }

    /// <summary>The reason the last rejected batch was rejected, for diagnostics.</summary>
    public TextEditBatchProblem LastProblem { get; private set; }

    /// <summary>Takes an immutable snapshot of the current text.</summary>
    /// <remarks>Cached until the next mutation, so repeated reads at one sequence do not each copy the buffer.</remarks>
    public DocumentSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= new DocumentSnapshot(_text.ToString(), _sequence, _alternativeSequence);
        }
    }

    /// <summary>Applies <paramref name="batch"/>, or reports that the replica has fallen out of step.</summary>
    public ReplicaApplyOutcome Apply(TextEditBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        List<Waiter>? released;
        lock (_gate)
        {
            var problem = batch.Validate(_sequence, _text.Length);
            if (problem != TextEditBatchProblem.None)
            {
                LastProblem = problem;
                return ReplicaApplyOutcome.NeedsResync;
            }

            // Applied from the end backwards so an earlier edit's length change cannot move a later edit's offsets.
            for (var i = batch.Edits.Count - 1; i >= 0; i--)
            {
                var edit = batch.Edits[i];
                _text.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
            }

            _sequence = batch.ResultSequence;
            _alternativeSequence = batch.AlternativeSequence;
            _snapshot = null;
            LastProblem = TextEditBatchProblem.None;
            released = TakeSatisfiedWaiters();
        }

        Release(released);
        return ReplicaApplyOutcome.Applied;
    }

    /// <summary>Replaces the whole replica with a fresh snapshot taken from Monaco.</summary>
    /// <remarks>
    /// The recovery path for a sequence gap or a saturated queue. It is deliberately the only way to set text without
    /// a batch: a shadow that can be assigned to freely is a shadow that can silently disagree with the editor.
    /// </remarks>
    public void Resync(string text, long sequence, long alternativeSequence)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<Waiter>? released;
        lock (_gate)
        {
            _text.Clear();
            _text.Append(text);
            _sequence = sequence;
            _alternativeSequence = alternativeSequence;
            _snapshot = null;
            ResyncCount++;
            released = TakeSatisfiedWaiters();
        }

        Release(released);
    }

    /// <summary>
    /// Completes once the replica has caught up to <paramref name="sequence"/>.
    /// </summary>
    /// <remarks>
    /// The barrier save and other consistency-sensitive commands wait on. It never blocks the caller's thread and is
    /// satisfied by a resynchronization past the requested sequence as well as by applying the batches that reach it,
    /// because either way the shadow is at least as new as what was asked for.
    /// </remarks>
    public Task WaitForSequenceAsync(long sequence, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Waiter waiter;
        lock (_gate)
        {
            if (_sequence >= sequence)
            {
                return Task.CompletedTask;
            }

            waiter = new Waiter(sequence);
            _waiters.Add(waiter);
        }

        // Registered outside the lock: the callback completes the waiter, and completing it inside the lock would run
        // continuations while holding it.
        var registration = cancellationToken.Register(
            static state => ((Waiter)state!).Completion.TrySetCanceled(),
            waiter);

        return AwaitAsync(waiter, registration);

        static async Task AwaitAsync(Waiter waiter, CancellationTokenRegistration registration)
        {
            using (registration)
            {
                await waiter.Completion.Task.ConfigureAwait(false);
            }
        }
    }

    private List<Waiter>? TakeSatisfiedWaiters()
    {
        if (_waiters.Count == 0)
        {
            return null;
        }

        List<Waiter>? satisfied = null;
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Sequence > _sequence)
            {
                continue;
            }

            (satisfied ??= []).Add(_waiters[i]);
            _waiters.RemoveAt(i);
        }

        return satisfied;
    }

    private static void Release(List<Waiter>? waiters)
    {
        if (waiters is null)
        {
            return;
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetResult();
        }
    }

    private sealed class Waiter(long sequence)
    {
        public long Sequence { get; } = sequence;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
