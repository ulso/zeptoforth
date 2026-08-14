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

The current sources implement ABI v4, including persistent endpoint sessions
and bounded non-control transfers.  The earlier ABI-v2 descriptor smoke and
ABI-v3 control/enumeration milestones are retained below as reproducible
historical checkpoints; their images and helpers must not be mixed with ABI
v4.

## Verified historical ABI-v2 descriptor-smoke checkpoint

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
and `descriptor_smoke.fs` must not be mixed with the current ABI-v4 sources.

## Verified historical ABI-v3 generic-control milestone

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

Historical ABI-v3 reference runtime artifact:

```text
core1_pio_usb.bin
size:    17664 bytes (0x4500)
SHA256:  07f806f909f31a7c17d748bea87ac453b222c7b0a8a93a98b67f121cce8e3308
```

This proved the generic endpoint-zero transport on the Cytron/BleuIO setup.

## Verified historical ABI-v3 Forth-owned root enumeration milestone

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
This historical milestone did not open the discovered endpoints or implement
CDC/ACM data transfers.  The matching ABI-v3 checkpoint is preserved in commit
`a9a57a69`.

## Verified current ABI-v4 CDC-ACM milestone

ABI v4 retains the Forth-owned control and enumeration policy from ABI v3 and
adds generic persistent endpoint open/close operations plus bounded IN and OUT
transfers.  Core 1 owns each open endpoint's transport state and data toggle;
Forth on core 0 discovers the class topology, issues the CDC class requests,
and decides which endpoints to open.

The three definitions-only helpers compiled successfully on the target:

- `enumeration_v4.fs` exposes the ABI-v4 launch, control, endpoint-lifecycle,
  and transfer primitives;
- `full_enumeration_v4.fs` performs and validates complete root enumeration,
  retaining stable descriptor copies; and
- `cdc_acm_v4.fs` finds a valid CDC Union/data-interface pair, applies
  `SET_CONTROL_LINE_STATE` and 115200 8N1 `SET_LINE_CODING`, opens only the
  bulk data endpoints, and provides a bounded `AT\r\n` smoke test.

On 2026-08-14, the ABI-v4 image and all three helpers were exercised on a
Cytron MOTION 2350 Pro with a directly attached BleuIO.  It enumerated as
VID:PID `2DCF:6002`, configuration 1, with CDC control interface 0 and CDC data
interface 1.  The selected endpoints were bulk OUT `0x02` and bulk IN `0x81`,
both with MPS 64.

Repeated `AT\r\n` transfers returned the optional command echo followed by a
complete `OK\r\n` line.  An empty bulk-IN poll completed with the nonfatal USB
transfer status `TIMEOUT` (`0x8B`) and actual length 0; a subsequent `AT`
exchange succeeded without reopening the endpoints.  Later repeated testing
showed that a physical close followed by reopen in the same configured USB
session is not reliable: closing currently discards the host endpoint's data
toggle while the device retains its toggle.  A reopened IN endpoint can then
discard the first response packet.  Keep the data endpoints open for the whole
enumerated port epoch, and close them only at final teardown.  Before another
open, reset the host port and enumerate again.  Throughout the verified runs,
the native Zeptoforth USB CDC console remained responsive and core 1 reported
`fault = 0`.

The current `cdc_acm_v4.fs` also exposes bounded, serialized byte-stream
operations for higher protocol layers:

- `cdc-acm-v4-write-all` splits arbitrary caller buffers into mailbox-sized
  transfers and safely resumes partial OUT completions;
- `cdc-acm-v4-read` performs one bounded IN poll and copies data out of the
  ephemeral shared mailbox before releasing its locks; and
- `with-cdc-acm-v4-transaction` lets one higher-level operation own the CDC
  class and ABI mailbox continuously across a write and its response reads.

## Verified current BleuIO Forth command milestone

`bleuio.fs` is a definitions-only Forth module built on the generic CDC byte
stream.  It adds bounded CRLF framing, optional echo handling, the BleuIO
verbose `C`/`A`/`R`/`E` response protocol, raw-response retention, numeric
BleuIO error reporting, and atomic command/bootstrap/lifecycle operations.
Loading the file performs no USB I/O.

On 2026-08-14, the module was compiled on the running Cytron target without
reloading the already verified core-1 image or descriptor state.  Its atomic
bootstrap successfully issued `ATV1`, `ATE0`, `ATA0`, `ATEW0`, and `ATDS0`.
`ATI` then identified the attached device as a BleuIO Pro running firmware
`1.0.5.6`, and `AT+GAPSTATUS` reported the dual GAP role.  Both responses had
matching command indices and `A.err = 0`.

An intentionally invalid command returned `false`, retained BleuIO error 7
(`Invalid command or wrong argument count`), and did not lose stream
synchronization.  A subsequent `AT` succeeded.  `bleuio::close` then closed
both bulk endpoints; the ABI mailbox was idle, core 1 remained alive, and
`fault`, CDC terminal-timeout, and BleuIO resync-required were all zero.

`bleuio_scan.fs` extends that same synchronous stream owner with bounded
`AT+GAPSCAN=<seconds>` collection.  In the hardware acceptance run, a
two-second scan retained 28 complete `S` records, including three named
`HibouAIR`, with zero dropped, oversized, unknown, or truncated records.  It
consumed the matching nested scan-end record
`{"SE":21,"evt":{"action":"scan completed"}}`; an ordinary `AT` succeeded
immediately afterward.  The resync latch and core-1 fault remained zero.

Both BleuIO layers are deliberately synchronous and do not yet run a
background receive task or retain arbitrary unsolicited events.  If a
transport/framing timeout makes command boundaries uncertain,
`resync-required?` latches true across ordinary logical cleanup.  Do not
publish another command; reset the target, load the matching runtime state,
and enumerate again.

Current reference runtime artifact:

```text
core1_pio_usb.bin
size:    19120 bytes (0x4ab0)
SHA256:  637f9b88b15b344a2147203eaa373d571224c6ff1eaa4e56d215fa9d780f50cf
```

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

## Current ABI-v4 SRAM layout

`src/rp2350_1core/config.s` lowers Zeptoforth's `ram_end` to `0x20060000`.
The upper 136 KiB is reserved for the freestanding image:

```text
Zeptoforth RAM end:  0x20060000
core 1 reservation: 0x20060000..0x20082000
vector table:        0x20060000
entry instruction:   0x200604c0
initialized copy:    0x20060000..0x20064ab0
BSS:                 0x20064ab0..0x2006653c
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

The address block above and `enumeration_v4.fs` are pinned to the current
ABI-v4 BIN hash.  `enumeration_v3.fs` and `full_enumeration_v3.fs` belong to the
historical ABI-v3 artifact; `descriptor_smoke.fs` belongs to ABI v2.  None may
be mixed across ABI versions.  Any rebuild that changes the current hash must
re-derive and verify `core1_entry`, `__core1_copy_end`, `__bss_end__`,
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

## Running the current ABI-v4 checkpoint

The image is a raw SRAM payload, not firmware that can be flashed by itself.
`enumeration_v4.fs`, `full_enumeration_v4.fs`, `cdc_acm_v4.fs`, and
`bleuio.fs` only define words and initialize RAM-local helper state.  Loading
them does not launch core 1, reset the port, enumerate a device, issue a class
request, or open an endpoint.

The safe sequence is:

1. Reset into the carved `rp2350_1core` Zeptoforth baseline.
2. Confirm `ram-end` is `0x20060000`.
3. Load the matching 19120-byte BIN at `0x20060000` while both cores are
   stopped, read back through `0x20064ab0`, and require the SHA above.
4. Load `enumeration_v4.fs`, `full_enumeration_v4.fs`, and `cdc_acm_v4.fs`, in
   that order, over the native USB console.  Load `bleuio.fs` afterward if the
   high-level BleuIO command API is wanted, followed by `bleuio_scan.fs` for
   bounded GAP scanning.
5. Invoke `launch-pio-usb-core1-v4` exactly once.
6. Require ABI 4, phase 4, a changing heartbeat, `fault=0`,
   `connected=1`, `full-speed=1`, and no resource conflict.
7. Run the complete root enumeration.
8. Open the discovered CDC ACM function once, then run all bounded commands and
   scans in the same endpoint session.
9. Close the CDC data endpoints only when completely finished.  With the
   current transport, reset the host port and enumerate again before a later
   open; physical close/reopen in one configured port epoch is not supported.

The endpoint-zero descriptor smoke remains available with:

```forth
: show-v4-smoke
  pio-usb-get-device-descriptor-8-v4
  0 do dup i + c@ h.2 space loop drop
;
show-v4-smoke
```

The expected bytes are `12 01 00 02 02 02 00 08`.

The verified enumeration and CDC sequence is invoked explicitly:

```forth
hex
pio-usb-enumerate-root-control-v4 .s
decimal
open-cdc-acm-v4
cdc-acm-v4-at-smoke
close-cdc-acm-v4
```

The four returned values are `( address configuration vid pid )`; for the
tested BleuIO, the stack contains `1 1 2DCF 6002`.  On success,
`pio-usb-v4-enumeration-stage` is 9,
`pio-usb-v4-enumeration-complete?` is true, and the stable descriptor copies
and normalized interface/endpoint records can be inspected without reusing the
shared control buffer.  The CDC helper leaves the interrupt notification
endpoint closed; its smoke sends exactly `41 54 0D 0A` and requires a complete
`OK\r\n` response line within bounded polls and a 256-byte local buffer.

With `bleuio.fs` loaded, the verified high-level sequence is:

```forth
bleuio::open .                 \ true on successful CDC open and bootstrap
bleuio::bootstrap-complete? .  \ true
bleuio::ati .                  \ true
bleuio::.response
bleuio::gap-status .           \ true
bleuio::.response
```

`bleuio::command ( data length -- success? )` accepts a command without CR or
LF and appends CRLF itself.  Protocol-level `ERROR` or a nonzero verbose
`A.err` returns false and leaves the raw response, `result@`, and
`error-code@` available for inspection.  Transport, framing, timeout, and
capacity failures raise.  The convenience words currently include `at`,
`ati`, `central`, `peripheral`, and `gap-status`.

With `bleuio_scan.fs` loaded, continue in that same open session to test a
finite scan, then close only after all work is complete:

```forth
2 bleuio::gap-scan .          \ true after a natural two-second scan
bleuio::.scan-results         \ print the retained raw S records
bleuio::scan-count@ .
bleuio::scan-dropped@ .
bleuio::scan-end@ type cr     \ matching SE / "scan completed" record
bleuio::at .                  \ must still succeed immediately afterward
bleuio::close
```

`gap-scan ( seconds -- success? )` accepts 1 through 30 seconds and owns the
BleuIO command lock and CDC transaction until the matching scan-end record has
been consumed.  It retains at most 32 complete raw `S` records of at most 256
bytes each; further records are drained and counted rather than blocking USB.
The accessors `scan-result@`, `scan-count@`, `scan-dropped@`,
`scan-oversize@`, `scan-unknown-count@`, `scan-end@`, and
`scan-last-unknown@` expose the bounded results and diagnostics.  Inspect them
after `gap-scan` has returned; no consistent snapshot is promised while a scan
is still running.

`request-scan-stop` is intended for another Forth task while `gap-scan` is
blocked.  It requests one raw ETX byte and the scan owner continues draining
until the matching `SE`; a clean controlled stop returns false with
`scan-aborted?` true and leaves the stream usable.  If the matching end record
cannot be recovered within the bounded drain period, the operation raises and
latches `bleuio::resync-required?`; reset and re-enumerate before issuing any
more commands.

Two different timeout cases must not be confused.  A completed endpoint IN
request with USB transfer status `TIMEOUT` (`0x8B`) and actual length 0 is a
normal, nonfatal empty poll; the endpoint session and data toggle remain valid.
A core-0 host-side mailbox wait that expires instead raises
`x-pio-usb-v4-timeout`, leaving request/completion ownership unknown.  That is
terminal: do not issue cleanup or another command, and reset or power-cycle the
complete target first.  Also reset if the heartbeat stops or the native console
misbehaves.

## Running the historical ABI-v3 checkpoints

The exact ABI-v3 image, helpers, and procedure are preserved in commit
`a9a57a69`.  They establish the generic endpoint-zero transport and complete
Forth-owned root enumeration described above, but do not support persistent
CDC data endpoints.  Do not load an ABI-v3 helper against the current ABI-v4
image.

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

## Current limitations

The verified ABI-v4 path is still a single directly attached, full-speed root
device experiment.  It does not implement hubs, low-speed devices, VBUS power
control, hot-plug policy above the core-1 transport, or CDC notification
handling.  The class helper intentionally accepts only a validated CDC ACM
control interface with one Union-linked alternate-zero data interface and
exactly one bulk IN plus one bulk OUT endpoint.  General multi-device and
multi-class policy remains future work.  The BleuIO module now includes
bounded synchronous GAP scanning, but not a background receive owner, general
unsolicited-event retention, or the broader JavaScript-library-equivalent
command vocabulary.  Those remain future layers.

## Upstream

Pico-PIO-USB is included as an unmodified Git submodule pinned to commit
`5a37a66dc5d3fbe0ef3cdbeda923a757440f984f`.  Its MIT license and copyright
notices remain in the submodule.
