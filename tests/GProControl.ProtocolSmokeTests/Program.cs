using GProControl.Protocol;

var frame = new byte[]
{
    2, 0, 3, 0, (byte)'a', (byte)'p', (byte)'p',
    1,             // GamingBoxCmd command type
    0x08, 0x14,    // field 1: business_type = 20
    0x12, 0x02, (byte)'{', (byte)'}', // field 2: data = "{}"
    0x18, 0,       // field 3: status = 0
    0x25, 1, 2, 3, 4 // unknown fixed32 field must be skipped safely
};

Require(MiServiceProtocolReader.TryReadEnvelope(frame, out var envelope, out var envelopeError), envelopeError);
Require(envelope.Kind == 2, "Envelope kind mismatch.");
Require(envelope.ClientId == "app", "Client identifier mismatch.");

Require(MiServiceProtocolReader.TryReadGameBox(envelope.Payload.Span, out var message, out var messageError), messageError);
Require(message.CommandType == 1, "Command type mismatch.");
Require(message.BusinessType == 20, "Business type mismatch.");
Require(message.Data == "{}", "Payload mismatch.");
Require(message.Status == 0, "Status mismatch.");

Require(!MiServiceProtocolReader.TryReadEnvelope(new byte[] { 1, 0, 8, 0 }, out _, out _),
    "Truncated client identifier should be rejected.");
Require(!MiServiceProtocolReader.TryReadGameBox(new byte[] { 1, 0x12, 4, 1 }, out _, out _),
    "Truncated length-delimited field should be rejected.");

var performancePayload = new byte[] { 12, 0x08, 1, 0x10, 3 };
Require(MiServiceProtocolReader.TryReadPerformanceMode(
    performancePayload, out var performance, out var performanceError), performanceError);
Require(performance.CommandType == 12, "Performance command type mismatch.");
Require(performance.Operation == 1, "Performance operation mismatch.");
Require(performance.Value == 3, "Performance value mismatch.");

var encodedPerformance = MiServiceProtocolWriter.WritePerformanceMode(1, 4);
Require(MiServiceProtocolReader.TryReadPerformanceMode(
    encodedPerformance, out var encodedMode, out var encodedModeError), encodedModeError);
Require(encodedMode.Operation == 1, "Encoded performance operation mismatch.");
Require(encodedMode.Value == 4, "Encoded performance value mismatch.");

var encodedGameBox = MiServiceProtocolWriter.WriteGameBox(30, "1");
Require(MiServiceProtocolReader.TryReadGameBox(
    encodedGameBox, out var encodedBusiness, out var encodedBusinessError), encodedBusinessError);
Require(encodedBusiness.BusinessType == 30, "Encoded GameBox business type mismatch.");
Require(encodedBusiness.Data == "1", "Encoded GameBox data mismatch.");
Require(encodedBusiness.Status == 0, "Encoded GameBox status mismatch.");

var encodedFrame = MiServiceProtocolWriter.WriteCommand("GProControl-test", encodedPerformance);
Require(MiServiceProtocolReader.TryReadEnvelope(
    encodedFrame, out var encodedEnvelope, out var encodedEnvelopeError), encodedEnvelopeError);
Require(encodedEnvelope.Kind == MiServiceProtocolWriter.CommandFrameKind, "Encoded frame kind mismatch.");
Require(encodedEnvelope.ClientId == "GProControl-test", "Encoded frame client identifier mismatch.");
Require(encodedEnvelope.Payload.Span.SequenceEqual(encodedPerformance), "Encoded frame payload mismatch.");

var registration = MiServiceProtocolWriter.WriteRegistration("GProControl-test");
Require(MiServiceProtocolReader.TryReadEnvelope(
    registration, out var registrationEnvelope, out var registrationError), registrationError);
Require(registrationEnvelope.IsRegistration, "Registration frame kind mismatch.");
Require(registrationEnvelope.Payload.IsEmpty, "Registration frame should not contain a command payload.");

var expectedModes = new[]
{
    new PerformanceModeDefinition("静谧", 0, 2),
    new PerformanceModeDefinition("均衡", 1, 1),
    new PerformanceModeDefinition("极速", 2, 3),
    new PerformanceModeDefinition("狂暴", 3, 4)
};
Require(PerformanceModeCatalog.All.SequenceEqual(expectedModes), "Performance mode catalog mapping mismatch.");
foreach (var modeDefinition in expectedModes)
{
    var payload = MiServiceProtocolWriter.WritePerformanceMode(1, (ulong)modeDefinition.ServiceValue);
    Require(MiServiceProtocolReader.TryReadPerformanceMode(payload, out var decoded, out var decodeError), decodeError);
    Require(decoded.Operation == 1, $"{modeDefinition.Name} operation mismatch.");
    Require(decoded.Value == (ulong)modeDefinition.ServiceValue, $"{modeDefinition.Name} service value mismatch.");
    Require(PerformanceModeCatalog.IndexOfEc(modeDefinition.EcValue) >= 0, $"{modeDefinition.Name} EC mapping missing.");
    Require(PerformanceModeCatalog.FindByServiceValue(modeDefinition.ServiceValue) == modeDefinition,
        $"{modeDefinition.Name} service-value lookup mismatch.");
}

var firmwarePerformance = MiCommonInterfaceCodec.WritePerformanceMode(4);
Require(firmwarePerformance.Length == 32, "MICommonInterface input must be 32 bytes.");
Require(firmwarePerformance.AsSpan(0, 8).SequenceEqual(new byte[] { 0x00, 0xFB, 0x00, 0x08, 0x04, 0x00, 0x00, 0x00 }),
    "Firmware performance payload mismatch.");

var thermalFanRead = MiCommonInterfaceCodec.ReadThermalFanStatus();
Require(thermalFanRead.Length == 32, "Thermal/fan request must be 32 bytes.");
Require(thermalFanRead.AsSpan(0, 6).SequenceEqual(new byte[] { 0x00, 0xFA, 0x00, 0x09, 0x00, 0x00 }),
    "Thermal/fan read payload mismatch.");

var fnEnabled = MiCommonInterfaceCodec.WriteDeviceSwitch(MiDeviceSwitch.FnKey, true);
Require(fnEnabled.AsSpan(0, 10).SequenceEqual(new byte[] { 0x00, 0xFB, 0x00, 0x0A, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 }),
    "Fn-key enable payload mismatch.");

// The firmware field is an inverse lock flag: disabling writes 1.
var winDisabled = MiCommonInterfaceCodec.WriteDeviceSwitch(MiDeviceSwitch.WindowsKey, false);
Require(winDisabled.AsSpan(0, 8).SequenceEqual(new byte[] { 0x00, 0xFB, 0x00, 0x0A, 0x02, 0x00, 0x01, 0x00 }),
    "Windows-key disable payload mismatch.");

var winEnabled = MiCommonInterfaceCodec.WriteDeviceSwitch(MiDeviceSwitch.WindowsKey, true);
Require(winEnabled.AsSpan(0, 8).SequenceEqual(new byte[] { 0x00, 0xFB, 0x00, 0x0A, 0x02, 0x00, 0x00, 0x00 }),
    "Windows-key enable payload mismatch.");

var touchpadRead = MiCommonInterfaceCodec.ReadDeviceSwitch(MiDeviceSwitch.Touchpad);
Require(touchpadRead.AsSpan(0, 6).SequenceEqual(new byte[] { 0x00, 0xFA, 0x00, 0x0A, 0x00, 0x00 }),
    "Touchpad read payload mismatch.");

var resultBuffer = new byte[32];
resultBuffer[0] = 0x00;
resultBuffer[1] = 0x80;
resultBuffer[2] = 0x00;
resultBuffer[3] = 0x08;
resultBuffer[4] = 0x04;
Require(MiCommonInterfaceCodec.TryReadResult(resultBuffer, out var firmwareResult),
    "MICommonInterface result should parse.");
Require(firmwareResult.Succeeded && firmwareResult.Function == 0x0800 && firmwareResult.Value0 == 4,
    "MICommonInterface result fields mismatch.");

// Command type 3 carries a WmiObtain exec operator as protobuf-style TLV. The
// field order and tags below come from the recovered client serializer.
var wmiInput = MiCommonInterfaceCodec.ReadPerformanceMode();
var wmiBody = MiWmiExecuteCodec.WriteMiInterface(wmiInput);
var wmiFields = ReadLengthDelimitedFields(wmiBody);
Require(Field(wmiFields, 1) == @"root\wmi", "WMI namespace field mismatch.");
Require(Field(wmiFields, 2) == "MICommonInterface", "WMI class field mismatch.");
Require(Field(wmiFields, 3) == "MiInterface", "WMI method field mismatch.");
Require(Field(wmiFields, 4)?.Contains("MIFS_0", StringComparison.Ordinal) == true, "WMI object path field mismatch.");
Require(Field(wmiFields, 5) == "InData", "WMI put-place field mismatch.");
Require(wmiFields.Any(field => field.Number == 7 && field.Value.AsSpan().SequenceEqual(wmiInput)),
    "WMI input payload must be carried verbatim in field 7.");
Require(wmiFields.Any(field => field.Number == 8 && System.Text.Encoding.UTF8.GetString(field.Value).Contains("OutData", StringComparison.Ordinal)),
    "WMI output spec must be carried in field 8.");

Require(MiWmiExecuteCodec.TryReadOutData(BuildWmiResultFrame(wmiInput), out var outData) && outData.Length >= 18,
    "OutData should be extracted from a nested result frame.");

// The WMI proxy is a GameBox command: field 1 = business type tag, field 2 = the
// serialized operator (raw bytes, so it must be checked on the wire), field 3 = the
// operator kind (0 = exec).
var wmiFrame = MiServiceProtocolWriter.WriteGameBox(7, wmiBody, 0, includeStatus: true);
var wireOffset = 0;
Require(MiServiceProtocolReader.TryReadVarUInt(wmiFrame, ref wireOffset, out var commandTag)
    && commandTag == MiServiceProtocolReader.GameBoxCommandType,
    "WMI proxy must use the GameBox command type.");
Require(MiServiceProtocolReader.TryReadVarUInt(wmiFrame, ref wireOffset, out var businessTag) && businessTag == 0x08,
    "field 1 must be the business type.");
Require(MiServiceProtocolReader.TryReadVarUInt(wmiFrame, ref wireOffset, out var businessValue) && businessValue == 7,
    "business type value mismatch.");
Require(MiServiceProtocolReader.TryReadVarUInt(wmiFrame, ref wireOffset, out var dataTag) && dataTag == 0x12,
    "field 2 must carry the operator payload.");
Require(MiServiceProtocolReader.TryReadLengthDelimited(wmiFrame, ref wireOffset, out var carried)
    && carried.SequenceEqual(wmiBody),
    "WMI operator bytes must travel verbatim in the GameBox data field.");
Require(MiServiceProtocolReader.TryReadVarUInt(wmiFrame, ref wireOffset, out var statusTag) && statusTag == 0x18,
    "field 3 must carry the operator kind.");
Require(MiServiceProtocolReader.TryReadVarUInt(wmiFrame, ref wireOffset, out var statusValue) && statusValue == 0,
    "operator kind must be exec (0).");
Require(wireOffset == wmiFrame.Length, "WMI proxy frame must not carry trailing bytes.");

// Graphic mode rides the same MICommonInterface word: 00 FB 00 0B <mode> 00.
var graphicWrite1 = MiCommonInterfaceCodec.WriteGraphicMode(1);
Require(graphicWrite1.AsSpan(0, 6).SequenceEqual(new byte[] { 0x00, 0xFB, 0x00, 0x0B, 0x01, 0x00 }),
    "graphic-mode write payload mismatch");
var graphicWrite0 = MiCommonInterfaceCodec.WriteGraphicMode(0);
Require(graphicWrite0.AsSpan(0, 6).SequenceEqual(new byte[] { 0x00, 0xFB, 0x00, 0x0B, 0x00, 0x00 }),
    "graphic-mode write payload mismatch for mode 0");
var graphicRead = MiCommonInterfaceCodec.ReadGraphicMode();
Require(graphicRead.AsSpan(0, 6).SequenceEqual(new byte[] { 0x00, 0xFA, 0x00, 0x0B, 0x00, 0x00 }),
    "graphic-mode read payload mismatch");
Require(Throws(() => MiCommonInterfaceCodec.WriteGraphicMode(2)),
    "out-of-range graphic mode must be rejected");

Console.WriteLine("MiService protocol reader smoke tests passed.");

static List<(uint Number, byte[] Value)> ReadLengthDelimitedFields(byte[] payload)
{
    var fields = new List<(uint Number, byte[] Value)>();
    var offset = 0;
    while (offset < payload.Length)
    {
        if (!MiServiceProtocolReader.TryReadVarUInt(payload, ref offset, out var tag) || tag == 0)
        {
            break;
        }

        var field = (uint)(tag >> 3);
        var wire = (int)(tag & 7);
        if (wire == 2 && MiServiceProtocolReader.TryReadLengthDelimited(payload, ref offset, out var value))
        {
            fields.Add((field, value.ToArray()));
        }
        else if (!MiServiceProtocolReader.TrySkipField(payload, ref offset, wire))
        {
            break;
        }
    }

    return fields;
}

static string? Field(List<(uint Number, byte[] Value)> fields, uint number)
{
    foreach (var (fieldNumber, value) in fields)
    {
        if (fieldNumber == number)
        {
            return System.Text.Encoding.UTF8.GetString(value);
        }
    }

    return null;
}

static byte[] BuildWmiResultFrame(byte[] _)
{
    var result = new byte[32];
    result[0] = 0x00;
    result[1] = 0x80;
    result[2] = 0x00;
    result[3] = 0x08;
    result[4] = 0x04;
    var frame = new byte[2 + result.Length];
    frame[0] = 0x42; // field 8, length-delimited
    frame[1] = (byte)result.Length;
    result.CopyTo(frame, 2);
    return frame;
}

static bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (ArgumentOutOfRangeException)
    {
        return true;
    }
}

static void Require(bool condition, string? message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message ?? "Protocol smoke test failed.");
    }
}
