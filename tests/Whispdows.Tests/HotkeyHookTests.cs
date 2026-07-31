using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class HotkeyHookTests
{
    [Theory]
    [InlineData(0x11u, 0x1Du, 0x01u, VirtualKey.RightControl)]
    [InlineData(0x11u, 0x1Du, 0x00u, VirtualKey.LeftControl)]
    [InlineData(0x10u, 0x36u, 0x00u, VirtualKey.RightShift)]
    [InlineData(0x10u, 0x2Au, 0x00u, VirtualKey.LeftShift)]
    [InlineData(0x12u, 0x38u, 0x01u, VirtualKey.RightAlt)]
    public void Normalizes_side_specific_modifier_keys(
        uint virtualKey,
        uint scanCode,
        uint flags,
        VirtualKey expected)
    {
        Assert.Equal(
            (uint)expected,
            HotkeyHook.NormalizeVirtualKey(virtualKey, scanCode, flags));
    }
}
