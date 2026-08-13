# RP2350 core 1 PIO USB host experiment

This directory contains an experimental, freestanding PIO USB host image for
the `rp2350_1core` Zeptoforth platform.  It is currently specific to a Cytron
MOTION 2350 Pro with a directly attached full-speed device on its USB-A port.

The design deliberately separates responsibilities:

- core 1 owns the timing-sensitive PIO USB transport and runs entirely from
  SRAM;
- core 0 keeps the native USB Zeptoforth console and owns enumeration,
  descriptor parsing, and USB class policy;
- runtime requests and results cross a fixed shared-SRAM mailbox.  The SIO FIFO
  is used only for the RP2350 core-launch handshake, and its IRQ is disabled
  again before normal execution resumes on core 0.

This is research code, not yet a general USB host implementation.

The current sources implement ABI v3.  The earlier ABI-v2 descriptor smoke is
retained below as a reproducible historical checkpoint; it should not be mixed
with the current image or helper.

## Verified ABI-v2 descriptor-smoke checkpoint

The ABI v2 image implements one bounded command: a 50 ms bus reset followed by
a request for the first eight bytes of the device descriptor.  On 2026-08-13,
with a BleuIO dongle attached, the command completed without transport errors
or stalls and returned:

```text
12 01 00 02 02 02 00 08
```

This describes a USB 2.0 CDC/ACM-class device with an 8-byte endpoint zero.
Core 1 and the native USB Forth console then ran concurrently for more than one
hour with a continuously advancing 1 kHz heartbeat and `fault = 0`.

Reference runtime artifact:

```text
core1_pio_usb.bin
size:    17052 bytes (0x429c)
SHA256:  42875e20f468b57b13ce0578756ea5603b5fb581622bd0c6f3543c97b7de2a94
```

The ELF SHA may vary with debug paths and metadata.  The loadable BIN hash
identifies the exact artifact used for this checkpoint.
The matching ABI-v2 sources are preserved in commit `87436954`; that artifact
and `descriptor_smoke.fs` must not be mixed with the current ABI-v3 sources.

## Verified generic-control milestone

ABI v3 replaces the scripted descriptor command with two bounded transport
operations: `PORT_RESET` and a generic USB control transfer.  Core 1 owns the
SETUP, optional DATA, and STATUS stages; Forth on core 0 supplies the request
and owns enumeration policy.  The fixed mailbox advertises a 256-byte control
buffer and rejects stale port epochs, invalid addresses, endpoint-zero packet
sizes, and oversized requests before changing bus state.

On 2026-08-14, the ABI-v3 image was loaded into SRAM and read back byte for
byte on a Cytron MOTION 2350 Pro.  With a directly attached BleuIO dongle,
Forth issued `PORT_RESET` followed by the generic control request
`80 06 00 01 00 00 08 00`.  It returned the same device-descriptor prefix:

```text
12 01 00 02 02 02 00 08
```

The completion status was `OK`, the actual length was 8, the endpoint complete
mask was 1, and the error, stall, failure-detail, and core-fault fields were all
zero.  The native USB Forth console remained responsive and the core 1
heartbeat continued to advance.

Current reference runtime artifact:

```text
core1_pio_usb.bin
size:    17664 bytes (0x4500)
SHA256:  07f806f909f31a7c17d748bea87ac453b222c7b0a8a93a98b67f121cce8e3308
```

This proves the generic endpoint-zero transport on the current Cytron/BleuIO
setup.

## Verified Forth-owned root enumeration milestone

On 2026-08-14, the definitions-only `full_enumeration_v3.fs` module used the
same generic ABI-v3 transport to perform the complete standard control path:

1. reset the root port and fetch the first eight device-descriptor bytes;
2. assign address 1 and observe the address recovery delay;
3. fetch and validate the complete 18-byte device descriptor;
4. fetch the 9-byte configuration prefix and then its declared full length;
5. bounds-walk and validate the complete descriptor tree;
6. select its advertised configuration; and
7. confirm the selection with `GET_CONFIGURATION`.

The directly attached BleuIO enumerated as address 1, configuration 1,
VID:PID `2DCF:6002`.  Its configuration was 67 bytes and declared two
interfaces.  The parser found two unique interfaces, two interface
descriptors, three endpoint descriptors, and four class-specific descriptors:

```text
interface 0: CDC control, interrupt IN  0x83, MPS 64
interface 1: CDC data,    bulk IN       0x81, MPS 64
                          bulk OUT      0x02, MPS 64
```

The helper finished at stage 9 with its complete flag set and no exception.
The core-1 command status was `OK`; endpoint error, endpoint stall, and core
fault were all zero, and the native USB Forth console remained responsive.
This milestone does not open the discovered endpoints or implement CDC/ACM
data transfers yet.

## Hardware and resource contract

The current image assumes:

- `clk_sys` is exactly 150 MHz;
- D+ is GP24 and D- is GP25;
- all 32 instruction words of PIO0 are exclusively available;
- PIO0 SM0 is TX, SM1 is RX, and SM2 is edge/EOP detection;
- DMA channel 0 is exclusively available;
- core 1 SysTick is exclusively available;
- TIMER0's microsecond counter is running.

It does not control VBUS and must not treat GP18 as a VBUS-enable pin.

The hardware preflight detects enabled SM0-SM2 and an actively busy DMA0.  It
cannot detect an idle software claim, SM3 use, or existing PIO instructions.
Start only from a clean reset where no other PIO or DMA code has run.

## Current ABI-v3 SRAM layout

`src/rp2350_1core/config.s` lowers Zeptoforth's `ram_end` to `0x20060000`.
The upper 136 KiB is reserved for the freestanding image:

```text
Zeptoforth RAM end:  0x20060000
core 1 reservation: 0x20060000..0x20082000
vector table:        0x20060000
entry instruction:   0x200604c0
initialized copy:    0x20060000..0x20064500
BSS:                 0x20064500..0x20065f74
shared ABI:          0x20080e00..0x20081000
core 1 stack:        0x20081000..0x20082000
```

All executable code, constants, mutable data, vectors, and the stack must stay
in this SRAM region.  Core 1 must never call or read QSPI/XIP while core 0 may
write the Forth flash dictionary.

## Parent Zeptoforth prerequisite

The core 1 payload is only safe with the carved `rp2350_1core` parent built
from this branch.  Build and flash that baseline before loading the SRAM image:

```sh
make rp2350_1core
```

This produces `obj/zeptoforth.rp2350_1core.uf2`.  The baseline used for the
verified run was 265216 bytes with SHA256
`4eec2fc4cf1c13482ce3feb1452274d27104a96eb8a4c671c909ee8e21cc0fcd`.
After flashing it, load `src/rp2350_1core/forth/setup_full_usb.fs` with the
normal Zeptoforth code loader to reproduce the native USB CDC console used in
the coexistence test.  The carved baseline and the USB setup are separate from
the raw core 1 payload; rebuilding or flashing only the latter is insufficient.

## Build

Initialize the pinned upstream dependency and build.  The reference artifact
was produced with Pico SDK 2.2.0, Arm GNU Toolchain 15.3.Rel1 (GCC 15.3.1,
binutils 2.45.1), and CMake 4.4.2.  Other toolchain or SDK versions are not yet
verified and may produce a different, otherwise valid BIN:

```sh
git submodule update --init extra/rp2350_pio_usb_host/Pico-PIO-USB

env PICO_SDK_PATH=/path/to/pico-sdk \
  cmake -S extra/rp2350_pio_usb_host \
        -B extra/rp2350_pio_usb_host/build \
        -DPICO_PLATFORM=rp2350-arm-s \
        -DPICO_BOARD=pico2 \
        -DCMAKE_BUILD_TYPE=MinSizeRel

cmake --build extra/rp2350_pio_usb_host/build \
  --target core1_pio_usb

shasum -a 256 \
  extra/rp2350_pio_usb_host/build/core1_pio_usb.bin
```

The small original heartbeat-only image remains available with:

```sh
make -C extra/rp2350_pio_usb_host inspect
```

The address block above and `enumeration_v3.fs` are pinned to the current
ABI-v3 BIN hash.  `descriptor_smoke.fs` is instead pinned to the historical
ABI-v2 artifact and must not be used with ABI v3.  Any rebuild that changes the
current hash must re-derive and verify `core1_entry`, `__core1_copy_end`,
`core1_shared`, and `__core1_shared_end`; the linker must fail rather than move
or resize the fixed `0x20080e00..0x20081000` mailbox.  Before running a new
artifact, confirm that it has no undefined symbols or relocations and that
every allocatable/loadable section lies inside the reserved SRAM range:

```sh
arm-none-eabi-nm -u extra/rp2350_pio_usb_host/build/core1_pio_usb.elf
arm-none-eabi-nm -n extra/rp2350_pio_usb_host/build/core1_pio_usb.elf
arm-none-eabi-readelf -l -r \
  extra/rp2350_pio_usb_host/build/core1_pio_usb.elf
```

## Running the current ABI-v3 checkpoint

The image is a raw SRAM payload, not firmware that can be flashed by itself.
`enumeration_v3.fs` and `full_enumeration_v3.fs` only define words; loading
either file does not launch core 1, reset the port, or contact a USB device.

The safe sequence is:

1. Reset into the carved `rp2350_1core` Zeptoforth baseline.
2. Confirm `ram-end` is `0x20060000`.
3. Load the matching 17664-byte BIN at `0x20060000` while both cores are
   stopped, read back through `0x20064500`, and require the SHA above.
4. Load `enumeration_v3.fs` and `full_enumeration_v3.fs` over the native USB
   console.
5. Invoke `launch-pio-usb-core1-v3` exactly once.
6. Require ABI 3, phase 4, a changing heartbeat, `fault=0`,
   `connected=1`, `full-speed=1`, and no resource conflict.
7. Only then issue the bounded reset and generic control-transfer smoke.
8. After the smoke succeeds, run the full control-only root enumeration.

The exact first milestone can be displayed with:

```forth
: show-v3-smoke
  pio-usb-get-device-descriptor-8-v3
  0 do dup i + c@ h.2 space loop drop
;
show-v3-smoke
```

The expected bytes are `12 01 00 02 02 02 00 08`.  Do not publish a second
command after a timeout or stopped heartbeat; reset the complete target first.

The verified complete enumeration is invoked explicitly; it is never run while
either helper file is loaded:

```forth
hex
pio-usb-enumerate-root-control-v3 .s
decimal
```

The four returned values are `( address configuration vid pid )`; for the
tested BleuIO, the stack contains `1 1 2DCF 6002`.  On success,
`pio-usb-v3-enumeration-stage` is 9,
`pio-usb-v3-enumeration-complete?` is true, and the stable descriptor copies
and normalized interface/endpoint records can be inspected without reusing the
shared control buffer.  The next development step is opening the parsed CDC
endpoints and transferring CDC/ACM data while core 0 retains class policy.

## Running the historical ABI-v2 checkpoint

The image is a raw SRAM payload, not firmware that can be flashed by itself.
The exact interactive helper words and ABI offsets are in
`descriptor_smoke.fs`.  The safe sequence is:

1. Reset into the carved `rp2350_1core` Zeptoforth baseline.
2. Confirm `ram-end` is `0x20060000`.
3. Load and byte-verify the BIN at `0x20060000` while core 1 is stopped.
4. Compile the launch helper while interrupts are enabled.
5. Launch exactly once and wait for phase `RUNNING`.
6. Publish the descriptor command only after startup has cleared shared RAM.
7. Require completion within one second from core 0.

After the audited BIN has been loaded and verified, load the helper source and
run the checkpoint as separate interpreter commands:

```forth
launch-pio-usb-core1
pio-usb-status.
descriptor-smoke .
pio-usb-results.
```

Wait until `pio-usb-status.` reports the ABI-v2 magic, phase `4`, a changing
heartbeat, `connected=1`, `full-speed=1`, and `fault=0` before invoking
`descriptor-smoke`.  A successful smoke prints `-1`; `pio-usb-results.` should
then show status `2`, command phase `6`, descriptor length `8`, no endpoint
error/stall, and descriptor bytes beginning `12 01`.

Do not paste a raw interpreter line beginning with `disable-int`: native USB
may be starved before the rest of that line has arrived.  Compile the complete
launch word first and invoke only the finished word.

A launch must never be repeated without a full target reset.  Reset before
loading another image or using PIO0, DMA0, GP24, or GP25 from Forth.

The mailbox timeouts cannot recover if an upstream synchronous PIO/DMA wait
loop itself hangs.  If the heartbeat stops, the native console misbehaves, or
completion is absent after one second, perform a full reset or power cycle and
do not publish another command.

## Upstream

Pico-PIO-USB is included as an unmodified Git submodule pinned to commit
`5a37a66dc5d3fbe0ef3cdbeda923a757440f984f`.  Its MIT license and copyright
notices remain in the submodule.
