namespace GProControl.Protocol;

public readonly record struct PerformanceModeMessage(
    uint CommandType,
    ulong Operation,
    ulong Value);
