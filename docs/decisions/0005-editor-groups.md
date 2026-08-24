# 0005: Editor groups over shared Monaco models

## Status

Accepted.

## Decision

Keep document identity, file state, replication, and the one-model-per-canonical-URI lease in `DocumentRegistry` and
the Monaco host. A separate single-writer editor-group manager owns view identities, group-local tab order and focus,
and an immutable binary split tree. Split leaves are groups; branches contain horizontal or vertical orientation and a
clamped size ratio. Empty leaves and their parent branch are normalized away.

Each view is a separate Monaco editor instance attached to the document's existing `ITextModel`. Text, edit sequence,
dirty state, and undo history are therefore document-wide. Cursor, selection, and scroll state are captured and
validated per view. Closing a view releases only that editor instance; the registry closes and disposes the document
model only after its final view closes.

When a split-tree rewrite replaces a Blazor leaf container, capture the view state, dispose that editor instance,
create a new editor in the new empty container, and attach the existing model. Do not reparent Monaco-owned DOM or
recreate the model.

Use the same manager operations for commands and drag/drop. Edge drops split, center/tab drops transfer or copy, and
tab insertion uses the target index. Pointer resizing updates CSS once per animation frame and sends one ratio commit
after pointer release; keyboard splitters commit the same bounded ratio operation.

Bound the layout to four levels, 16 groups, and 256 views. Persist immutable layout snapshots asynchronously in
workspace-state schema 3. Persist the split tree, ratios, group tabs, active views, per-view states, and focused group.
Reject duplicate leaves, unknown documents, excessive depth, and malformed nodes, then restore one normalized group.

## Consequences

- Splitting never copies source text or introduces another document/model authority.
- Editing and undo are immediately coherent in every view of a document while navigation state remains independent.
- Closing a copied view cannot prompt to save or dispose the shared model while another view remains.
- The primary editor and dynamically mounted secondary editors use the same public Monaco APIs and packaged assets.
- Floating windows and arbitrary tool-panel docking remain outside this layout tree.
