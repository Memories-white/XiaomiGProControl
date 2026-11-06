namespace GProControl.Protocol;

public readonly record struct PerformanceModeDefinition(
    string Name,
    int ServiceValue,
    int EcValue);

public static class PerformanceModeCatalog
{
    public static IReadOnlyList<PerformanceModeDefinition> All { get; } =
    [
        // MiDeviceService operation 1 uses its own enum. It is not the same
        // value accepted by XiaomiGCommandCenter's set_performance_mode bridge.
        new("静谧", 0, 2),
        new("均衡", 1, 1),
        new("极速", 2, 3),
        new("狂暴", 3, 4)
    ];

    public static int IndexOfEc(int ecValue)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index].EcValue == ecValue)
            {
                return index;
            }
        }

        return -1;
    }

    public static PerformanceModeDefinition? FindByServiceValue(int serviceValue)
    {
        foreach (var mode in All)
        {
            if (mode.ServiceValue == serviceValue)
            {
                return mode;
            }
        }

        return null;
    }
}
