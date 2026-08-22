# Editor host tests

Browser-level gates for phases 1, 2, and 4. These assert what only a real browser can show about the packaged Monaco
editor:

- the bundle and its worker load from the application's own origin, with no runtime network access;
- the editor worker starts as a real dedicated worker rather than falling back to the browser thread;
- C# lexical colours come from the packaged language definition;
- typing, composition, surrogate pairs, and undo are owned by Monaco, with no round trip to .NET;
- one text model exists per canonical document URI, and disposal releases it;
- tab switching reattaches retained models, restores view state, survives rapid switching, rekeys renamed documents,
  and releases only the model whose last lease closes;
- the edit batches the host produces reconstruct Monaco's text exactly in a shadow that only ever sees those batches,
  with at most one replication call in flight, ascending non-overlapping offsets, and no rejected batch;
- a line-ending change asks for a resynchronization rather than sending offsets into text the shadow does not have;
- a NovaSharp-driven replacement stays undoable and sends no batches, and replication resumes cleanly after it;
- a read-only editor refuses edits, and the save shortcut reaches the command identifier .NET registers;
- find, long-line navigation, scrolling, a 60-second typing/replication run against the 2,000-line fixture,
  browser-thread long tasks, bounded overflow recovery, and 100 create/open/dispose cycles stay within their phase
  budgets.

The shadow in the harness is a deliberate second implementation of `DocumentReplica`'s apply rule. Agreement between
two independent implementations of the same protocol is what makes the replication contract a gate rather than a
restatement of the code under test.

## Running

The Monaco assets must already be built, which the repository bootstrap does. Then:

```bash
cd tests/editor-host
npm ci
npx playwright install chromium
npm test
```

To run against a Chromium that is already present rather than downloading one, set `NOVASHARP_CHROMIUM_PATH` to its
executable and skip `npx playwright install`.

## Status

The suite runs in [CI](../../.github/workflows/ci.yml) on every runtime identifier in the
[supported platform matrix](../../docs/delivery-plan.md#supported-platform-matrix), against the Chromium build pinned
by the Playwright version in `package-lock.json`. CI sets `NOVASHARP_BROWSER_METRICS` and
`NOVASHARP_FIXTURE_NAME`, causing the suite to write the measured values and all 69 assertions to the RID's retained
JSON evidence.

It is deliberately not part of the bootstrap. The bootstrap acquires only hash-pinned assets, and adding a browser
download to it would put an unpinned dependency in the one place the repository guarantees there are none. CI installs
the browser through the same lockfile that pins everything else.

Until the new workflow has been green on every row, this suite proves the editor contract on the machines it has
actually run on, not on every supported platform.
