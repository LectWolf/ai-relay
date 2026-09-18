using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRelay.Application.SystemUpdate.Dtos;
using AiRelay.Application.SystemUpdate.Options;
using Leistd.Exception.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiRelay.Application.SystemUpdate;

public class SystemUpdateAppService(
    HttpClient httpClient,
    IOptions<SystemUpdateOptions> options,
    ILogger<SystemUpdateAppService> logger) : ISystemUpdateAppService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] AllowedDownloadHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    private const long MaxDownloadBytes = 500L * 1024 * 1024;
    private readonly SystemUpdateOptions _options = options.Value;

    public SystemVersionOutputDto GetVersion()
    {
        var runtime = DetectRuntime();
        var version = ReadCurrentVersion();
        return new SystemVersionOutputDto
        {
            Version = version,
            CustomVersion = version,
            UpstreamVersion = ReadUpstreamVersion(),
            Runtime = runtime.Kind,
            UpdateSupported = runtime.UpdateSupported
        };
    }

    public async Task<SystemUpdateInfoOutputDto> CheckUpdateAsync(bool force, CancellationToken cancellationToken = default)
    {
        _ = force;
        var runtime = DetectRuntime();
        var current = ReadCurrentVersion();
        var upstream = ReadUpstreamVersion();

        try
        {
            var release = await FetchLatestCustomReleaseAsync(cancellationToken);
            if (release is null)
            {
                return new SystemUpdateInfoOutputDto
                {
                    CurrentVersion = current,
                    LatestVersion = current,
                    UpstreamVersion = upstream,
                    Runtime = runtime.Kind,
                    UpdateSupported = runtime.UpdateSupported,
                    Warning = "还没有 custom-v* Release，先打版本标签发版后再检查。"
                };
            }

            var latest = StripTag(release.TagName);
            return new SystemUpdateInfoOutputDto
            {
                CurrentVersion = current,
                LatestVersion = latest,
                UpstreamVersion = upstream,
                HasUpdate = CompareVersion(latest, current) > 0,
                Runtime = runtime.Kind,
                UpdateSupported = runtime.UpdateSupported,
                Warning = runtime.Warning,
                ReleaseInfo = new ReleaseInfoDto
                {
                    Name = release.Name ?? release.TagName,
                    Body = release.Body ?? "",
                    PublishedAt = release.PublishedAt ?? "",
                    HtmlUrl = release.HtmlUrl ?? ""
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "检查更新失败");
            return new SystemUpdateInfoOutputDto
            {
                CurrentVersion = current,
                LatestVersion = current,
                UpstreamVersion = upstream,
                Runtime = runtime.Kind,
                UpdateSupported = runtime.UpdateSupported,
                Warning = ex.Message
            };
        }
    }

    public async Task<SystemUpdateResultDto> PerformUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
        {
            throw new BadRequestException("已有更新任务在执行");
        }

        try
        {
            var runtime = DetectRuntime();
            if (!runtime.UpdateSupported)
            {
                throw new BadRequestException(runtime.Warning ?? "当前运行方式不支持原地更新");
            }

            var info = await CheckUpdateAsync(true, cancellationToken);
            if (!info.HasUpdate)
            {
                return new SystemUpdateResultDto
                {
                    Message = "当前已是最新版本",
                    AlreadyUpToDate = true,
                    Runtime = runtime.Kind
                };
            }

            if (runtime.Kind == "docker")
            {
                var image = $"{_options.ImageName.Trim().TrimEnd('/')}:{info.LatestVersion}";
                await PullDockerImageAsync(info.LatestVersion, cancellationToken);
                return new SystemUpdateResultDto
                {
                    Message = "镜像已拉取，即将重建容器切换到新版本。",
                    RecreateContainer = true,
                    TargetImage = image,
                    Runtime = runtime.Kind
                };
            }

            await ApplyProcessUpdateAsync(info.LatestVersion, cancellationToken);
            return new SystemUpdateResultDto
            {
                Message = "更新包已替换，请立即重启服务。",
                NeedRestart = true,
                Runtime = runtime.Kind
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RecreateContainerAsync(string image, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new BadRequestException("容器原地更新仅支持 Linux");
        }

        using var docker = new DockerEngineClient(_options.DockerSocket, logger);
        await docker.RecreateSelfAsync(image, cancellationToken);
    }

    private async Task PullDockerImageAsync(string version, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new BadRequestException("容器原地更新仅支持 Linux");
        }

        var image = _options.ImageName.Trim().TrimEnd('/');
        using var docker = new DockerEngineClient(_options.DockerSocket, logger);
        logger.LogInformation("拉取镜像 {Image}:{Version}", image, version);
        await docker.PullImageAsync(image, version, cancellationToken);
    }

    private async Task ApplyProcessUpdateAsync(string version, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new BadRequestException("进程原地更新仅支持 Linux 生产环境");
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new BadRequestException("当前只有 linux-x64 更新包，ARM 请用容器镜像");
        }

        var release = await FetchLatestCustomReleaseAsync(cancellationToken)
                      ?? throw new BadRequestException("没有可用的 GitHub Release");
        var assetName = $"ai-relay_{version}_linux-x64.tar.gz";
        var asset = release.Assets.FirstOrDefault(a =>
                        string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new BadRequestException($"Release 里没有 {assetName}");

        ValidateDownloadUrl(asset.BrowserDownloadUrl);

        var installDir = GetInstallDirectory();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ai-relay-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, assetName);
        var extractDir = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(extractDir);

        try
        {
            await DownloadAsync(asset.BrowserDownloadUrl, archivePath, cancellationToken);
            ExtractTarGz(archivePath, extractDir);
            ReplaceInstallDirectory(extractDir, installDir);
            await File.WriteAllTextAsync(Path.Combine(installDir, "VERSION"), version + Environment.NewLine, cancellationToken);
            logger.LogInformation("已将安装目录更新到 {Version}: {Dir}", version, installDir);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignore */ }
        }
    }

    private async Task<GitHubRelease?> FetchLatestCustomReleaseAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{_options.Repository}/releases?per_page=30";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GitHub API {(int)response.StatusCode}: {body}");
        }

        var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(JsonOptions, cancellationToken)
                       ?? [];

        return releases
            .Where(r => !r.Draft && !r.Prerelease && r.TagName.StartsWith("custom-v", StringComparison.Ordinal))
            .Select(r => (Release: r, Version: StripTag(r.TagName)))
            .Where(x => Version.TryParse(NormalizeVersion(x.Version), out _))
            .OrderByDescending(x => Version.Parse(NormalizeVersion(x.Version)))
            .Select(x => x.Release)
            .FirstOrDefault();
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > MaxDownloadBytes)
        {
            throw new BadRequestException("更新包超过 500MB 上限");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw new BadRequestException("更新包超过 500MB 上限");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ExtractTarGz(string archivePath, string destDir)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory)
            {
                continue;
            }

            var relative = entry.Name.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains("..", StringComparison.Ordinal))
            {
                throw new BadRequestException("更新包包含非法路径");
            }

            var target = Path.GetFullPath(Path.Combine(destDir, relative));
            if (!target.StartsWith(Path.GetFullPath(destDir), StringComparison.Ordinal))
            {
                throw new BadRequestException("更新包包含非法路径");
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private void ReplaceInstallDirectory(string sourceDir, string installDir)
    {
        var preserve = new HashSet<string>(_options.PreserveFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (preserve.Contains(relative) && File.Exists(Path.Combine(installDir, relative)))
            {
                continue;
            }

            var dest = Path.Combine(installDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private RuntimeInfo DetectRuntime()
    {
        var inDocker = File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
        if (!inDocker)
        {
            return new RuntimeInfo("process", true, null);
        }

        if (File.Exists(_options.DockerSocket))
        {
            return new RuntimeInfo("docker", true, null);
        }

        return new RuntimeInfo(
            "docker",
            false,
            "当前以容器运行，但没有挂载 docker.sock。把 /var/run/docker.sock 挂进容器，或改用 systemd 跑发布包。");
    }

    private string GetInstallDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.InstallDirectory))
        {
            return _options.InstallDirectory;
        }

        return AppContext.BaseDirectory;
    }

    private static string ReadCurrentVersion()
    {
        foreach (var path in CandidateVersionFiles("VERSION"))
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return typeof(SystemUpdateAppService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    private static string ReadUpstreamVersion()
    {
        foreach (var path in CandidateVersionFiles("UPSTREAM_VERSION"))
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return "";
    }

    private static IEnumerable<string> CandidateVersionFiles(string name)
    {
        yield return Path.Combine(AppContext.BaseDirectory, name);
        yield return Path.Combine(Directory.GetCurrentDirectory(), name);
    }

    private static string StripTag(string tag) =>
        tag.StartsWith("custom-v", StringComparison.OrdinalIgnoreCase) ? tag["custom-v".Length..] : tag.TrimStart('v', 'V');

    private static string NormalizeVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0.0",
            2 => $"{parts[0]}.{parts[1]}.0.0",
            3 => $"{parts[0]}.{parts[1]}.{parts[2]}.0",
            _ => version
        };
    }

    private static int CompareVersion(string left, string right)
    {
        if (!Version.TryParse(NormalizeVersion(left), out var l))
        {
            return 0;
        }

        if (!Version.TryParse(NormalizeVersion(right), out var r))
        {
            return 1;
        }

        return l.CompareTo(r);
    }

    private static void ValidateDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new BadRequestException("更新包下载地址无效");
        }

        if (!AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new BadRequestException($"拒绝从非 GitHub 域名下载：{uri.Host}");
        }
    }

    private sealed record RuntimeInfo(string Kind, bool UpdateSupported, string? Warning);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public string? PublishedAt { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
