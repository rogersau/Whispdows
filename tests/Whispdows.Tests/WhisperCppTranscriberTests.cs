using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class WhisperCppTranscriberTests
{
    [Fact]
    public void Validation_reports_the_exact_missing_model_path()
    {
        var modelPath = Path.Combine(
            Path.GetTempPath(),
            "WhispdowsTests",
            Guid.NewGuid().ToString("N"),
            "ggml-small.en.bin");
        using var transcriber = new WhisperCppTranscriber(modelPath, "en", 1);

        var exception = Assert.Throws<LocalModelNotFoundException>(
            transcriber.ValidateConfiguration);

        Assert.Equal(Path.GetFullPath(modelPath), exception.ModelPath);
        Assert.Contains(Path.GetFullPath(modelPath), exception.Message);
    }
}
