using System.Text.RegularExpressions;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Context;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Dto;
using AiRelay.Domain.Shared.ExternalServices.ModelClient.Processor;

namespace AiRelay.Infrastructure.Shared.ExternalServices.ModelClient.Processor.DeepSeek;

/// <summary>
/// DeepSeek URL 处理器：Chat Completions / Responses 打官方 OpenAI 兼容入口，
/// Anthropic Messages 打 /anthropic；官方路径不带 /v1。
/// </summary>
public class DeepSeekUrlRequestProcessor(ChatModelConnectionOptions options) : IRequestProcessor
{
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultAnthropicBaseUrl = "https://api.deepseek.com/anthropic";

    private static readonly Regex VersionSuffixRegex = new(@"/v\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task ProcessAsync(DownRequestContext down, UpRequestContext up, CancellationToken ct)
    {
        var configured = NormalizeBase(options.BaseUrl);
        var relativePath = NormalizeRelativePath(down.RelativePath);

        if (IsAnthropicRoute(relativePath))
        {
            up.BaseUrl = ResolveAnthropicBase(configured);
            up.RelativePath = relativePath;
        }
        else
        {
            up.BaseUrl = string.IsNullOrEmpty(configured) ? DefaultBaseUrl : configured;
            up.RelativePath = MapOpenAiPath(relativePath);
        }

        up.QueryString = down.QueryString;
        return Task.CompletedTask;
    }

    public static bool IsAnthropicRoute(string? relativePath)
    {
        var path = NormalizeRelativePath(relativePath);
        return path.Contains("/v1/messages", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/messages", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAnthropicBase(string? configured)
    {
        if (string.IsNullOrEmpty(configured))
            return DefaultAnthropicBaseUrl;

        if (configured.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
            return configured;

        return VersionSuffixRegex.Replace(configured, "") + "/anthropic";
    }

    private static string MapOpenAiPath(string relativePath)
    {
        var path = relativePath.TrimStart('/');

        // 官方文档是 /chat/completions、/responses、/models。
        // BaseUrl 带 /v1 时同样去掉路径上的 v1，避免 /v1/v1/...
        if (path.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
            path = path[3..];

        return "/" + path;
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        var path = relativePath?.Trim() ?? "";
        if (string.IsNullOrEmpty(path))
            return "/";
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static string? NormalizeBase(string? baseUrl)
    {
        var value = baseUrl?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.TrimEnd('/');
    }
}
