using AiRelay.Application.Permissions.Provider;
using AiRelay.Application.SystemUpdate;
using AiRelay.Application.SystemUpdate.Dtos;
using Leistd.Ddd.Application.Permission;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiRelay.Api.Controllers;

[Authorize]
[Route("api/v1/admin/system")]
public class SystemController(
    ISystemUpdateAppService systemUpdateAppService,
    IHostApplicationLifetime lifetime,
    ILogger<SystemController> logger) : BaseController
{
    [HttpGet("version")]
    [Permission(PermissionConstant.Settings.Default)]
    public SystemVersionOutputDto GetVersion() => systemUpdateAppService.GetVersion();

    [HttpGet("check-updates")]
    [Permission(PermissionConstant.Settings.Default)]
    public Task<SystemUpdateInfoOutputDto> CheckUpdatesAsync(
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default) =>
        systemUpdateAppService.CheckUpdateAsync(force, cancellationToken);

    [HttpPost("update")]
    [Permission(PermissionConstant.Settings.Update)]
    public async Task<SystemUpdateResultDto> PerformUpdateAsync(CancellationToken cancellationToken)
    {
        var result = await systemUpdateAppService.PerformUpdateAsync(cancellationToken);
        if (result.RecreateContainer && !string.IsNullOrWhiteSpace(result.TargetImage))
        {
            ScheduleContainerRecreate(result.TargetImage);
        }
        else if (result.NeedRestart)
        {
            ScheduleRestart();
        }

        return result;
    }

    [HttpPost("restart")]
    [Permission(PermissionConstant.Settings.Update)]
    public SystemRestartResultDto Restart()
    {
        ScheduleRestart();
        return new SystemRestartResultDto { Message = "服务即将重启" };
    }

    private void ScheduleContainerRecreate(string image)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                logger.LogInformation("管理端请求用镜像 {Image} 重建容器", image);
                await systemUpdateAppService.RecreateContainerAsync(image);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "重建容器失败");
            }
        });
    }

    private void ScheduleRestart()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800);
                logger.LogInformation("管理端请求重启，正在退出进程");
                lifetime.StopApplication();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "重启失败");
            }
        });
    }
}
