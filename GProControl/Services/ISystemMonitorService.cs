using GProControl.Models;

namespace GProControl.Services;

public interface ISystemMonitorService
{
    SystemSnapshot Capture();
}
