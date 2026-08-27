namespace NovaSharp.Editing;

/// <summary>The state handed to Monaco when a document is opened or reloaded.</summary>
/// <remarks>
/// Text crosses the interop boundary here and in a snapshot resynchronization, including the resynchronization needed
/// to replace an immutable model URI. Ordinary editing never sends a whole document in either direction.
/// </remarks>
/// <param name="Uri">The host's canonical document URI used as the editor protocol identity.</param>
/// <param name="DisplayName">The short name shown in the workbench.</param>
/// <param name="LanguageId">The Monaco language identifier.</param>
/// <param name="Text">The document text, with line endings already normalized to <paramref name="LineEnding"/>.</param>
/// <param name="LineEnding">The line ending sequence Monaco inserts, either <c>\n</c> or <c>\r\n</c>.</param>
/// <param name="ReadOnly">Whether Monaco should refuse edits because the file cannot be written.</param>
public sealed record DocumentContent(
    Uri Uri,
    string DisplayName,
    string LanguageId,
    string Text,
    string LineEnding,
    bool ReadOnly);
