using FluentAssertions;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class ReasoningFilterTests
{
    private static (string Visible, bool Saw) Stream(params string[] pieces)
    {
        var f = new ReasoningFilter();
        var visible = string.Concat(pieces.Select(f.Push)) + f.Flush();
        return (visible, f.SawReasoning);
    }

    [Fact]
    public void Hides_a_think_block_split_across_pieces()
    {
        var (visible, saw) = Stream("<th", "ink>Okay, the user wants ", "the total…</th", "ink>\n\nYou spent **$42**.");
        visible.Should().Be("\n\nYou spent **$42**.");
        saw.Should().BeTrue();
    }

    [Fact]
    public void Plain_answers_pass_through_including_lone_angle_brackets()
    {
        var (visible, saw) = Stream("Spending was < $50 and ", "a <b>tag</b> stays", " <");
        visible.Should().Be("Spending was < $50 and a <b>tag</b> stays <");
        saw.Should().BeFalse();
    }

    [Fact]
    public void Unfinished_reasoning_is_not_flushed()
    {
        Stream("Hi <think>still going").Visible.Should().Be("Hi ");
    }

    [Fact]
    public void Thinking_tag_variant_and_case()
    {
        Stream("<THINKING>hmm</Thinking>Answer").Visible.Should().Be("Answer");
    }

    [Theory]
    [InlineData("<think>reasoning</think>\n\nThe answer.", "The answer.")]
    [InlineData("reasoning without an opening tag</think>The answer.", "The answer.")]
    [InlineData("Intro <think>aside</think> end.", "Intro  end.")]
    [InlineData("<think>never closed", "")]
    [InlineData("No reasoning here.", "No reasoning here.")]
    public void Strip_cleans_whole_answers(string input, string expected) =>
        ReasoningFilter.Strip(input).Should().Be(expected);
}
