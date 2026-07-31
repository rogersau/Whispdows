using Dictate;
using Xunit;

namespace Dictate.Tests;

public sealed class TextInserterTests
{
    [Fact]
    public void Native_input_layout_matches_win32_input_size()
    {
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expectedSize, WindowsInputSender.NativeInputSize);
    }

    [Fact]
    public async Task Changed_target_copies_without_sending_paste()
    {
        var clipboard = new FakeClipboard();
        var input = new FakeInputSender();
        var inserter = CreateInserter(clipboard, input, foregroundWindow: new IntPtr(2));

        var result = await inserter.InsertAsync(
            "dictated text",
            new IntPtr(1),
            CancellationToken.None);

        Assert.Equal(TextInsertionResult.TargetChanged, result);
        Assert.Equal("dictated text", clipboard.Text);
        Assert.Equal(0, input.CallCount);
    }

    [Fact]
    public async Task Successful_paste_restores_owned_clipboard()
    {
        var clipboard = new FakeClipboard { Text = "previous" };
        var input = new FakeInputSender();
        var inserter = CreateInserter(clipboard, input, foregroundWindow: new IntPtr(1));

        var result = await inserter.InsertAsync(
            "dictated text",
            new IntPtr(1),
            CancellationToken.None);

        Assert.Equal(TextInsertionResult.Pasted, result);
        Assert.Equal("previous", clipboard.Text);
        Assert.Equal(1, input.CallCount);
    }

    [Fact]
    public async Task Newer_clipboard_value_is_never_overwritten()
    {
        var clipboard = new FakeClipboard { Text = "previous" };
        var input = new FakeInputSender
        {
            OnSend = () => clipboard.SetExternalText("newer value")
        };
        var inserter = CreateInserter(clipboard, input, foregroundWindow: new IntPtr(1));

        var result = await inserter.InsertAsync(
            "dictated text",
            new IntPtr(1),
            CancellationToken.None);

        Assert.Equal(TextInsertionResult.Pasted, result);
        Assert.Equal("newer value", clipboard.Text);
        Assert.Equal(0, clipboard.RestoreCount);
    }

    [Fact]
    public async Task Failed_input_leaves_dictated_text_on_clipboard()
    {
        var clipboard = new FakeClipboard { Text = "previous" };
        var input = new FakeInputSender { Succeeds = false };
        var inserter = CreateInserter(clipboard, input, foregroundWindow: new IntPtr(1));

        var result = await inserter.InsertAsync(
            "dictated text",
            new IntPtr(1),
            CancellationToken.None);

        Assert.Equal(TextInsertionResult.Copied, result);
        Assert.Equal("dictated text", clipboard.Text);
    }

    private static TextInserter CreateInserter(
        IClipboardService clipboard,
        IInputSender input,
        nint foregroundWindow)
    {
        return new TextInserter(
            new PasteSettings
            {
                RestoreClipboard = true,
                RestoreDelayMs = 0
            },
            clipboard,
            input,
            () => foregroundWindow);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        private uint _sequence;

        public string? Text { get; set; }

        public int RestoreCount { get; private set; }

        public Task<ClipboardSnapshot?> CaptureAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<ClipboardSnapshot?>(
                new ClipboardSnapshot(Text ?? string.Empty));
        }

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            _sequence++;
            return Task.CompletedTask;
        }

        public uint GetSequenceNumber() => _sequence;

        public Task<bool> ContainsTextAsync(
            string text,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Equals(Text, text, StringComparison.Ordinal));
        }

        public Task RestoreAsync(
            ClipboardSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            RestoreCount++;
            Text = (string)snapshot.Data;
            _sequence++;
            return Task.CompletedTask;
        }

        public void SetExternalText(string text)
        {
            Text = text;
            _sequence++;
        }
    }

    private sealed class FakeInputSender : IInputSender
    {
        public bool Succeeds { get; set; } = true;

        public int CallCount { get; private set; }

        public Action? OnSend { get; set; }

        public bool SendPasteShortcut()
        {
            CallCount++;
            OnSend?.Invoke();
            return Succeeds;
        }
    }
}
