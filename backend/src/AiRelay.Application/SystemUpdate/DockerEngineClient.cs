using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AiRelay.Application.SystemUpdate;

internal sealed class DockerEngineClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public DockerEngineClient(string socketPath, ILogger logger)
    {
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public async Task PullImageAsync(string image, string tag, CancellationToken cancellationToken)
    {
        var url = $"/v1.41/images/create?fromImage={Uri.EscapeDataString(image)}&tag={Uri.EscapeDataString(tag)}";
        using var response = await _http.PostAsync(url, null, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var errors = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.String)
                {
                    var message = error.GetString();
                    if (!string.IsNullOrWhiteSpace(message))
                        errors.Add(message);
                }
            }
            catch (JsonException)
            {
                // Docker 偶发非 JSON 进度行，忽略
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"拉取镜像失败 ({(int)response.StatusCode}): {(errors.Count > 0 ? string.Join("; ", errors) : "未知错误")}");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"拉取镜像失败: {string.Join("; ", errors)}");
        }
    }

    public async Task RecreateSelfAsync(string newImage, CancellationToken cancellationToken)
    {
        var containerId = Environment.MachineName;
        var inspect = await GetJsonAsync($"/v1.41/containers/{containerId}/json", cancellationToken)
                      ?? throw new InvalidOperationException($"找不到当前容器 {containerId}，请确认已挂载 docker.sock");

        var oldName = inspect["Name"]?.GetValue<string>()?.Trim('/') ?? "ai-relay";
        var config = inspect["Config"] as JsonObject
                     ?? throw new InvalidOperationException("容器 Config 缺失");
        var hostConfig = inspect["HostConfig"] as JsonObject ?? [];
        var networks = inspect["NetworkSettings"]?["Networks"] as JsonObject;

        if (hostConfig["NetworkMode"] is not null && networks is { Count: > 0 })
        {
            hostConfig.Remove("NetworkMode");
        }

        var createBody = new JsonObject
        {
            ["Image"] = newImage,
            ["Env"] = config["Env"]?.DeepClone(),
            ["Cmd"] = config["Cmd"]?.DeepClone(),
            ["Entrypoint"] = config["Entrypoint"]?.DeepClone(),
            ["WorkingDir"] = config["WorkingDir"]?.DeepClone(),
            ["Labels"] = config["Labels"]?.DeepClone(),
            ["ExposedPorts"] = config["ExposedPorts"]?.DeepClone(),
            ["HostConfig"] = hostConfig.DeepClone(),
        };
        if (networks is { Count: > 0 })
        {
            createBody["NetworkingConfig"] = new JsonObject
            {
                ["EndpointsConfig"] = networks.DeepClone()
            };
        }

        var backupName = $"{oldName}-old-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await PostAsync($"/v1.41/containers/{containerId}/rename?name={Uri.EscapeDataString(backupName)}", null, cancellationToken);

        try
        {
            var created = await PostJsonAsync(
                $"/v1.41/containers/create?name={Uri.EscapeDataString(oldName)}",
                createBody,
                cancellationToken);
            var newId = created?["Id"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("创建新容器未返回 Id");

            await PostAsync($"/v1.41/containers/{newId}/start", null, cancellationToken);
            await PostAsync($"/v1.41/containers/{containerId}/stop?t=8", null, cancellationToken);
            await DeleteAsync($"/v1.41/containers/{containerId}?force=true", cancellationToken);
            _logger.LogInformation("容器已用镜像 {Image} 重建：{Old} -> {New}", newImage, containerId, newId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重建容器失败，尝试把旧容器改回 {Name}", oldName);
            try
            {
                await PostAsync(
                    $"/v1.41/containers/{containerId}/rename?name={Uri.EscapeDataString(oldName)}",
                    null,
                    CancellationToken.None);
                await PostAsync($"/v1.41/containers/{containerId}/start", null, CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "回滚旧容器名称失败");
            }

            throw;
        }
    }

    private async Task<JsonObject?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Docker GET {url} 失败 ({(int)response.StatusCode}): {Trim(body)}");
        }

        return JsonSerializer.Deserialize<JsonObject>(body, JsonOptions);
    }

    private async Task<JsonObject?> PostJsonAsync(string url, JsonNode body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Docker POST {url} 失败 ({(int)response.StatusCode}): {Trim(text)}");
        }

        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<JsonObject>(text, JsonOptions);
    }

    private async Task PostAsync(string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(url, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Docker POST {url} 失败 ({(int)response.StatusCode}): {Trim(text)}");
        }
    }

    private async Task DeleteAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Docker DELETE {url} 失败 ({(int)response.StatusCode}): {Trim(text)}");
        }
    }

    private static string Trim(string text) =>
        text.Length <= 400 ? text : text[..400];

    public void Dispose() => _http.Dispose();
}
