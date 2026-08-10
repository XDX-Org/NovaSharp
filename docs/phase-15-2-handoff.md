# Phase 15.2 handoff

Date: 2026-08-09  
Branch: `phase15-2`  
Base implementation: `d38db1a Implement Phase 15.2 language servers`

## Current state

The real Roslyn, HTML, and CSS language servers are packaged and start successfully. The latest working tree contains uncommitted follow-up fixes and UI/theme work made after `d38db1a`.

Verified locally:

- Roslyn survives rapid incremental edits without restarting.
- Roslyn opens the solution/project and returns compiler diagnostics in the real-server test.
- Restored documents are replayed with their current in-memory text.
- Ordinary `.cs` saves no longer trigger a project/explorer reload.
- Dynamic diagnostic and semantic-token registrations are retained.
- Roslyn's dynamically registered semantic-token legend is decoded.
- Hover ranges use the server's camel-case JSON fields and empty hovers are suppressed.
- All 24 `EditorCoreTests` and `LanguageServerTests` pass.
- The solution builds successfully.

Current verification command:

```sh
dotnet build NovaSharp.slnx --no-restore
dotnet tests/NovaSharp.Tests/bin/Debug/net10.0/NovaSharp.Tests.dll \
  --filter 'ClassName=NovaSharp.Tests.LanguageServerTests|ClassName=NovaSharp.Tests.EditorCoreTests' \
  --progress off --output Normal
```

## Resolved blocker: live Problems

Live Problems and editor squiggles now retain pull diagnostics across LSP `unchanged` reports. Pull state is tracked per server, document, and diagnostic identifier, and `previousResultId` is sent on subsequent requests. Responses for superseded editor versions are rejected instead of being published against the latest snapshot.

Roslyn's `workspace/diagnostic/refresh` request now triggers analysis through the workbench/editor path. The timing retries previously in `CodeEditor` were removed.

Regression coverage verifies retained `unchanged` reports, stale editor-version rejection, and rapid edits against the packaged Roslyn server.

## Semantic colouring decision

The rendered editor currently uses the local `CSharpTokenizer` only. Roslyn semantic tokens are still requested and tested, but are deliberately not overlaid in `CodeEditor.BuildPresentationAsync` because translated/stale spans repeatedly coloured the wrong words while typing.

The local tokenizer now includes Rider-style categories for namespaces, types, enum names/members, records, parameters, events, accessors, attributes, and embedded `GeneratedRegex` patterns. Theme colours are CSS variables in `wwwroot/app.css`.

This is a deliberate temporary deviation from `phase-15-2-LSPs.md`, whose goal says the server is the sole source of semantic highlighting. Decide whether to:

- keep stable lexical colouring and treat semantic tokens as optional enrichment; or
- reintroduce semantic tokens only with exact document-version ownership and no stale-span translation.

Do not restore the current semantic overlay without a rapid-edit UI test.

## Important implementation changes

- `LanguageDocumentCoordinator`: ordered incremental edits, CRLF/surrogate-safe ranges, synchronization before dependent requests.
- `LanguageServerManager`: Roslyn `solution/open`/`project/open`, crash stderr, safer cleanup, diagnostic capability negotiation.
- `LspClient`: null omission, dynamic registration storage, configuration responses.
- `LspLanguageProvider`: registered pull-diagnostic streams, merged diagnostics, dynamic semantic legend.
- `WorkbenchPanel`: restored-document replay and analysis trigger, persistent server indicators.
- `ProjectSystem`: source saves no longer count as project inputs.
- `CodeEditor`: cancellable language refreshes, local stable colouring, diagnostic retry attempt.
- `EditorCore`/`app.css`: local Rider-style tokenizer and theme palette.

## Working tree

The working tree is intentionally uncommitted. Review all changes before committing. `src/NovaSharp/BuildRun.cs` contains only an extra blank line and can be cleaned up if it is not intentional.

Use:

```sh
git status --short
git diff --check
git diff --stat
```

Do not discard the working tree: it contains the crash, synchronization, workspace-open, restored-document, hover, diagnostic, and theme fixes described above.
