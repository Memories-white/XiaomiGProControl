namespace GProControl.Protocol;

public readonly record struct MiServiceEnvelope(
    byte Kind,
    string ClientId,
    ReadOnlyMemory<byte> Payload)
{
    public bool IsRegistration => Kind == 1;
}
