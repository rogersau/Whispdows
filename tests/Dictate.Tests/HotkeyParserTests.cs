using Dictate;
using Xunit;

namespace Dictate.Tests;

public sealed class HotkeyParserTests
{
    [Fact]
    public void Parses_the_default_right_control_trigger()
    {
        var shortcut = HotkeyParser.Parse("RightCtrl");

        Assert.Equal(HotkeyModifiers.None, shortcut.Modifiers);
        Assert.Equal(VirtualKey.RightControl, shortcut.TriggerKey);
    }

    [Fact]
    public void Parses_a_modifier_chord()
    {
        var shortcut = HotkeyParser.Parse("Ctrl+Win+Space");

        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Windows,
            shortcut.Modifiers);
        Assert.Equal(VirtualKey.Space, shortcut.TriggerKey);
    }

    [Theory]
    [InlineData("F13", VirtualKey.F13)]
    [InlineData("a", (VirtualKey)0x41)]
    [InlineData("7", (VirtualKey)0x37)]
    public void Parses_supported_single_keys(string text, VirtualKey expected)
    {
        var shortcut = HotkeyParser.Parse(text);

        Assert.Equal(expected, shortcut.TriggerKey);
    }

    [Theory]
    [InlineData("Ctrl++Space")]
    [InlineData("Ctrl+Ctrl+Space")]
    [InlineData("Ctrl+Win")]
    [InlineData("Ctrl+VolumeUp")]
    [InlineData("Escape")]
    [InlineData("Ctrl+RightCtrl")]
    public void Rejects_invalid_shortcuts(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
