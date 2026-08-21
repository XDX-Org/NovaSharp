using NovaSharp.Platform;
using Xunit;

namespace NovaSharp.Tests;

public sealed class WorkspacePathsTests
{
    private readonly WorkspacePaths _paths = new();

    [Fact]
    public void ToDocumentUri_ProducesAnAbsoluteFileUri()
    {
        var path = Path.Combine(Path.GetTempPath(), "novasharp-sample.cs");

        var uri = _paths.ToDocumentUri(path);

        Assert.True(uri.IsAbsoluteUri);
        Assert.Equal(Uri.UriSchemeFile, uri.Scheme);
        Assert.EndsWith("novasharp-sample.cs", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDocumentUri_IsStableAcrossEquivalentSpellingsOfTheSamePath()
    {
        var directory = Path.GetTempPath();
        var direct = Path.Combine(directory, "sample.cs");
        var indirect = Path.Combine(directory, "nested", "..", "sample.cs");

        Assert.True(_paths.IsSameDocument(_paths.ToDocumentUri(direct), _paths.ToDocumentUri(indirect)));
    }

    [Fact]
    public void ToDocumentUri_EscapesCharactersThatWouldOtherwiseChangeTheUri()
    {
        // A space or a '#' in a file name must survive the round trip. If '#' were left unescaped the rest of the
        // name would become a URI fragment and Monaco would key the model on a truncated identity.
        var path = Path.Combine(Path.GetTempPath(), "a b#c.cs");

        var uri = _paths.ToDocumentUri(path);

        Assert.Equal(path, uri.LocalPath);
        Assert.DoesNotContain(" ", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(string.Empty, uri.Fragment);
    }

    [Fact]
    public void ToDocumentUri_RejectsEmptyInput()
    {
        Assert.Throws<ArgumentException>(() => _paths.ToDocumentUri("   "));
    }

    [Fact]
    public void ToDisplayName_ReturnsTheFileName()
    {
        var path = Path.Combine(Path.GetTempPath(), "Widget.cs");

        Assert.Equal("Widget.cs", _paths.ToDisplayName(path));
    }

    [Fact]
    public void IsSameDocument_DistinguishesDifferentDocuments()
    {
        var left = _paths.ToDocumentUri(Path.Combine(Path.GetTempPath(), "left.cs"));
        var right = _paths.ToDocumentUri(Path.Combine(Path.GetTempPath(), "right.cs"));

        Assert.False(_paths.IsSameDocument(left, right));
    }
}
