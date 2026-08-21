using NovaSharp.Text;

namespace NovaSharp.Editing;

/// <summary>What NovaSharp last observed about the file behind the open document.</summary>
public enum ExternalChangeState
{
    /// <summary>The file is as NovaSharp last saw it.</summary>
    None,

    /// <summary>Something else wrote to the file.</summary>
    Modified,

    /// <summary>The file is no longer there.</summary>
    Deleted,
}

/// <summary>
/// A small immutable description of the open document, published to the workbench.
/// </summary>
/// <remarks>
/// Snapshots rather than shared state: background work builds one of these and hands it over, so no renderer ever
/// reads a field another thread is in the middle of writing, and no content change has to call
/// <c>StateHasChanged</c> to keep the UI honest.
/// </remarks>
/// <param name="IsOpen">Whether a document is open at all.</param>
/// <param name="DisplayName">The short name shown in the workbench.</param>
/// <param name="Path">The full path, shown as a tooltip.</param>
/// <param name="IsDirty">Whether the editor's text differs from the file.</param>
/// <param name="IsReadOnly">Whether the file cannot be written.</param>
/// <param name="Encoding">The encoding a save will write with.</param>
/// <param name="LineEnding">The line ending a save will write.</param>
/// <param name="LineEndingsWereMixed">Whether the file held more than one kind of ending when it was opened.</param>
/// <param name="DecodedWithFallback">Whether the encoding above was a fallback rather than a confident answer.</param>
/// <param name="ExternalChange">What has happened to the file behind NovaSharp's back.</param>
/// <param name="IsBusy">Whether a save, load, or reload is in flight.</param>
/// <param name="IsComparing">Whether the file on disk is being shown beside the editor's text.</param>
public sealed record DocumentStatus(
    bool IsOpen = false,
    string DisplayName = "No file open",
    string? Path = null,
    bool IsDirty = false,
    bool IsReadOnly = false,
    TextEncodingProfile? Encoding = null,
    LineEndingStyle LineEnding = LineEndingStyle.Lf,
    bool LineEndingsWereMixed = false,
    bool DecodedWithFallback = false,
    ExternalChangeState ExternalChange = ExternalChangeState.None,
    bool IsBusy = false,
    bool IsComparing = false);
