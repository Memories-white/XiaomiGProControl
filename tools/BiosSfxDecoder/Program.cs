using System.Security.Cryptography;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: BiosSfxDecoder <protected-sfx.exe> <decoded.7z>");
    return 2;
}

var inputPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var source = File.ReadAllBytes(inputPath);
var marker = Encoding.ASCII.GetBytes(";!@InstallEnd@!");
var markerOffset = Find(source, marker);
if (markerOffset < 0)
{
    Console.Error.WriteLine("SFX end marker was not found.");
    return 3;
}

var payloadOffset = markerOffset + marker.Length;
if (payloadOffset < source.Length && source[payloadOffset] == (byte)'\r')
{
    payloadOffset++;
}
if (payloadOffset < source.Length && source[payloadOffset] == (byte)'\n')
{
    payloadOffset++;
}

var decoded = new byte[source.Length - payloadOffset];
for (var i = 0; i < decoded.Length; i++)
{
    var value = source[payloadOffset + i];
    decoded[i] = (byte)((value >> 1) | (value << 7));
}

byte[] sevenZipMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
if (!decoded.AsSpan().StartsWith(sevenZipMagic))
{
    Console.Error.WriteLine($"Decoded payload does not begin with the 7z signature: {Convert.ToHexString(decoded.AsSpan(0, Math.Min(6, decoded.Length)))}");
    return 4;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllBytes(outputPath, decoded);

Console.WriteLine($"Input: {inputPath}");
Console.WriteLine($"Input SHA-256: {Convert.ToHexString(SHA256.HashData(source))}");
Console.WriteLine($"Marker offset: 0x{markerOffset:X}");
Console.WriteLine($"Payload offset: 0x{payloadOffset:X}");
Console.WriteLine($"Decoded bytes: {decoded.Length}");
Console.WriteLine($"Decoded SHA-256: {Convert.ToHexString(SHA256.HashData(decoded))}");
Console.WriteLine($"Output: {outputPath}");
return 0;

static int Find(ReadOnlySpan<byte> source, ReadOnlySpan<byte> pattern)
{
    for (var i = 0; i <= source.Length - pattern.Length; i++)
    {
        if (source.Slice(i, pattern.Length).SequenceEqual(pattern))
        {
            return i;
        }
    }
    return -1;
}
