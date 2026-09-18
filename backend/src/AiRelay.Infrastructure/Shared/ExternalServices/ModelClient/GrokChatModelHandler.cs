using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRelay.Domain.ProviderAccounts.ValueObjects;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Context;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Dto;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Processor;
using AiRelay.Domain.Shared.ExternalServices.ModelProvider;
using AiRelay.Domain.Shared.ExternalServices.ModelProvider.Dto;
using AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.Common;
using AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.Grok;
using AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.OpenAi;
using AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.OpenAiCompatible;
using Microsoft.Extensions.Logging;

namespace AiRelay.Infrastructure.Shared.ExternalServices.ModelClient;

/// <summary>
/// xAI Grok 官方 API：一把 Key 同时转发 Chat Completions 与 Responses。
/// </summary>
public class GrokChatModelHandler(
    ChatModelConnectionOptions options,
    IModelProvider modelProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<GrokChatModelHandler> logger)
    : BaseChatModelHandler(options, httpClientFactory, logger)
{
    public override bool Supports(Provider provider, AuthMethod authMethod) =>
        provider == Provider.Grok && authMethod == AuthMethod.ApiKey;

    protected override IReadOnlyList<IRequestProcessor> GetRequestProcessors(DownRequestContext down, int degradationLevel)
    {
        return
        [
            new GrokUrlRequestProcessor(Options),
            new OpenAiCompatibleHeaderRequestProcessor(Options),
            new ModelIdMappingRequestProcessor(modelProvider, Options.Provider, Options),
            new OpenAiCompatibleModifyBodyRequestProcessor(Options)
        ];
    }

    protected override IReadOnlyList<IResponseProcessor> GetResponseProcessors(UpRequestContext up, DownRequestContext down)
    {
        return
        [
            new OpenAiParseSseResponseProcessor(),
            new UsageAccumulatorResponseProcessor()
        ];
    }

    public override void ExtractModelInfo(DownRequestContext down, Guid apiKeyId)
    {
        if (down.ExtractedProps.TryGetValue("public.model", out var modelId) && !string.IsNullOrWhiteSpace(modelId))
            down.ModelId = modelId;

        if (down.Headers.TryGetValue("session_id", out var sessionIdHeader))
        {
            var sessionId = sessionIdHeader.Trim();
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                down.SessionId = sessionId;
                return;
            }
        }

        if (down.ExtractedProps.TryGetValue("public.fingerprint", out var fingerprint) && !string.IsNullOrWhiteSpace(fingerprint))
            down.SessionId = GenerateSessionHashWithContext(fingerprint, down, apiKeyId);
    }

    public override async Task<IReadOnlyList<ModelOption>?> GetModelsAsync(CancellationToken ct = default)
    {
        var down = new DownRequestContext
        {
            Method = HttpMethod.Get,
            RelativePath = "/v1/models"
        };

        var up = await ProcessRequestContextAsync(down, 0, ct);
        var requestUrl = up.GetFullUrl();

        using var response = await SendCoreRequestAsync(up, down, ct);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Grok 上游模型拉取失败: {StatusCode}, URL={Url}", response.StatusCode, requestUrl);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var models = new List<ModelOption>();

        if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataArray.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idProp))
                    continue;

                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id))
                    models.Add(new ModelOption(id, id));
            }
        }

        if (models.Count == 0)
        {
            Logger.LogWarning("Grok 上游返回 0 模型: URL={Url}, JsonLength={Len}", requestUrl, json.Length);
            return null;
        }

        Logger.LogInformation("Grok 上游拉取成功: {Count} 个模型", models.Count);
        return models;
    }

    public override DownRequestContext CreateChatDownContext(ChatDownContextInput input)
    {
        var messages = new JsonArray();
        var systemMessages = input.Messages
            .Where(message => message.Role == ChatDownContextMessageRole.System && !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => message.Content!.Trim())
            .ToList();

        foreach (var message in input.Messages.Where(message => message.Role != ChatDownContextMessageRole.System))
        {
            var contentParts = new JsonArray();
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                contentParts.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = message.Content
                });
            }

            foreach (var attachment in message.Attachments ?? [])
            {
                var imageUrl = !string.IsNullOrWhiteSpace(attachment.Data)
                    ? $"data:{attachment.MimeType};base64,{attachment.Data}"
                    : attachment.Url;

                if (string.IsNullOrWhiteSpace(imageUrl))
                    continue;

                contentParts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = imageUrl
                    }
                });
            }

            JsonNode contentNode = contentParts.Count switch
            {
                0 => string.Empty,
                1 when contentParts[0]?["type"]?.GetValue<string>() == "text" => contentParts[0]!["text"]!.GetValue<string>(),
                _ => contentParts
            };

            messages.Add(new JsonObject
            {
                ["role"] = message.Role == ChatDownContextMessageRole.Assistant ? "assistant" : "user",
                ["content"] = contentNode
            });
        }

        if (systemMessages.Count > 0)
        {
            messages.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = string.Join("\n\n", systemMessages)
            });
        }

        var json = new JsonObject
        {
            ["model"] = input.ModelId,
            ["messages"] = messages,
            ["stream"] = input.Stream,
            ["max_tokens"] = input.MaxTokens ?? 4096
        };
        var body = json.ToJsonString();

        return new DownRequestContext
        {
            Method = HttpMethod.Post,
            RelativePath = "/v1/chat/completions",
            ModelId = input.ModelId,
            SessionId = input.SessionId,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "application/json"
            },
            RawStream = new MemoryStream(Encoding.UTF8.GetBytes(body)),
            PreloadedBodyPreview = body
        };
    }
}
