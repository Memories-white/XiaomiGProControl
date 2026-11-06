namespace GProControl.Protocol;

public readonly record struct GameBoxMessage(
    uint CommandType,
    uint BusinessType,
    string Data,
    int Status);

public readonly record struct RawGameBoxMessage(
    uint CommandType,
    uint BusinessType,
    ReadOnlyMemory<byte> Data,
    int Status);
