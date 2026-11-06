# Performance-mode fix validation — 2026-10-29

Target: Redmi G Pro 2024 / TM2211  
Original control center: 2.1.1.418  
Replacement build: 0.3.1 Debug x64

## Root cause

The public `set_performance_mode` bridge enum was incorrectly sent directly as
field 2 of MiDeviceService command type 12, operation 1. Static analysis of the
service's operation-1 switch and guarded runtime logs prove that the pipe uses a
different internal enum.

| UI mode | Original bridge setter | Pipe service value | Expected EC |
| --- | ---: | ---: | ---: |
| 静谧 | 2 | 0 | 2 |
| 均衡 | 1 | 1 | 1 |
| 极速 | 0 | 2 | 3 |
| 狂暴 | 4 | 3 | 4 |

The failed pre-fix probe requested 静谧 with pipe value 2 and the service produced
EC 3, demonstrating the enum collision. Its old rollback value 4 was also outside
the valid service-value set. The original control center was used immediately to
restore and confirm EC 4 before further work.

## Fix

- Added an explicit catalog for UI name, pipe service value, and EC readback.
- Replaced index-dependent selection with explicit tagged radio buttons.
- Corrected the service whitelist and rollback map to service values 0..3.
- Added pre-write readback, post-write verification, and automatic restoration.
- Added a hard deadline around named-pipe reads.
- Added a restricted hardware smoke-test entry point that requires the exact
  machine/version guard and always restores full-speed EC 4 after a target test.

## Runtime result

Each target test started from confirmed EC 4, applied one mode, verified the EC
readback, then restored and re-verified EC 4:

| Test | Applied readback | Final readback |
| --- | ---: | ---: |
| 静谧 | EC 2 | EC 4 |
| 均衡 | EC 1 | EC 4 |
| 极速 | EC 3 | EC 4 |

The dedicated restore-only path also changed the observed EC 1 state to EC 4 and
verified it before the three target tests. All hardware smoke-test runs exited 0.

Final safety state: **狂暴 / EC 4**.
