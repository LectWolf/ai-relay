using AiRelay.Domain.ProviderAccounts.ValueObjects;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Context;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Dto;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Processor;

namespace AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.OpenAi;

public class OpenAiUrlRequestProcessor(ChatModelConnectionOptions options) : IRequestProcessor
{

    public Task ProcessAsync(DownRequestContext down, UpRequestContext up, CancellationToken ct)
    {
        up.BaseUrl = !string.IsNullOrEmpty(options.BaseUrl)
            ? options.BaseUrl
            : options.AuthMethod == AuthMethod.OAuth ? "https://chatgpt.com" : "https://api.openai.com";

        var downPath = down.RelativePath?.Trim() ?? "";

        if (options.AuthMethod == AuthMethod.OAuth)
        {
            var relativePath = "/backend-api/codex/responses";
            if (downPath.Contains("/responses/", StringComparison.OrdinalIgnoreCase))
            {
                var idx = downPath.IndexOf("/responses/", StringComparison.OrdinalIgnoreCase);
                relativePath += downPath[(idx + "/responses".Length)..];
            }

            up.RelativePath = relativePath;
            up.QueryString = down.QueryString;
            return Task.CompletedTask;
        }

        // API Key：chat completions / models 原样打上游，其余走 Responses
        if (downPath.Contains("/models", StringComparison.OrdinalIgnoreCase))
        {
            up.RelativePath = "/v1/models";
        }
        else if (downPath.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            up.RelativePath = "/v1/chat/completions";
        }
        else
        {
            var relativePath = "/v1/responses";
            if (downPath.Contains("/responses/", StringComparison.OrdinalIgnoreCase))
            {
                var idx = downPath.IndexOf("/responses/", StringComparison.OrdinalIgnoreCase);
                relativePath += downPath[(idx + "/responses".Length)..];
            }

            up.RelativePath = relativePath;
        }

        up.QueryString = down.QueryString;
        return Task.CompletedTask;
    }
}
