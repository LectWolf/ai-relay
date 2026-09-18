using AiRelay.Domain.Shared.ExternalServices.ModelClient.Constants;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Context;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Dto;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Processor;

namespace AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.DeepSeek;

/// <summary>
/// DeepSeek Header：OpenAI 协议走 Bearer，Anthropic 协议走 x-api-key。
/// </summary>
public class DeepSeekHeaderRequestProcessor(ChatModelConnectionOptions options) : IRequestProcessor
{
    private static readonly HashSet<string> AnthropicPassthroughHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept",
        "accept-language",
        "content-type",
        "user-agent",
        "anthropic-version",
        "anthropic-beta"
    };

    public Task ProcessAsync(DownRequestContext down, UpRequestContext up, CancellationToken ct)
    {
        if (DeepSeekUrlRequestProcessor.IsAnthropicRoute(down.RelativePath))
        {
            ApplyAnthropicHeaders(down, up);
        }
        else
        {
            ApplyOpenAiHeaders(down, up);
        }

        return Task.CompletedTask;
    }

    private void ApplyOpenAiHeaders(DownRequestContext down, UpRequestContext up)
    {
        foreach (var kvp in down.Headers)
        {
            if (OpenAiCompatibleMimicDefaults.Headers.TryGetValue(kvp.Key, out var config) &&
                config.AllowPassthrough &&
                !string.IsNullOrEmpty(kvp.Value))
            {
                up.Headers[kvp.Key] = kvp.Value;
            }
        }

        up.Headers.Remove("x-api-key");
        up.Headers.Remove("x-goog-api-key");
        up.Headers.Remove("cookie");
        up.Headers["Authorization"] = $"Bearer {options.Credential}";

        if (!options.ShouldMimicOfficialClient)
            return;

        foreach (var (key, (_, defaultValue, forceOverride)) in OpenAiCompatibleMimicDefaults.Headers)
        {
            if (defaultValue == null)
                continue;

            if (forceOverride || !up.Headers.ContainsKey(key))
                up.Headers[key] = defaultValue;
        }
    }

    private void ApplyAnthropicHeaders(DownRequestContext down, UpRequestContext up)
    {
        foreach (var kvp in down.Headers)
        {
            if (AnthropicPassthroughHeaders.Contains(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                up.Headers[kvp.Key] = kvp.Value;
        }

        up.Headers.Remove("Authorization");
        up.Headers.Remove("cookie");
        up.Headers["x-api-key"] = options.Credential;

        if (!up.Headers.ContainsKey("anthropic-version"))
            up.Headers["anthropic-version"] = "2023-06-01";
    }
}
