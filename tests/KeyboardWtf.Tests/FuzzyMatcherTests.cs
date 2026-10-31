namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class FuzzyMatcherTests
{
    [Theory]
    [InlineData("codex", "Codex")]
    [InlineData("openai codex", "OpenAI Codex")]
    [InlineData("visual code", "Visual Studio Code")]
    [InlineData("spotfy", "Spotify")]
    [InlineData("aple music", "Apple Music")]
    public void NaturalOrMisspelledNamesScoreHighly(string query, string candidate)
    {
        Assert.True(FuzzyMatcher.Score(query, candidate) >= 0.68);
    }

    [Fact]
    public void UnrelatedNamesScoreLow()
    {
        Assert.True(FuzzyMatcher.Score("codex", "Calculator") < 0.5);
    }

    [Fact]
    public void RoutineTriggerVariantsRemainCloseEnoughToResolve()
    {
        Assert.True(FuzzyMatcher.Score("initialize my set", "initialize my setup") >= 0.72);
    }
}
