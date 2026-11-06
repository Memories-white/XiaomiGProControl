# MiDeviceService pipe protocol

Target: `MiDeviceServiceEntry.dll` 4.1.1.640  
SHA-256: `9DC30210C58A032E336780C641EEB8C081EE2364D27937495ABCA7D49D9485E9`

This document records static-analysis evidence plus the small request subset later
correlated on the target Redmi G Pro 2024. Runtime writes are guarded by an exact
machine/version whitelist and an explicit, session-only user unlock.

## Transport

The service creates `\\.\pipe\E09DF953-5B84-4CB7-A23B-969218E24781` with duplex, overlapped, message-mode access and up to 255 instances. Each `ReadFile` call provides a one-megabyte buffer; outgoing messages are queued and written as complete buffers.

Every service frame starts with this envelope:

| Offset | Size | Meaning |
| --- | ---: | --- |
| `0x00` | 1 | frame kind; `1` registers or updates the client identifier |
| `0x01` | 1 | reserved/unknown |
| `0x02` | 2 | UTF-8 client-identifier length, little-endian |
| `0x04` | variable | client identifier |
| next | remaining | command bytes; absent for a registration frame |

For non-registration frames, the service skips `4 + client_id_length` bytes and invokes the command dispatcher on the remainder.

## Command wrapper

The first command value is a protobuf-style varint. The GameBox command class returns command type `1`; other recovered command classes include type `2` (service-to-client result data), type `3` (`MiOSD::MiOSDEventInfo`, not WMI) and type `12` (performance mode). WMI execution is carried by GameBox, as detailed below.

The GameBox wrapper owns a `ProtoGameBox` protobuf-lite object. Its recovered wire layout is:

```proto
message ProtoGameBox {
  uint32 business_type = 1;
  string data = 2;
  int32 status = 3; // semantic name inferred from response construction
}
```

Evidence for the field numbers comes from the generated parser and serializer:

- field 1 accepts tag `0x08` and writes the object member at `+0x18`;
- field 2 is length-delimited and owns the string member at `+0x10`;
- field 3 accepts tag `0x18` and writes the integer member at `+0x1c`.

`PipeServerHandler::HandleGameBoxBusiness` reads field 1 as the business type and field 2 as the business payload. The response constructor supplies the business type, `0` for field 3 and a serialized payload string.

## Current implementation boundary

`GProControl.Protocol.MiServiceProtocolReader` defensively decodes envelopes,
GameBox messages and performance-mode messages. `MiServiceProtocolWriter` encodes
the correlated registration, command, performance-mode and GameBox subset.
`MiDeviceServiceClient` owns the duplex transport and ignores envelope-only
registration acknowledgements while waiting for a command response. Writes are
serialized by `XiaomiHardwareControlService` and armed by default only after the
model, vendor version and a real transport handshake all pass.
On this service build, operation `0` completes asynchronously and its EC result
is not returned as a command envelope. The client therefore prefers a correlated
pipe response, then reads only a new `HandleGetResult:00800008xx` record from the
original service log. The `xx` byte is the EC mode; the timestamp must be newer
than the request so stale log data cannot confirm an action.

## The WMI proxy: a GameBox command, not command type 3

**Correction (0.8.0).** Earlier revisions of this document claimed that command type
`3` (`ClientDataPipeCmd`) carries the `WmiObtain` operator. That is wrong. Recovered from
the service:

- `ClientDataPipeCmd` constructs and deserializes **`MiOSD::MiOSDEventInfo`**, a
  protobuf-lite message (its handler `FUN_180c0bc10` is literally a
  `dynamic_cast<MiOSD::MiOSDEventInfo*>` from `google::protobuf::MessageLite`). Its method
  names include `GetPowerRequests` and `GetnternalScreenId` (sic). It has nothing to do
  with WMI.
- The WMI proxy is a **GameBox command (type 1)**.
  `PipeServerHandler::HandleGameBoxBusiness` routes generic business types to
  `FUN_180c2ffb0(context, business_type, status, data)`, and that dispatcher selects the
  operator kind from `ProtoGameBox.status`:

  | `status` | Operator class |
  | ---: | --- |
  | 0 | `WmiExecOperatorInfo` (call `MICommonInterface.MiInterface`) |
  | 1 | read |
  | 2 | `WmiNotifyAsyncOperatorInfo` |
  | 3 | write |
  | 4 | query |

  `ProtoGameBox.data` (field 2) carries the serialized operator as **raw bytes**.
  `ProtoGameBox.business_type` (field 1) is only a tag the service echoes back, but some
  values are claimed by other handlers (`20`/`21`, `22`/`23`/`25`, and the WildEngine
  set `26`/`27`/`29`/`30`), so a client must pick from the generic range.

Frame for the read-only probe (business type 7, exec operator, `00 FA 00 0A 02`):

```
01                       GameBox command type
08 07                    field 1: business_type = 7
12 97 01 <151 bytes>     field 2: serialized WmiExecOperatorInfo (raw)
18 00                    field 3: status = 0 (exec)
```

## The WmiObtain operator body

`MiWmiExecuteCodec.WriteMiInterface` encodes the operator the client builds in
`FUN_1801eb410`; every field assignment is confirmed by that builder's setters:

| Tag | Field | Content |
| ---: | ---: | --- |
| `0x0A` | 1 | `wmi_namespace` = `root\wmi` |
| `0x12` | 2 | `class_name` = `MICommonInterface` |
| `0x1A` | 3 | `method_name` = `MiInterface` |
| `0x22` | 4 | `object_path` = `MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'` |
| `0x2A` | 5 | `put_place` = `InData` (the input **property name**) |
| `0x32` | 6 | `get_place` = `OutData` (set only by read operators) |
| `0x3A` | 7 | the 32-byte `InData` value itself |
| `0x42` | 8 | repeated sub-operator `{ 0x0A "OutData", 0x10 varint 1 }` |

Encoding is protobuf-style: tag = `(field << 3) | 2`, then a varint length, then bytes;
the fast path writes the length as a single byte while it stays below `0x80`.


### Concrete `MICommonInterface.MiInterface` operator

The client binary contains the exact strings this operator needs (offsets
`0x3A3654`-`0x3A36C0`): `MICommonInterface`,
`MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'`, `InData`, `OutData`.
`MiWmiExecuteCodec.WriteMiInterface` encodes them as:

```
                                     operator body inside GameBox.data (not command type 3)
0A 08 "root\wmi"                     field 1  wmi_namespace
12 11 "MICommonInterface"            field 2  class_name
1A 0B "MiInterface"                  field 3  method_name
22 34 "MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'"   field 4 object_path
2A 06 "InData"                       field 5  put_place (input property name)
3A 20 <32-byte InData>               field 7  the input value itself
42 0B { 0A 07 "OutData" 10 01 }      field 8  repeated output spec
```

So `put_place` / `get_place` are WMI *property names*, not raw buffers; the 32-byte
firmware payload travels in field 7 verbatim.

### Verification status

The format is recovered statically and pinned by
`tests/GProControl.ProtocolSmokeTests`. The GameBox carrier was subsequently
validated on the target machine by matching newly executed buffers in the service
log. No WMI write path uses command type 3.

`tests/GProControl.ServiceProbe --wmi` builds the operator above from a 32-byte
`InData`, wraps it in GameBox business type 0/status 0, and prints returned frames.
Run it from an administrator terminal. The app's automatic self-test additionally
checks the service execution log and searches compatible business types.

```powershell
# read fan/performance mode (safe, read-only opcode 0xFA00)
dotnet run --project .\tests\GProControl.ServiceProbe -- `
  --wmi 00FA000800000000000000000000000000000000000000000000000000000000 --wait 2000
```

A successful round trip appears in `MiDeviceService_log.txt` as a new
`OperatorManager::HandleExecBusiness info_detail 00fa0008...` record followed by
`HandleGetResult:`. If nothing is logged the operator was rejected; iterate with
`--hex <payload-hex>` to try alternative field assignments.

### Service-side observables

The service log is a complete oracle for this path; every executed operation is logged
verbatim as a 32-byte `InData` buffer.

Observed `InData` opcodes:

| Buffer prefix | Operation |
| --- | --- |
| `00 FA 00 08` | get fan/performance mode |
| `00 FB 00 08 <mode> 00` | set fan/performance mode |
| `00 FA 00 09` | get CPU fan status |
| `00 FA 00 0A 01` | get Fn-key status |
| `00 FA 00 0A 02` | get Windows-key status |
| `00 FB 00 0A 00 <lock>` | set touchpad status |
| `00 FB 00 0A 01 <lock>` | set Fn-key status |
| `00 FB 00 0A 02 <lock>` | set Windows-key status |
| `00 FA 00 0B` | get graphics mode |
| `00 FB 00 0B <mode>` | set graphics mode |
| `00 FB 00 0E` | remaining unknown write observed at application start |

Observed notification codes (`OperatorManager::HandleRegResult:<code><value>...`):
`0x09` Fn key, `0x0D` graphics mode, `0x16` performance mode.

## Screen acceleration readback

Command type 30 returns only a GameBox acknowledgement with an empty payload, so the
applied state must be read from the service trace. After applying the request the
service logs `WmiIndicate, kWmiSetOverDriverStatus` and then emits the resulting state
to clients:

```
[WildEngine] NotifyMsg30, data:1   # 30 = supported (stays 1)
[WildEngine] NotifyMsg31, data:1   # 31 = enabled (1 on, 0 off)
```

Correlated on the target machine: enabling logged `NotifyMsg31, data:1`, disabling
logged `NotifyMsg31, data:0`. `HandleSetOverDriverStatus, status:N` records the
*requested* value and `UpdateOverDriverStatus, over_driver_status_:N` records the value
observed before the change, so neither is a usable post-write readback on its own.

## Pipe reachability

`WaitNamedPipe` succeeding proves only that a pipe object exists; it does not prove the
current process may connect. On this machine both service pipes satisfy
`WaitNamedPipe` while `ConnectNamedPipe` fails with `ERROR_ACCESS_DENIED` (5) for a
token the service DACL does not accept — for example a low-integrity sandbox token, and
also a plain medium-integrity token in an automated session. The application therefore
verifies readiness with a real connect plus registration frame instead of trusting pipe
presence alone.


## Performance-mode command

Command type `12` wraps `performanceMode.ModeControlInfo`. Its generated protobuf parser confirms two unsigned-varint fields:
```proto
message ModeControlInfo {
  uint64 operation = 1; // semantic name inferred from the dispatcher
  uint64 value = 2;
}
```

The service dispatches operations `0, 1, 2, 3, 4, 5, 6, 8, 9`. Operation `1`
consumes field 2 and constructs a `SetFanMode`-family WMI payload. This field uses
an internal service enum, not the public `set_performance_mode` bridge enum. The
validated service values `0`, `1`, `2`, and `3` map to EC readback values `2`,
`1`, `3`, and `4` respectively. Operation `0` issues the corresponding read
request. Other operations remain outside the implemented whitelist.

### Timing: the service never answers type 12 on the pipe

The first implementation waited the generic four-second response window after every
command. Target-machine logs show that this window always expired: `HandleGetResult`
records appear exactly four seconds apart because each one is the *next* command
starting after the previous wait timed out. A single mode switch therefore cost three
commands — roughly twelve seconds.

The service publishes the result instead:

- a read produces `HandleGetResult:00800008<hex EC mode>`;
- a write produces `HandleRegResult:0116<hex EC mode>` twice.

Both land within milliseconds. The implementation now sends the command, waits only a
short 250 ms probe in case a correlated envelope ever appears, then reads the result
from the log incrementally (50 ms polling, cursor + rolling buffer). Mode switches
complete in roughly 0.3–0.7 s.

The same log is the transport proof for the GameBox WMI operator: `HandleExecBusiness  info_detail
<32-byte hex>` records the exact buffer the service executed, so the device-page
self-test can confirm the `WmiObtain` body format without writing anything.

