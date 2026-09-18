namespace AiRelay.Application.SystemUpdate.Options;

public class SystemUpdateOptions
{
    public const string SectionName = "SystemUpdate";

    public string Repository { get; set; } = "LectWolf/ai-relay";

    public string ImageName { get; set; } = "ghcr.io/lectwolf/ai-relay";

    public string DockerSocket { get; set; } = "/var/run/docker.sock";

    public string? InstallDirectory { get; set; }

    public string[] PreserveFiles { get; set; } =
    [
        "appsettings.Production.json",
        "appsettings.Development.json",
        "appsettings.Local.json",
        ".env"
    ];
}
