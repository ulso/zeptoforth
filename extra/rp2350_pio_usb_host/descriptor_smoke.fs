\ RP2350 core 1 PIO USB descriptor smoke-test helper for zeptoforth 1.16.5.
\
\ DANGER -- read this before using the words below:
\
\ * After a fresh board reset, load build/core1_pio_usb.bin byte-for-byte at
\   $20060000 FIRST.  This source file does not load or validate the BIN.
\ * Invoke launch-pio-usb-core1 exactly ONCE after that reset.  Never relaunch
\   core 1 while it is running; reset the board before another launch.
\ * Do NOT send a separate raw line beginning with disable-int before launch.
\   The compiled launch helper below contains the complete critical section,
\   including disabling the otherwise unused core 0 SIO FIFO interrupt again.
\ * The addresses below are pinned to the audited ABI-v2 descriptor image.
\   Rebuilds must be checked against core1_pio_usb.elf before using this file.
\ * A request is published in this mandatory order: command, DMB, command_seq,
\   DMB.  Do not reverse this order or poke command_seq by hand.
\ * Core 1 publishes every result field before completion_seq.  Interpret the
\   result only when completion_seq equals the sequence returned by
\   request-device-descriptor-8.

compile-to-ram

core1 import
interrupt import

\ Audited launch addresses for build/core1_pio_usb.bin.
$20060000 constant pio-usb-core1-vector-table
$200604C0 constant pio-usb-core1-entry
$20082000 constant pio-usb-core1-rstack
25 constant pio-usb-sio-fifo-irq

\ Audited ABI v2 shared block (128 bytes at $20065D00..$20065D7F).
$20065D00 constant pio-usb-core1-shared

pio-usb-core1-shared $00 + constant pio-usb-magic
pio-usb-core1-shared $04 + constant pio-usb-abi-version
pio-usb-core1-shared $08 + constant pio-usb-image-kind
pio-usb-core1-shared $0C + constant pio-usb-phase
pio-usb-core1-shared $10 + constant pio-usb-heartbeat
pio-usb-core1-shared $14 + constant pio-usb-fault
pio-usb-core1-shared $18 + constant pio-usb-clock-hz
pio-usb-core1-shared $1C + constant pio-usb-frame-count
pio-usb-core1-shared $20 + constant pio-usb-line-state
pio-usb-core1-shared $24 + constant pio-usb-root-event
pio-usb-core1-shared $28 + constant pio-usb-root-ints
pio-usb-core1-shared $2C + constant pio-usb-root-connected
pio-usb-core1-shared $30 + constant pio-usb-root-full-speed
pio-usb-core1-shared $34 + constant pio-usb-resource-busy
pio-usb-core1-shared $38 + constant pio-usb-pio-ctrl-before
pio-usb-core1-shared $3C + constant pio-usb-dma-ctrl-before
pio-usb-core1-shared $40 + constant pio-usb-pio-ctrl
pio-usb-core1-shared $44 + constant pio-usb-dma-ctrl
pio-usb-core1-shared $48 + constant pio-usb-command-seq
pio-usb-core1-shared $4C + constant pio-usb-command
pio-usb-core1-shared $50 + constant pio-usb-completion-seq
pio-usb-core1-shared $54 + constant pio-usb-command-status
pio-usb-core1-shared $58 + constant pio-usb-command-phase
pio-usb-core1-shared $5C + constant pio-usb-command-deadline-frame
pio-usb-core1-shared $60 + constant pio-usb-descriptor-length
pio-usb-core1-shared $64 + constant pio-usb-endpoint-complete
pio-usb-core1-shared $68 + constant pio-usb-endpoint-error
pio-usb-core1-shared $6C + constant pio-usb-endpoint-stalled
pio-usb-core1-shared $70 + constant pio-usb-descriptor

$43505531 constant pio-usb-abi-magic       \ "CPU1"
2 constant pio-usb-abi-v2
2 constant pio-usb-frame-image
4 constant pio-usb-running-phase
1 constant get-device-descriptor-8-command
0 constant pio-usb-command-idle
1 constant pio-usb-command-busy
2 constant pio-usb-command-ok
$80 constant pio-usb-command-error-invalid
$81 constant pio-usb-command-error-not-connected
$82 constant pio-usb-command-error-not-full-speed
$83 constant pio-usb-command-error-endpoint-open
$84 constant pio-usb-command-error-setup-start
$85 constant pio-usb-command-error-data-start
$86 constant pio-usb-command-error-status-start
$87 constant pio-usb-command-error-transfer
$88 constant pio-usb-command-error-stall
$89 constant pio-usb-command-error-timeout
$8A constant pio-usb-command-error-disconnect
$8B constant pio-usb-command-error-descriptor

0 constant pio-usb-command-phase-idle
1 constant pio-usb-command-phase-reset
2 constant pio-usb-command-phase-recovery
3 constant pio-usb-command-phase-setup
4 constant pio-usb-command-phase-data
5 constant pio-usb-command-phase-status
6 constant pio-usb-command-phase-complete
$80 constant pio-usb-command-phase-error

variable pio-usb-core1-launched
false pio-usb-core1-launched !

: x-pio-usb-core1-already-launched ( -- )
  ." core 1 is already running; reset the board before relaunching" cr
;

: x-pio-usb-core1-not-running ( -- )
  ." the audited ABI-v2 PIO USB core 1 image is not running" cr
;

: x-pio-usb-command-in-flight ( -- )
  ." a core 1 command is still in flight" cr
;

: pio-usb-core1-abi-v2? ( -- flag )
  pio-usb-magic @ pio-usb-abi-magic =
  pio-usb-abi-version @ pio-usb-abi-v2 = and
  pio-usb-image-kind @ pio-usb-frame-image = and
;

: pio-usb-core1-running? ( -- flag )
  pio-usb-core1-abi-v2?
  pio-usb-phase @ pio-usb-running-phase = and
  pio-usb-fault @ 0= and
;

\ A changing heartbeat also catches an image launched before this helper was
\ loaded, so the RAM-local guard is not the only protection against relaunch.
: pio-usb-core1-alive? ( -- flag )
  pio-usb-heartbeat @ 2 ms pio-usb-heartbeat @ <>
;

: launch-pio-usb-core1 ( -- )
  pio-usb-core1-launched @ pio-usb-core1-alive? or
  triggers x-pio-usb-core1-already-launched
  true pio-usb-core1-launched !
  disable-int
  pio-usb-sio-fifo-irq NVIC_ICER_CLRENA!
  pio-usb-sio-fifo-irq NVIC_ICPR_CLRPEND!
  pio-usb-core1-entry pio-usb-core1-rstack
  pio-usb-core1-vector-table launch-core1
  pio-usb-sio-fifo-irq NVIC_ICER_CLRENA!
  pio-usb-sio-fifo-irq NVIC_ICPR_CLRPEND!
  dmb dsb isb
  enable-int
;

\ Return the sequence that the caller must wait for in completion_seq.
: request-device-descriptor-8 ( -- seq )
  pio-usb-core1-running? averts x-pio-usb-core1-not-running
  pio-usb-command-seq @ pio-usb-completion-seq @ =
  averts x-pio-usb-command-in-flight
  pio-usb-command-seq @ 1+
  get-device-descriptor-8-command pio-usb-command !
  dmb
  dup pio-usb-command-seq !
  dmb
;

: pio-usb-request-complete? ( seq -- flag )
  pio-usb-completion-seq @ = dmb
;

: pio-usb-command-ok? ( seq -- flag )
  pio-usb-request-complete?
  pio-usb-command-status @ pio-usb-command-ok = and
;

\ Wait at most one second, leaving native USB time to run between polls.
: wait-pio-usb-request ( seq -- completed? )
  1000 0 do
    dup pio-usb-request-complete? if drop true unloop exit then
    1 ms
  loop
  drop false
;

\ Publish the only ABI-v2 command and report whether it completed successfully.
: descriptor-smoke ( -- completed-ok? )
  request-device-descriptor-8 dup wait-pio-usb-request
  if pio-usb-command-ok? else drop false then
;

: pio-usb-status. ( -- )
  cr ." magic=$" pio-usb-magic @ h.8
  ."  abi=$" pio-usb-abi-version @ h.8
  ."  image=$" pio-usb-image-kind @ h.8
  ."  phase=$" pio-usb-phase @ h.8
  cr ." heartbeat=$" pio-usb-heartbeat @ h.8
  ."  fault=$" pio-usb-fault @ h.8
  ."  clock=$" pio-usb-clock-hz @ h.8
  ."  frame=$" pio-usb-frame-count @ h.8
  cr ." line=$" pio-usb-line-state @ h.8
  ."  event=$" pio-usb-root-event @ h.8
  ."  ints=$" pio-usb-root-ints @ h.8
  ."  connected=$" pio-usb-root-connected @ h.8
  ."  full-speed=$" pio-usb-root-full-speed @ h.8
  cr ." resource-busy=$" pio-usb-resource-busy @ h.8
  ."  pio-before=$" pio-usb-pio-ctrl-before @ h.8
  ."  dma-before=$" pio-usb-dma-ctrl-before @ h.8
  cr ." pio=$" pio-usb-pio-ctrl @ h.8
  ."  dma=$" pio-usb-dma-ctrl @ h.8 cr
;

: pio-usb-descriptor. ( -- )
  ." descriptor="
  pio-usb-descriptor-length @ 16 min
  0 do pio-usb-descriptor i + c@ h.2 space loop
  cr
;

: pio-usb-results. ( -- )
  cr ." command-seq=$" pio-usb-command-seq @ h.8
  ."  command=$" pio-usb-command @ h.8
  ."  completion-seq=$" pio-usb-completion-seq @ h.8
  cr ." status=$" pio-usb-command-status @ h.8
  ."  command-phase=$" pio-usb-command-phase @ h.8
  ."  deadline-frame=$" pio-usb-command-deadline-frame @ h.8
  cr ." descriptor-length=$" pio-usb-descriptor-length @ h.8
  ."  ep-complete=$" pio-usb-endpoint-complete @ h.8
  ."  ep-error=$" pio-usb-endpoint-error @ h.8
  ."  ep-stalled=$" pio-usb-endpoint-stalled @ h.8 cr
  pio-usb-descriptor.
;

: pio-usb-dump ( -- )
  pio-usb-status. pio-usb-results.
;
