using AiRelay.Domain.ProviderAccounts.ValueObjects;
using Xunit;

namespace AiRelay.Tests.ProviderAccounts;

public class RouteProfileRegistryTests
{
    [Fact]
    public void DeepSeek_ApiKey_Supports_Three_Public_Protocols()
    {
        var combo = (Provider.DeepSeek, AuthMethod.ApiKey);

        Assert.Contains(combo, RouteProfileRegistry.Profiles[RouteProfile.ChatCompletions].SupportedCombinations);
        Assert.Contains(combo, RouteProfileRegistry.Profiles[RouteProfile.OpenAiResponses].SupportedCombinations);
        Assert.Contains(combo, RouteProfileRegistry.Profiles[RouteProfile.ClaudeMessages].SupportedCombinations);
        Assert.DoesNotContain(combo, RouteProfileRegistry.Profiles[RouteProfile.OpenAiCodex].SupportedCombinations);
    }

    [Fact]
    public void Grok_ApiKey_Supports_ChatCompletions_And_Responses()
    {
        var combo = (Provider.Grok, AuthMethod.ApiKey);

        Assert.Contains(combo, RouteProfileRegistry.Profiles[RouteProfile.ChatCompletions].SupportedCombinations);
        Assert.Contains(combo, RouteProfileRegistry.Profiles[RouteProfile.OpenAiResponses].SupportedCombinations);
        Assert.DoesNotContain(combo, RouteProfileRegistry.Profiles[RouteProfile.ClaudeMessages].SupportedCombinations);
        Assert.DoesNotContain(combo, RouteProfileRegistry.Profiles[RouteProfile.OpenAiCodex].SupportedCombinations);
    }
}
