# RMGRP6B0P0909 BIOS static analysis

Analysis date: 2026-10-30  
Target platform: Xiaomi G Pro 2024 / `TM2211`

## Safety boundary

This analysis was performed offline. The updater, `Install.bat`, `H2OFFT-Wx64.exe`,
EFI helper and flash driver were not executed. No BIOS, EC, NVRAM or device setting
was written.

## Package identity

- Source archive: `RMGRP6B0P0909.zip`
- Archive SHA-256: `7714F52C20340F2E77947873A482DB851608C4FC9C1C0F5EE0B8C72E402ACBBE`
- Signed SFX SHA-256: `F8E169747AEBF40F01E5E5575E9E0736F0AE573DC8E77D4CAEB94FB7530472AD`
- The SFX Authenticode signature is valid and identifies `Xiaomi Communications Co., Ltd.`.
- The inner Insyde flash components have valid Insyde signatures; the Intel firmware
  update helper has a valid Intel signature.

The SFX uses the configuration `Protect="yes"`. Static decompilation shows that its
payload decoder rotates every byte right by one bit. The project-local
`tools/BiosSfxDecoder` reproduces this transformation without launching the updater.
The decoded 7z payload SHA-256 is
`EC68D6BDB920818216422C3BC3FC37AB15A0648DDF802D34F89503CF85ACEF43`.

## Flash scope

`platform.ini` selects `TM2211.fd` and requires AC power plus at least 20% battery.
Its region configuration is:

| Region | Enabled |
| --- | --- |
| BIOS | Yes |
| EC | No |
| ME | No |
| GbE | No |
| Descriptor | No |
| Platform data | No |

`UpdateEC`, EC compare and EC verify are also disabled. This package therefore gives
us the BIOS-side ACPI/WMI/SMM implementation, but it is not an EC firmware image and
does not expose the EC register map by itself.

## Recovered ACPI/WMI contract

UEFIExtract found the OEM modules `OemWMISmmCallback`, `HQAcpiTable`, `ThermalSmm`,
`OemAcpiPlatform` and `UpdateDsdtByAcpiSdtDxe`. The raw SSDTs from the first two were
successfully disassembled with ACPICA iASL.

`OemWMISmmCallback` defines a `PNP0C14` WMI device with UID `MIFS`. Its `_WDG` table
maps GUID `{B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B}` to object ID `AA`, method flag
`0x02`. This is the same GUID exposed by the installed `MICommonInterface` WMI class,
and its AML method is `WMAA`.

`WMAA` accepts method ID `1` and interprets its 32-byte input buffer as:

| Offset | Width | AML field | Meaning |
| ---: | ---: | --- | --- |
| `0x00` | 2 | `FUN1` | operation: `0xFA00` read, `0xFB00` write |
| `0x02` | 2 | `FUN2` | function group |
| `0x04` | 2 | `FUN3` | selector/value |
| `0x06` | 4 | `FUN4` | value |

The return buffer starts with status `0x8000` on success, followed by the function
and result fields. This explains the original service log prefix `00 80 00 08` for a
successful performance-mode result.

### Verified functions

| Function | Read/write mapping |
| --- | --- |
| `0x0800` | Performance/fan mode. Write values `0..4` update EC field `FANF`; a WMI event `0x16` is emitted. |
| `0x0900` | Reads CPU/GPU fan tachometers in Value0/Value1, then CPU/GPU temperatures in Value2/Value3. Admin probe verified 4707/4568 RPM and 90/56 C on 2026-11-05. |
| `0x0A00`, selector `0` | Touchpad state (`TPDI` read, `TPEN` write). |
| `0x0A00`, selector `1` | Fn-key state (`FNSW` read, `FNHK` write). |
| `0x0A00`, selector `2` | Windows-key state (`WIDS` read, `WINK` write). |
| `0x0A00`, selector `6` | Overdrive state (`ODST` read, `ODEN` write). |

For the three boolean device controls, input `FUN4=1` writes EC value `1`; input
`FUN4=0` writes EC value `2`. That confirms the original control center payloads but
does not grant a normal desktop process permission to call the WMI method.

The verified byte codec is implemented in
`GProControl/Protocol/MiCommonInterfaceCodec.cs` and covered by protocol smoke tests.
It intentionally performs no WMI or hardware access.

## Integration consequence

The BIOS establishes command semantics and selectors; it does not remove the
Windows privilege boundary. Direct sensor reads require administrator rights on
the tested machine. Device-switch writes use the vendor LocalSystem service's
GameBox WMI operator (type 1, protobuf-style body), which was later validated by
new execution records in its log. Earlier assumptions that this was a Boost
request in command type 3 were incorrect; type 3 carries OSD events.

The implemented application now runs elevated, validates the machine/vendor
version and service handshake, then performs a read-only proxy self-test before
enabling device switches. See [service-pipe-protocol.md](service-pipe-protocol.md).

## Reproducible artifacts

- Decoded package: `analysis-firmware/RMGRP6B0P0909/decoded-payload.7z`
- Firmware image: `analysis-firmware/RMGRP6B0P0909/decoded/TM2211.fd`
- UEFI report: `analysis-firmware/RMGRP6B0P0909/decoded/TM2211.fd.report.txt`
- Disassembled SSDTs: `analysis-firmware/RMGRP6B0P0909/acpi/*.dsl`
- Offline SFX decoder: `tools/BiosSfxDecoder`

## REA evidence

- ZIP inspection: `ev_044c670202df9560444f295d801725931fb51b9bd2200c78af82181409990351`
- SFX inspection: `ev_392a9d3722188b9aea6c8c42ae73158fa72d184bd4843c5ecb50d6affeef9e15`
- SFX binary overview: `ev_fe72c471ed782c4fe31ae42c68f7425ed652fb2ab2d28036ed9f84458797b9e8`
- `Protect` reference: `ev_b75b126c9cca0f36c762e415c709b7431948bb1c4d2a88943273efa4c1fe8647`
- Payload-decoder functions: `ev_90c46207ea9a1642e3bf79420a9cd7854a082bac74dd617bb80d182884625baf`
