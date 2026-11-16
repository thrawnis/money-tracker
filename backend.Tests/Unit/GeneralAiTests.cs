using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class GeneralAiTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Nothing_set_means_no_assistant_and_receipts_use_bundled_ollama()
    {
        var c = Config();
        GeneralAi.Resolve(c).Endpoint.Should().BeNull();
        GeneralAi.ReceiptOverrides(c).Should().Equal(new Dictionary<string, string?> { ["Receipt:Provider"] = "ollama" });
    }

    [Fact]
    public void Receipts_inherit_the_ai_settings_when_receipt_provider_is_blank()
    {
        var c = Config(("Ai:Provider", "openai"), ("Ai:ApiKey", "sk-1"), ("Ai:Model", "gpt-4o"), ("Receipt:Provider", ""));
        GeneralAi.ReceiptOverrides(c).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Receipt:Provider"] = "openai", ["Receipt:OpenAi:ApiKey"] = "sk-1", ["Receipt:OpenAi:Model"] = "gpt-4o",
        });
        GeneralAi.Resolve(c).Endpoint.Should().Be(new GeneralAi.Endpoint("openai", "https://api.openai.com/v1", "sk-1", "gpt-4o"));
    }

    [Fact]
    public void Receipt_settings_win_and_ollama_urls_drop_v1()
    {
        var c = Config(("Ai:Provider", "anthropic"), ("Ai:ApiKey", "sk-ant"),
            ("Receipt:Provider", "ollama"), ("Receipt:BaseUrl", "http://h:11434/v1"), ("Receipt:Model", "llava"));
        GeneralAi.ReceiptOverrides(c).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Receipt:Provider"] = "ollama", ["Receipt:Ollama:BaseUrl"] = "http://h:11434", ["Receipt:Ollama:Model"] = "llava",
        });
    }

    [Fact]
    public void Explicit_receipt_provider_alone_keeps_the_older_per_provider_keys()
    {
        var c = Config(("Receipt:Provider", "ollama"));
        GeneralAi.ReceiptOverrides(c).Should().Equal(new Dictionary<string, string?> { ["Receipt:Provider"] = "ollama" });
    }

    [Fact]
    public void Ollama_assistant_needs_a_model_and_uses_the_v1_api()
    {
        GeneralAi.Resolve(Config(("Ai:Provider", "ollama"))).Problem.Should().Contain("AI_MODEL");
        GeneralAi.Resolve(Config(("Ai:Provider", "ollama"), ("Ai:Model", "qwen2.5:14b"))).Endpoint!.BaseUrl
            .Should().Be("http://ollama:11434/v1");
    }

    [Fact]
    public void A_base_url_alone_means_an_openai_compatible_server_without_a_key()
    {
        var ep = GeneralAi.Resolve(Config(("Ai:BaseUrl", "http://lm:1234/v1/"), ("Ai:Model", "local"))).Endpoint;
        ep.Should().Be(new GeneralAi.Endpoint("openai", "http://lm:1234/v1", null, "local"));
    }

    [Fact]
    public void Hosted_providers_need_a_key()
    {
        GeneralAi.Resolve(Config(("Ai:Provider", "openai"))).Problem.Should().Contain("AI_API_KEY");
        GeneralAi.Resolve(Config(("Ai:Provider", "gemini"))).Problem.Should().Contain("AI_API_KEY");
    }
}
