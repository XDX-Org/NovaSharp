namespace NovaSharp.Platform;

/// <summary>
/// Converts operating-system file paths into the canonical document identity that NovaSharp and Monaco share.
/// </summary>
/// <remarks>
/// This is the seam that keeps path handling out of feature code. Implementations must not branch on the host
/// operating system: the framework primitives used here already behave correctly per platform.
/// </remarks>
public interface IWorkspacePaths
{
    /// <summary>Returns the canonical <c>file:</c> URI that identifies the document at <paramref name="path"/>.</summary>
    Uri ToDocumentUri(string path);

    /// <summary>Returns the short name shown in the workbench for <paramref name="path"/>.</summary>
    string ToDisplayName(string path);

    /// <summary>Returns whether two document URIs identify the same document.</summary>
    bool IsSameDocument(Uri left, Uri right);
}
