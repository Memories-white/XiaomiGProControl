# Xiaomi G Command Center 2.1.1.418 - investigation notes

## Target identity

- Installed product: `Xiaomi G Command Center`
- Version: `2.1.1.418`
- Installed path: `C:\Program Files\MI\Xiaomi G Command Center\2.1.1.418`
- Architecture observed: CEF-hosted React UI (`dist/static/js/main.js`) plus native bridge (`libXiaomiGCommandCenter.dll`)
- Native DLL public export observed: `WinMainDLL`
- Analyzed input SHA-256: `0C18F39188286EECEF7949583CB4210A07303CC74029084C9FC2D9133A2F6397`
- Static-analysis project: Ghidra 12.1.4, x86-64 PE, image base `0x180000000`

## Observed front-end bridge methods

The minified UI sends JSON requests through `window.cefQuery`. Confirmed method names include:

- Monitoring: `register_cpu_current_info`, `register_gpu_current_info`, `register_physical_memory_current_info`, `register_disk_current_info`, `register_network_speed`
- Performance: `get_performance_mode`, `set_performance_mode`, `register_performance_mode`, `get_performance_support_level`
- Graphics: `get_graphic_mode`, `set_graphic_mode`, `register_graphic_mode`, `get_graphic_mode_need_reboot`
- Device state: `register_fn_key_status`, `register_win_key_status`, `get_touchpad_status`
- Lighting: `set_light_effect_param`, `reset_light_effect`, `save_light_effect_parameters`, `register_effect_change`
- Screen: brightness, resolution, DPI, HDR, color gamut, eye protection and automatic brightness methods
- Overclocking: CPU/GPU status get, set and register methods; XTU repair helper

## Observed performance values

- `1`: Normal / balanced
- `2`: Silence
- `3`: Performance
- `4`: Decepticon / maximum performance

The first list above is the native notification/result enum. A controlled target-laptop
test on 2026-10-28 established that the front-end setter value is not always the same
as the resulting EC notification value:

| UI choice | `set_performance_mode` parameter | WMI leading bytes | EC/FnK result |
| --- | ---: | --- | --- |
| 极速模式 | `0` | `00 FB 00 08 00 00` | `3`, `TURBO_MODE` |
| 均衡模式 | `1` | `00 FB 00 08 01 00` | `1`, `BALANCE_MODE` |
| 静谧模式 | `2` | `00 FB 00 08 02 00` | `2`, `SILENCE_MODE` |
| 狂暴模式 | `4` | `00 FB 00 08 04 00` | `4`, `FULL_SPEED` |

All four UI modes were acknowledged by both the original front end and the lower
service during controlled target-laptop tests on 2026-10-28. The machine was restored
to its starting mode, 狂暴 / EC mode `4`, after the correlations.

The native result adapter additionally accepts an internal value `0` and has branches
for values `0` through `4`. Runtime correlation now proves that setter `0` becomes
notification `3`; UI setter values and firmware/hotkey notification values must be
modeled as separate enums.

Direct clients of `MiDeviceService` must model a third enum. Operation `1` of
`performanceMode.ModeControlInfo` maps its field-2 service values as follows:

| UI choice | Bridge setter | Pipe service value | EC result |
| --- | ---: | ---: | ---: |
| 静谧模式 | `2` | `0` | `2` |
| 均衡模式 | `1` | `1` | `1` |
| 极速模式 | `0` | `2` | `3` |
| 狂暴模式 | `4` | `3` | `4` |

The service-value mapping is confirmed both by the operation-1 switch that builds
the WMI payload and by guarded runtime requests. Passing the bridge setter directly
to the pipe is incorrect.

## Native request routing

Ghidra recovered `JsSpecialKeyMessageHandler::OnQuery` in the native bridge. It dispatches the following request families by their JSON method string:

- touchpad: get, set and register
- Windows key: get, set and register
- Fn key: get, set and register
- performance/fan mode: get, set and register
- graphics mode: get, set, register and reboot requirement

The setter payload keys recovered from the handler include `mode`, `enable` and `lock`. The front end calls performance mode as `set_performance_mode` with `{ "mode": value }`.

Performance-mode notifications are not isolated UI state. The native result adapter also checks CPU/GPU overclocking support and can restore overclocking defaults when leaving mode `4`. A replacement must preserve these side effects or explicitly warn when it cannot.

## WMI hardware layer

The bridge contains RTTI and virtual tables for the following `wmi::bitland_sync_operation` classes:

- `GetCpuFanStatus`
- `SetFanMode` / `GetFanMode`
- `GetFnKeyStatus` / `SetFnKeyStatus`
- `GetWindowsKeyStatus` / `SetWindowsKeyStatus`
- `GetTouchpadStatus` / `SetTouchpadStatus`
- `SetGraphicMode` / `GetGraphicMode`
- `SetCPUOCOn` / `SetGPUOCOn`

They target WMI namespace `root\wmi`, class `MICommonInterface`, method `MiInterface`. The class GUID observed from the installed WMI metadata is `{b60bfb48-3e5b-49e4-a0e9-8cffe1b3434b}`. `InData` is a 32-byte `uint8` array and `OutData` is a 30-byte `uint8` array.

Confirmed command construction:

| Operation | Textual words used by the native class | Leading bytes in `InData` |
| --- | --- | --- |
| Set fan/performance mode | `FB00`, `0800`, `%04X(mode)` | `00 FB 00 08 <mode> 00` |
| Get fan/performance mode | `FA00`, `0800` | `00 FA 00 08` |
| Get CPU fan status | `FA00`, `0900` | `00 FA 00 09` |
| Set Fn-key status | `FB00`, `0A00`, `0001`, followed by boolean data | `00 FB 00 0A 01 00 ...` |
| Set Windows-key status | `FB00`, `0A00`, `0002`, followed by boolean data | `00 FB 00 0A 02 00 ...` |
| Set touchpad status | `FB00`, `0A00`, `0000`, followed by boolean data | `00 FB 00 0A 00 00 ...` |
| Set graphics mode | `FB00`, `0B00`, then `0000` or `0001` | `00 FB 00 0B <mode> 00` |

The mode-4 performance command exactly matches the installed service log sample `00fb000804000000...` after converting the native word strings to little-endian bytes.

Direct WMI access from the current user session returned access denied, including a direct fixed-instance attempt with COM impersonation and packet privacy. The original executable manifest is `asInvoker`; its native bridge creates a `ClientPipeManager` and connects to the installed LocalSystem services through named pipes:

- `\\.\pipe\E09DF953-5B84-4CB7-A23B-969218E24781`
- `\\.\pipe\349B49E2-996B-4B2B-8379-1075B23F1272`

REA 6.7.0 independently confirmed the two references to the service-side `WmiDataPipeCmd` vtable at `0x180c574f4` and `0x180c574fb` (evidence `ev_5197cfe1782bd9cd9e7922729ec309ef88d90a81f287043420025d9c772740d2`). Those references construct service-to-client WMI event notifications. The inbound dispatcher has no case for command type `2`, so it is not a client write entry.

The recovered privilege boundary is command type `3`, `ClientDataPipeCmd`. Its handler deserializes `WmiObtain` operator information, executes it inside the service and sends a result back to the client. The original bridge contains matching Boost-serialized `WmiExecOperatorInfo`, `WmiReadOperatorInfo`, `WmiWriteOperatorInfo`, query and notification structures. Fn, Windows-key and touchpad controls remain locked until this type-3 layout and correlated readback are confirmed on the target.

**Correction (0.5.0):** the type-3 body is *not* a Boost archive. The `WmiObtain.WmiExecOperatorInfo.*` strings in the binary are debug labels only; the writer emits protobuf-style TLV (`tag = field << 3 | 2`, then length, then bytes) with fields 1-6 = `wmi_namespace`, `class_name`, `method_name`, `object_path`, `put_place`, `get_place` and field 8 = repeated operator sub-message. Details and evidence are in `docs/service-pipe-protocol.md`.

The rebuilt application currently uses the verified type-12 pipe path for performance modes and read-only service logs for WLAN status. It does not send direct WMI commands.

## Additional runtime-correlated controls

The following controls were toggled once through the installed original application
and restored to their captured starting state on 2026-10-28.

### Screen acceleration

- Front-end setter: `set_over_driver_status`
- Payload: `{ "status": 1 }` to enable, `{ "status": 0 }` to disable
- Lower-service route: `business_type:30`, with data `1` or `0`
- Service handlers observed: `HandleSetOverDriverStatus`,
  `UpdateOverDriverStatus`, and `kWmiSetOverDriverStatus`

The test started off, enabled the feature, received the service notifications, and
restored it off.

### Windows-key lock

- Front-end setter: `set_win_key_status`
- Payload: `{ "enable": false }` to lock/disable the Windows key and
  `{ "enable": true }` to unlock/enable it
- WMI bytes for disabled: `00 FB 00 0A 02 00 01 00 ...`
- WMI bytes for enabled: `00 FB 00 0A 02 00 00 00 ...`

This confirms that the trailing boolean is encoded as an inverse lock flag at the WMI
layer. The test started with the Windows key enabled and restored it enabled.

### Keyboard lighting

The original keyboard-light scene on this machine is scene `4`, effect `1` (波浪),
with direction `2`, brightness/intensity `2`, and speed `1`. The controlled test used
effect `2` (呼吸), then restored the original effect:

- Apply preview: `set_light_effect_param`
- Commit: `save_light_effect_parameters`
- Wave restore payload: scene `4`, effect `1`, `direction:2`, `intensity1:2`,
  `speed:1`, `trigger:false`
- Breath test payload: scene `4`, effect `2`, `direction:4`, `intensity1:2`,
  `speed:1`, one red color entry and positions `[1,3,6,9,11,17]`

The keyboard, top light, and light strip remained on. Their starting linkage modes
(top light linked to the strip, strip linked to the keyboard) were not changed.

### WLAN acceleration limitation

The original application reports WLAN acceleration as off and periodically calls the
Xiaomi-router endpoints `/maccel` and `/ma_check` at `miwifi.com`. In this environment
those checks fail during TLS connection and the status stays off. Clicking the status
area did not issue a setter. Therefore no hardware/protocol write was inferred or
forced: this feature still needs a supported Xiaomi router for a meaningful runtime
correlation.

## MiDeviceService pipe framing

Static analysis of `MiDeviceServiceEntry.dll` 4.1.1.640 (SHA-256 `9DC30210C58A032E336780C641EEB8C081EE2364D27937495ABCA7D49D9485E9`) recovered the service-side message reader, writer and GameBox protobuf implementation.

- The primary service pipe is duplex, overlapped and message-mode, with a maximum of 255 instances.
- The outer header is four bytes: frame kind at byte 0, an unresolved byte at byte 1, then a little-endian 16-bit client-identifier length at bytes 2-3.
- Kind `1` registers the client identifier. Other kinds skip the four-byte header and client identifier before dispatch.
- Commands begin with a protobuf-style varint command type. `GamingBoxCmd` returns type `1`; `WmiDataPipeCmd`, `ClientDataPipeCmd` and `PerformanceModeCmd` return `2`, `3` and `12` respectively.
- `ProtoGameBox` is a protobuf-lite message with field 1 `business_type` (varint), field 2 payload data (length-delimited string) and field 3 a response/status integer (varint; semantic name inferred).
- Command type `12` wraps `performanceMode.ModeControlInfo`; its generated parser confirms field 1 as an operation varint and field 2 as a value varint. Operation `1` consumes an internal service enum and builds a fan/performance WMI write; this enum is separately mapped above. The remaining operation meanings still require correlation.

The exact field numbers are confirmed by the generated parser tags `0x08`, length-delimited field 2 and `0x18`, not guessed from logs. See `docs/service-pipe-protocol.md` for the compact layout and implementation boundary.

## Reproducible evidence

The `scripts` directory contains the headless Ghidra import, cross-reference extraction, targeted decompilation, constant dump and virtual-table dump scripts. Generated analysis outputs live under ignored directories `analysis-ghidra`, `analysis-service` and `.analysis`; the source scripts remain versionable.
