using NovaSharp.Commands;
using Xunit;

namespace NovaSharp.Tests;

public sealed class KeybindingsTests
{
    [Theory]
    [InlineData("CtrlCmd+S", "CtrlCmd+KeyS")]
    [InlineData("ctrlcmd+s", "CtrlCmd+KeyS")]
    [InlineData("CtrlCmd+Shift+S", "CtrlCmd+Shift+KeyS")]
    [InlineData("Alt+Z", "Alt+KeyZ")]
    [InlineData("CtrlCmd+1", "CtrlCmd+Digit1")]
    [InlineData("F5", "F5")]
    [InlineData("f12", "F12")]
    [InlineData("Escape", "Escape")]
    [InlineData("CtrlCmd+UpArrow", "CtrlCmd+UpArrow")]
    [InlineData("CtrlCmd+KeyS", "CtrlCmd+KeyS")]
    public void TryNormalize_ProducesMonacosVocabulary(string input, string expected)
    {
        Assert.True(Keybindings.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+S")]
    [InlineData("Cmd+S")]
    [InlineData("CtrlCmd+Nonsense")]
    [InlineData("CtrlCmd+F20")]
    [InlineData("S+CtrlCmd")]
    public void TryNormalize_RefusesWhatMonacoCouldNotBind(string input)
    {
        Assert.False(Keybindings.TryNormalize(input, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void TryNormalize_NamesTheOffendingToken()
    {
        Keybindings.TryNormalize("Ctrl+S", out _, out var problem);

        // The message has to say which part was wrong; "invalid keybinding" leaves the author guessing which of three
        // tokens NovaSharp objected to.
        Assert.Contains("Ctrl", problem);
        Assert.Contains("CtrlCmd", problem);
    }
}
