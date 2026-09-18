using AiRelay.Application.SystemUpdate.Dtos;
using Leistd.Ddd.Application.Contracts.AppService;

namespace AiRelay.Application.SystemUpdate;

public interface ISystemUpdateAppService : IAppService
{
    SystemVersionOutputDto GetVersion();

    Task<SystemUpdateInfoOutputDto> CheckUpdateAsync(bool force, CancellationToken cancellationToken = default);

    Task<SystemUpdateResultDto> PerformUpdateAsync(CancellationToken cancellationToken = default);

    Task RecreateContainerAsync(string image, CancellationToken cancellationToken = default);
}
