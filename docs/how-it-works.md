# How it works

Notes from the reverse engineering of the 8BitDo Ultimate 2C Wireless (controller firmware 1.09) and its 2.4 GHz dongle
(firmware 1.00). Both run a Telink chip (TC32 instruction set). Addresses are flash image addresses unless noted.

## Why there is no battery level over 2.4 GHz

- In game mode the dongle is an Xbox 360 controller to Windows (XInput). XInput reports it as "wired", and
  Windows.Gaming.Input returns a fixed 1000/1000 mWh: neither is a real level.
- The controller does track its battery: millivolts in RAM `0x843424` (task at `0xb6a0`), percentage in RAM `0x843415`
  (function `0xb5a8`: 3499 mV → 0 %, 4100 mV → 100 %, filtered over 10 readings).
- It only sends the level in the 22-byte radio packet format used by 8BitDo's own software mode (packet type `0x8e`).
  The dongle only passes it to the PC in a report (`02 66 …`) that exists in its "idle" mode (`2DC8:301C`), where it is
  no longer a game controller.
- The configuration channel's read request (`81 05 05`) only reads flash, not RAM (the address is masked), so the
  level cannot be read from RAM either.

## Identities and channels

| What | USB ID | Config channel |
|---|---|---|
| Controller on USB cable | `2DC8:310A` | page `FF7A`, identify product `0x301B` |
| Dongle with the controller connected | `2DC8:310A` | page `FF7A`, identify product `0x301C` |
| Dongle alone (controller off or on cable) | `2DC8:301C` | page `FFA0`, identify product `0x301C` |
| Update mode (bootloader), either device | `2DC8:3208` | 64-byte reports without ID |

Configuration channel requests (64-byte output reports):

- Identify `81 05 00 21 01` → `02 22 <version ×100> 00 00 00 <product LE> …`
- Read flash `81 05 05`, length in bytes 6-7, address in bytes 14-17, 46 bytes max → `02 05 00 .. 05`, data from byte 18.
- Enter update mode `81 05 00 51 00`.
- Requests that write, erase or change state, never sent by this tool: `81 05 c1` (lifts write protection),
  `81 05 02/03/04/0b/c4`, `81 05 00 38`, `81 05 00 61`, `81 xx 66 AA 63/64`.

Update mode (from the arithon-f toolkit): `[0]=05 [1:3]=command [5:7]=length [7:9]=CRC-16/MODBUS of the data
[9:13]=parameter [13:17]=address [17:]=data`; commands 5 read, 4 erase (4 KB sectors), 3 write (46 bytes max),
8 identity, 7 restart.

Flash layout: controller 512 KB, application `0x6000-0x19000` (official `.dat` = 0x1C-byte header + payload at
`0x6000`), update record `0x3f000`, Bluetooth address sector `0x76000`. Dongle 128 KB, bank A `0x6000-0x10000`,
update record `0x12000`, calibration `0x1e000`. Below `0x6000`: the update mode, never written.

## TC32

TC32 is Thumb-1 with the top nibble of every halfword remapped. TC32 → Thumb:

```
0→4 1→5 2→8 3→9 4→7 5→6 6→B 7→A 8→E 9→F A→2 B→3 C→D D→C E→1 F→0
```

`research/tc32dis.py` converts and disassembles; `research/xref.py` follows base+offset memory accesses.

## Radio packet (controller 1.09)

Built in RAM `0x843980` (RAM code `0xca0`):

| Byte | Content |
|---|---|
| 0 | type: `0x90` X-input, `0x8f` D-input, `0x8d`, `0x8e` software mode |
| 1-2 | channels (sum of the pairing address % `0x9c` + 2) |
| 3-6 | buttons |
| 7-14 | sticks |
| 15-16 | triggers |
| 17 | sum of bytes 0-16 |
| 18-19 | 16-bit sum, **or** in 22-byte packets: link quality (RAM `0x843487`) and battery (`0xb5a8()`) |

Send function `0xdd78`: 20 bytes, or 22 bytes when RAM `0x843468` (software mode) is set.

## The controller patch (9 bytes)

| Address | Original | Patched | Effect |
|---|---|---|---|
| `0xdd90` | `beq 0xdd9a` | `nop` | always 22 bytes |
| `0xddc8` | `beq 0xddd8` | `nop` | always fill bytes 18-19 |
| `0xde14` | literal `0x843487` | `0x843415` | byte 18 = battery % instead of link quality |
| `0xddd2` | `bl 0xb5a8` | `movs r0, #0xBA` ; `nop` | byte 19 = `0xBA` signature |

Type, checksum and the rest of the packet are unchanged. A stock dongle ignores the two extra bytes.

## The dongle patch (34 bytes)

- `0x6c28` (radio receive, RAM code at `0xb24`): `bne` → `nop`. Bytes 18-19 of every game packet are now kept in RAM
  `0x842e47` and `0x842e1c` (only `0x8e` packets were before).
- `0xac0e` (identify reply, function `0xabd8`): the instruction pair storing the version is replaced by `bl 0xc100`.
- `0xc100` (empty flash after the image): a 10-instruction helper that stores the version as before, then copies
  `0x842e47` and `0x842e1c` into the reply buffer at bytes 7 and 8, which are bytes **8 and 9** on the PC side
  (after the report ID `0x02`).

```
movs r3, #0x64 ; str r3, [sp, #0x40] ; add r2, sp, #0
ldr r3, =0x842e47 ; ldrb r3, [r3] ; strb r3, [r2, #7]
ldr r3, =0x842e1c ; ldrb r3, [r3] ; strb r3, [r2, #8]
bx lr
```

With a stock controller, byte 9 is not `0xBA`, so readers show no level rather than a wrong one.

## Known SHA-256

| What | SHA-256 |
|---|---|
| Official controller 1.09 payload (`.dat` without its 0x1C header) | `dfd443c77f831008aa453ee2c1c9cb6871c36f8947c548e61d4387fba0a9da70` |
| Controller zone `0x6000-0x19000`, 1.09 + patch | `01b2f36c92799c381b6b1c7ddb7d3f4f48813d43500f8fa993bfd4586c19bfe2` |
| Dongle bank A `0x6000-0x10000`, factory 1.00 | `311768ba6c29453bf7ecc8bfb9b9f3d6810d65cd9f0992ff1d201566b441a792` |
| Dongle bank A, 1.00 + patch | `e106d8b50c5541bca0e9f00d76b2db00f520a99b3ef56af15af318381772deb4` |

The official firmware comes from 8BitDo's update server (`http://dl.8bitdo.com:8080/firmware/select`, POST with headers
`Type: 75` and `Beta: 1`), as in the arithon-f toolkit.
