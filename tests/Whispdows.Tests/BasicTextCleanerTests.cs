using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class BasicTextCleanerTests
{
    [Theory]
    [InlineData("  hello   world  ", "hello world")]
    [InlineData("um um this works", "this works")]
    [InlineData("I uh uh think so", "I uh think so")]
    [InlineData("erm, keep the wording", "keep the wording")]
    public async Task Auto_style_only_performs_safe_cleanup(string input, string expected)
    {
        var cleaner = new BasicTextCleaner("auto");

        var result = await cleaner.CleanAsync(input, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("hello world", "Hello world.")]
    [InlineData("\"hello world\"", "\"Hello world.\"")]
    [InlineData("already done!", "Already done!")]
    public async Task Sentence_style_capitalizes_and_adds_punctuation(
        string input,
        string expected)
    {
        var cleaner = new BasicTextCleaner("sentence");

        var result = await cleaner.CleanAsync(input, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Fragment_style_does_not_add_sentence_punctuation()
    {
        var cleaner = new BasicTextCleaner("fragment");

        var result = await cleaner.CleanAsync("quick note", CancellationToken.None);

        Assert.Equal("quick note", result);
    }
}
