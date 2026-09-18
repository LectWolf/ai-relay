using System.Text.RegularExpressions;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Context;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Dto;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Processor;

namespace AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.Grok;

/// <summary>
/// xAI Grok URL：默认 https://api.x.ai，保留官方 /v1 路径（Chat Completions / Responses）。
/// </summary>
public class GrokUrlRequestProcessor(ChatModelConnectionOptions options) : IRequestProcessor
{
    public const string DefaultBaseUrl = "https://api.x.ai";

    private static readonly Regex VersionSuffixRegex = new(@"/v\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task ProcessAsync(DownRequestContext down, UpRequestContext up, CancellationToken ct)
    {
        var baseUrl = NormalizeBase(options.BaseUrl) ?? DefaultBaseUrl;
        var relPath = (down.RelativePath ?? "").TrimStart('/');

        if (VersionSuffixRegex.IsMatch(baseUrl) && relPath.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
            relPath = relPath[3..];

        up.BaseUrl = baseUrl;
        up.RelativePath = "/" + relPath;
        up.QueryString = down.QueryString;
        return Task.CompletedTask;
    }

    private static string? NormalizeBase(string? baseUrl)
    {
        var value = baseUrl?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.TrimEnd('/');
    }
}
