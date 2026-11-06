# Hardware validation — 2026-10-28

Target: Xiaomi G Pro 2024 (`TM2211`)

Validation was performed in two phases. The first phase was read-only. After explicit
user authorization, the second phase performed reversible tests of all four
performance modes, screen acceleration, one keyboard-light effect, and the Windows-key
lock. WLAN acceleration was inspected but could not be exercised without a working
supported Xiaomi-router path. No GPU acceleration, graphics-mode switch, service
restart, reboot, or firmware update was performed.

## Result

The original Xiaomi G Command Center visibly reported **狂暴模式** as the selected
performance mode during the validation session. The selected position was the fourth
mode in the performance-mode control.

This agrees with the earlier service-to-firmware transaction captured on the same
machine:

- `07:42:19.712`: `set_performance_mode {"mode":4}`
- `07:42:19.712`: device request `00 fb 00 08 04 00 ...`
- `07:42:19.720`: `business_type:26, data:4`
- `07:42:19.728`: firmware notification `01 16 04 00 00 00 00 00`
- `07:42:19.730`: `FnKNotify():FULL_SPEED`
- `07:42:19.731`: `LastMode = CurMode:04`, AC power detected

The original UI and the lower-layer service evidence therefore agree that mode value
`4` maps to the original application's 狂暴/FULL_SPEED state.

## Important finding: EC mode and Windows power plan are separate states

During the original mode-4 transaction, the Xiaomi service logged:

- `HandleSetPowerActiveScheme, value:4`
- `PowerSetActiveScheme failed, status:87`

At `15:20`, `powercfg /GETACTIVESCHEME` still reported the standard Windows Balanced
plan (`381b4222-f694-41f0-9685-ff5bb260df2e`). This does not disprove the firmware
mode: the EC/FnK path had already acknowledged mode `4` and emitted `FULL_SPEED`.

The replacement application must therefore:

1. treat EC/firmware performance mode as the authoritative mode state;
2. display the Windows power plan as a separate status;
3. report a partial failure when the EC mode succeeds but a Windows power-plan side
   effect fails;
4. never infer 狂暴模式 solely from `powercfg`.

## Read-only safety checks

- Opening the installed original application exposed its existing mode state; no mode
  tile or toggle was clicked.
- A direct WMI metadata inspection confirmed `root\\wmi`, `MICommonInterface`, and
  method `MiInterface`.
- A direct instance read was denied by the device's WMI permissions, so no attempt was
  made to bypass the Xiaomi service or elevate further.
- The read-only phase contained no `00 fb 00 08` write opcode; every later write was
  part of the explicitly authorized reversible validation documented below.
- GPU telemetry at `15:20`: RTX 4060 Laptop GPU, 53 C, 22.28 W. This is only a point-in-
  time observation, not a thermal stress test.

## Controlled write validation

Starting state: 狂暴 mode, EC notification mode `4` / `FULL_SPEED`, AC power.

At `15:27:50`, the original application switched to 极速 mode:

- front-end request: `set_performance_mode {"mode":0}`;
- WMI request: `00 fb 00 08 00 00 ...`;
- service business value: `0`;
- firmware notification: `PerformanceFnKNotify():3`;
- named state: `FnKNotify():TURBO_MODE`;
- readback/notification: `LastMode = CurMode:03`;
- UI registration callback: `setCurrentMode 3`.

At `15:28:18`, the starting state was restored:

- front-end request: `set_performance_mode {"mode":4}`;
- WMI request: `00 fb 00 08 04 00 ...`;
- service business value: `4`;
- firmware notification: `PerformanceFnKNotify():4`;
- named state: `FnKNotify():FULL_SPEED`;
- readback/notification: `LastMode = CurMode:04`;
- UI registration callback: `setCurrentMode 4`.

The GPU temperature was 52 C before the test, 54 C after the transition to 极速,
and 55 C after restoring 狂暴. The active Windows power plan remained Balanced.
The mode-4 restoration again logged `PowerSetActiveScheme failed, status:87`; the EC
mode nevertheless succeeded and emitted `FULL_SPEED`.

### Validated mapping

The UI setter value and EC result are separate enums. In particular, 极速 mode must
send setter value `0`, not `3`. A replacement that writes `3` merely because the
notification/readback is `3` would not be protocol-equivalent to the original.

At `15:36:12`, the original application selected 均衡:

- setter value `1`;
- WMI request `00 fb 00 08 01 00 ...`;
- service `business_type:26, data:1`;
- EC result `PerformanceFnKNotify():1`, `BALANCE_MODE`, `CurMode:01`.

At `15:36:26`, it selected 静谧:

- setter value `2`;
- WMI request `00 fb 00 08 02 00 ...`;
- service `business_type:26, data:2`;
- EC result `PerformanceFnKNotify():2`, `SILENCE_MODE`, `CurMode:02`.

At `15:36:50`, the starting 狂暴 state was restored with setter value `4`,
`FULL_SPEED`, and `CurMode:04`. The Windows power plan remained Balanced. The known
mode-4 side-effect failure `PowerSetActiveScheme failed, status:87` recurred, while the
EC write and readback succeeded.

The complete correlated mapping is therefore:

| UI mode | Setter | EC result |
| --- | ---: | ---: |
| 极速 | `0` | `3` / `TURBO_MODE` |
| 均衡 | `1` | `1` / `BALANCE_MODE` |
| 静谧 | `2` | `2` / `SILENCE_MODE` |
| 狂暴 | `4` | `4` / `FULL_SPEED` |

## Screen acceleration

Starting state: off.

- `15:37:16`: `set_over_driver_status {"status":1}` succeeded; the lower service
  logged `business_type:30, data:1`, `HandleSetOverDriverStatus`, and
  `kWmiSetOverDriverStatus`.
- `15:37:32`: `set_over_driver_status {"status":0}` succeeded; the lower service
  logged `business_type:30, data:0` and the corresponding notifications.

Final state: off, matching the starting state.

## WLAN acceleration

The UI reported the feature as not enabled. It exposed no working switch in the
current network environment. The lower service periodically attempted the Xiaomi
router acceleration/check endpoints, but the requests failed with
`OpenSSL SSL_connect: SSL_ERROR_SYSCALL in connection to miwifi.com:443`; notification
data remained `0`.

No setter or hardware write was forced. A supported Xiaomi router and successful
router-service handshake are required before this path can be considered validated.

## Keyboard lighting

Starting state: keyboard light on, 波浪 effect, speed `1`, brightness `2`, direction
`2`; top light and light strip on with their original linked modes.

- `15:38:11`: `set_light_effect_param` sent scene `4`, effect `2` (呼吸), followed by
  a successful `save_light_effect_parameters` at `15:38:16`.
- `15:39:08`: restore sent scene `4`, effect `1` (波浪), direction `2`, intensity `2`,
  speed `1`; `save_light_effect_parameters` completed at `15:39:14`.

Only the keyboard effect changed. The three light switches and top/strip linkage modes
were not changed. Final state matches the captured starting state.

## Windows-key lock

Starting state: `WIN键启用中`.

- `15:39:26`: `set_win_key_status {"enable":false}` succeeded; WMI payload began
  `00 fb 00 0a 02 00 01 00` and the UI visibly reported `WIN键已停用`.
- `15:39:37`: `set_win_key_status {"enable":true}` succeeded; WMI payload began
  `00 fb 00 0a 02 00 00 00`.

Final state: Windows key enabled, matching the starting state. The Fn-key setting was
left enabled and was not modified.

## Final safety state

- Performance: 狂暴 / EC mode `4` / `FULL_SPEED`
- Screen acceleration: off
- WLAN acceleration: off and unchanged
- Keyboard lighting: 波浪, scene `4`, effect `1`, speed `1`, brightness `2`, direction `2`
- Top light and light strip: on, original linkage modes unchanged
- Windows key: enabled; Fn key: enabled and untouched
- Windows power plan: Balanced
- Final GPU point-in-time telemetry: 53 C, 18.69 W

## Follow-up validation on 2026-10-29

The remaining reversible controls were retested after the performance-mode fix:

- Screen acceleration accepted the enable command and the immediate restore-to-off
  command. `MiDeviceService_log.txt` recorded `HandleSetOverDriverStatus` for status
  `1` followed by status `0`, with both corresponding WMI notifications. The service
  response does not expose an independent final-state readback, so this is recorded as
  a correlated write-and-restore rather than a readback-confirmed state.
- Keyboard lighting changed from wave to breath, was applied, then changed back to wave
  and applied again. The final UI readback showed wave, speed `1`, and brightness `2`;
  keyboard, top-light, strip switches and both linkage modes were left unchanged.
- Windows-key lock changed from enabled to disabled and then back to enabled. The final
  UI readback showed `WIN键启用中`. The service log recorded the inverse lock WMI
  payload `00fb000a02000100...` followed by the restore payload
  `00fb000a02000000...`.
- WLAN acceleration remained off. The service continued reporting
  `wifi_acc_status_0`; its Xiaomi-router requests still had no valid response, so no
  setter was forced.
- A final independent performance query returned EC `4`; no restoration write was
  necessary because full-speed mode was already active.

## Next validation gate

Before enabling writes in GProControl, implement a guarded hardware adapter with:

- explicit model and service-version allowlisting;
- readback-before-write and readback-after-write;
- one command in flight at a time;
- exact payload-length and mode-range checks;
- timeout handling and a visible partial-failure state;
- automatic rollback to the captured starting mode when a reversible test fails;
- writes disabled by default in Debug builds until a user-confirmed hardware session.

The guarded adapter may now implement all four correlated performance setter paths
(`0`, `1`, `2`, and `4`), the screen-acceleration boolean, the Windows-key boolean,
and the captured keyboard-light effect structures. WLAN acceleration remains disabled
until separately validated with a supported router. The replacement must use the
Xiaomi service rather than direct user-session WMI, which is denied on this machine.
