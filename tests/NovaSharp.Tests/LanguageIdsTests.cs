using NovaSharp.Editing;
using Xunit;

namespace NovaSharp.Tests;

public sealed class LanguageIdsTests
{
    [Theory]
    [InlineData("Widget.cs", "csharp")]
    [InlineData("WIDGET.CS", "csharp")]
    [InlineData("site.css", "css")]
    [InlineData("page.html", "html")]
    [InlineData("page.htm", "html")]
    [InlineData("notes.txt", LanguageIds.PlainText)]
    [InlineData("Makefile", LanguageIds.PlainText)]
    public void FromPath_MapsExtensionsToRegisteredLanguages(string fileName, string expected)
    {
        Assert.Equal(expected, LanguageIds.FromPath(Path.Combine(Path.GetTempPath(), fileName)));
    }
}
