namespace AiRelay.Application.SystemUpdate.Dtos;

public class SystemVersionOutputDto
{
    public string Version { get; init; } = "";
    public string CustomVersion { get; init; } = "";
    public string UpstreamVersion { get; init; } = "";
    public string Runtime { get; init; } = "process";
    public bool UpdateSupported { get; init; }
}

public class ReleaseInfoDto
{
    public string Name { get; init; } = "";
    public string Body { get; init; } = "";
    public string PublishedAt { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
}

public class SystemUpdateInfoOutputDto
{
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string UpstreamVersion { get; init; } = "";
    public bool HasUpdate { get; init; }
    public string Runtime { get; init; } = "process";
    public bool UpdateSupported { get; init; }
    public string? Warning { get; init; }
    public ReleaseInfoDto? ReleaseInfo { get; init; }
}

public class SystemUpdateResultDto
{
    public string Message { get; init; } = "";
    public bool NeedRestart { get; init; }
    public bool RecreateContainer { get; init; }
    public string? TargetImage { get; init; }
    public bool AlreadyUpToDate { get; init; }
    public string Runtime { get; init; } = "process";
}

public class SystemRestartResultDto
{
    public string Message { get; init; } = "";
}
