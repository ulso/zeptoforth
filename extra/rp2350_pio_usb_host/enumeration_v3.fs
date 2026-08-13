\ RP2350 core 1 PIO USB ABI-v3 helper for zeptoforth 1.16.5.
\
\ This file only defines words.  It does not load the raw image, launch core 1,
\ reset the USB port, or enumerate a device when the file is loaded.
\
\ DANGER:
\
\ * Start from a fresh board reset.  Load the matching core1_pio_usb.bin at
\   $20060000 and byte-verify it before calling launch-pio-usb-core1-v3.
\ * The addresses below are pinned to the ABI-v3 image whose initialized copy
\   ends at $20064500.  Re-derive them from the ELF after every changed build.
\   Reference BIN SHA-256:
\   07f806f909f31a7c17d748bea87ac453b222c7b0a8a93a98b67f121cce8e3308
\ * Launch core 1 exactly once.  Reset the board before loading or launching a
\   different image, or before reusing PIO0, DMA0, GP24, or GP25.
\ * Request fields and any OUT payload are written before request_seq is
\   published.  Response fields and any IN payload are read only after the
\   matching completion_seq has been observed.
\ * The control-data address returned by pio-usb-control-in-v3 remains valid,
\   but its contents are overwritten by the next control transfer.
\ * The command helpers are synchronous and bounded, but are not re-entrant.

compile-to-ram

core1 import
interrupt import

\ Audited image and launch addresses.
$20060000 constant pio-usb-core1-v3-vector-table
$200604C0 constant pio-usb-core1-v3-entry
$20064500 constant pio-usb-core1-v3-copy-end
$20080E00 constant pio-usb-core1-v3-shared
$20082000 constant pio-usb-core1-v3-rstack
25 constant pio-usb-core1-v3-sio-fifo-irq

\ ABI v3 telemetry and metadata.
pio-usb-core1-v3-shared $00 + constant pio-usb-v3-magic
pio-usb-core1-v3-shared $04 + constant pio-usb-v3-abi-version
pio-usb-core1-v3-shared $08 + constant pio-usb-v3-image-kind
pio-usb-core1-v3-shared $0C + constant pio-usb-v3-phase
pio-usb-core1-v3-shared $10 + constant pio-usb-v3-heartbeat
pio-usb-core1-v3-shared $14 + constant pio-usb-v3-fault
pio-usb-core1-v3-shared $18 + constant pio-usb-v3-clock-hz
pio-usb-core1-v3-shared $1C + constant pio-usb-v3-frame-count
pio-usb-core1-v3-shared $20 + constant pio-usb-v3-line-state
pio-usb-core1-v3-shared $24 + constant pio-usb-v3-root-event
pio-usb-core1-v3-shared $28 + constant pio-usb-v3-root-ints
pio-usb-core1-v3-shared $2C + constant pio-usb-v3-root-connected
pio-usb-core1-v3-shared $30 + constant pio-usb-v3-root-full-speed
pio-usb-core1-v3-shared $34 + constant pio-usb-v3-resource-busy
pio-usb-core1-v3-shared $38 + constant pio-usb-v3-pio-ctrl-before
pio-usb-core1-v3-shared $3C + constant pio-usb-v3-dma-ctrl-before
pio-usb-core1-v3-shared $40 + constant pio-usb-v3-pio-ctrl
pio-usb-core1-v3-shared $44 + constant pio-usb-v3-dma-ctrl
pio-usb-core1-v3-shared $48 + constant pio-usb-v3-abi-bytes
pio-usb-core1-v3-shared $4C + constant pio-usb-v3-capabilities
pio-usb-core1-v3-shared $50 + constant pio-usb-v3-data-capacity
pio-usb-core1-v3-shared $54 + constant pio-usb-v3-port-epoch
pio-usb-core1-v3-shared $58 + constant pio-usb-v3-port-state

\ Core 0 request, published by writing request_seq last.
pio-usb-core1-v3-shared $5C + constant pio-usb-v3-request-seq
pio-usb-core1-v3-shared $60 + constant pio-usb-v3-command
pio-usb-core1-v3-shared $64 + constant pio-usb-v3-request-epoch
pio-usb-core1-v3-shared $68 + constant pio-usb-v3-device-address
pio-usb-core1-v3-shared $6C + constant pio-usb-v3-ep0-mps
pio-usb-core1-v3-shared $70 + constant pio-usb-v3-transfer-length
pio-usb-core1-v3-shared $74 + constant pio-usb-v3-setup

\ Core 1 response, published by writing completion_seq last.
pio-usb-core1-v3-shared $7C + constant pio-usb-v3-completion-seq
pio-usb-core1-v3-shared $80 + constant pio-usb-v3-command-status
pio-usb-core1-v3-shared $84 + constant pio-usb-v3-command-phase
pio-usb-core1-v3-shared $88 + constant pio-usb-v3-completion-epoch
pio-usb-core1-v3-shared $8C + constant pio-usb-v3-actual-length
pio-usb-core1-v3-shared $90 + constant pio-usb-v3-completion-frame
pio-usb-core1-v3-shared $94 + constant pio-usb-v3-deadline-frame
pio-usb-core1-v3-shared $98 + constant pio-usb-v3-endpoint-complete
pio-usb-core1-v3-shared $9C + constant pio-usb-v3-endpoint-error
pio-usb-core1-v3-shared $A0 + constant pio-usb-v3-endpoint-stalled
pio-usb-core1-v3-shared $A4 + constant pio-usb-v3-failure-detail
pio-usb-core1-v3-shared $100 + constant pio-usb-v3-data

\ Fixed ABI values.
$43505531 constant pio-usb-v3-abi-magic       \ "CPU1"
3 constant pio-usb-v3-abi
512 constant pio-usb-v3-abi-size
256 constant pio-usb-v3-control-data-size
2 constant pio-usb-v3-frame-image
4 constant pio-usb-v3-running-phase
1 constant pio-usb-v3-cap-port-reset
2 constant pio-usb-v3-cap-control-transfer
3 constant pio-usb-v3-required-capabilities
2 constant pio-usb-v3-port-active

0 constant pio-usb-v3-command-none
1 constant pio-usb-v3-command-port-reset
2 constant pio-usb-v3-command-control-transfer

0 constant pio-usb-v3-command-idle
1 constant pio-usb-v3-command-busy
2 constant pio-usb-v3-command-ok
$80 constant pio-usb-v3-command-error-invalid
$81 constant pio-usb-v3-command-error-stale-epoch
$82 constant pio-usb-v3-command-error-not-connected
$83 constant pio-usb-v3-command-error-not-full-speed
$84 constant pio-usb-v3-command-error-port-state
$85 constant pio-usb-v3-command-error-endpoint-open
$86 constant pio-usb-v3-command-error-setup-start
$87 constant pio-usb-v3-command-error-data-start
$88 constant pio-usb-v3-command-error-status-start
$89 constant pio-usb-v3-command-error-transfer
$8A constant pio-usb-v3-command-error-stall
$8B constant pio-usb-v3-command-error-timeout
$8C constant pio-usb-v3-command-error-disconnect

250 constant pio-usb-v3-reset-timeout-ms
500 constant pio-usb-v3-control-timeout-ms

variable pio-usb-core1-v3-launched
false pio-usb-core1-v3-launched !

: x-pio-usb-core1-v3-image-missing ( -- )
  ." the audited ABI-v3 core 1 image is not loaded" cr
;

: x-pio-usb-core1-v3-already-launched ( -- )
  ." core 1 is already running; reset before relaunching" cr
;

: x-pio-usb-core1-v3-not-running ( -- )
  ." the audited ABI-v3 PIO USB core 1 image is not running" cr
;

: x-pio-usb-v3-command-in-flight ( -- )
  ." a PIO USB core 1 command is already in flight" cr
;

: x-pio-usb-v3-timeout ( -- )
  ." timed out waiting for the PIO USB core 1 response" cr
;

: x-pio-usb-v3-command-failed ( -- )
  ." the PIO USB core 1 command failed; inspect command_status" cr
;

: x-pio-usb-v3-stale-completion ( -- )
  ." the PIO USB port epoch changed unexpectedly" cr
;

: x-pio-usb-v3-invalid-argument ( -- )
  ." invalid PIO USB control-transfer argument" cr
;

: x-pio-usb-v3-invalid-descriptor ( -- )
  ." invalid first eight bytes of the USB device descriptor" cr
;

: pio-usb-core1-v3-image-present? ( -- flag )
  pio-usb-core1-v3-vector-table @ pio-usb-core1-v3-rstack =
  pio-usb-core1-v3-vector-table 4 + @
  pio-usb-core1-v3-entry 1 or = and
;

: pio-usb-core1-v3-abi? ( -- flag )
  pio-usb-v3-magic @ pio-usb-v3-abi-magic =
  pio-usb-v3-abi-version @ pio-usb-v3-abi = and
  pio-usb-v3-image-kind @ pio-usb-v3-frame-image = and
  pio-usb-v3-abi-bytes @ pio-usb-v3-abi-size = and
  pio-usb-v3-data-capacity @ pio-usb-v3-control-data-size = and
  pio-usb-v3-capabilities @
  pio-usb-v3-required-capabilities and
  pio-usb-v3-required-capabilities = and
;

: pio-usb-core1-v3-running? ( -- flag )
  pio-usb-core1-v3-abi?
  pio-usb-v3-phase @ pio-usb-v3-running-phase = and
  pio-usb-v3-fault @ 0= and
;

\ A changing heartbeat protects against loading this helper a second time and
\ clearing its RAM-local launch guard while the ABI-v3 image is still alive.
: pio-usb-core1-v3-alive? ( -- flag )
  pio-usb-v3-heartbeat @ 10 ms pio-usb-v3-heartbeat @ <>
;

: wait-pio-usb-core1-v3-start ( -- started? )
  1000 0 do
    pio-usb-core1-v3-running? if true unloop exit then
    1 ms
  loop
  false
;

: launch-pio-usb-core1-v3 ( -- )
  pio-usb-core1-v3-image-present?
  averts x-pio-usb-core1-v3-image-missing
  pio-usb-core1-v3-launched @ pio-usb-core1-v3-alive? or
  triggers x-pio-usb-core1-v3-already-launched
  true pio-usb-core1-v3-launched !
  disable-int
  pio-usb-core1-v3-sio-fifo-irq NVIC_ICER_CLRENA!
  pio-usb-core1-v3-sio-fifo-irq NVIC_ICPR_CLRPEND!
  pio-usb-core1-v3-entry pio-usb-core1-v3-rstack
  pio-usb-core1-v3-vector-table launch-core1
  pio-usb-core1-v3-sio-fifo-irq NVIC_ICER_CLRENA!
  pio-usb-core1-v3-sio-fifo-irq NVIC_ICPR_CLRPEND!
  dmb dsb isb
  enable-int
  wait-pio-usb-core1-v3-start averts x-pio-usb-core1-v3-not-running
;

: pio-usb-v3-valid-ep0-mps? ( mps -- flag )
  dup 8 = over 16 = or over 32 = or swap 64 = or
;

: pio-usb-v3-valid-byte? ( u -- flag ) 256 u< ;
: pio-usb-v3-valid-halfword? ( u -- flag ) 65536 u< ;

: pio-usb-v3-next-sequence ( sequence -- next-sequence )
  1+ dup 0= if drop 1 then
;

: pio-usb-v3-next-epoch ( epoch -- next-epoch )
  1+ dup 0= if drop 1 then
;

: pio-usb-v3-request-idle? ( -- flag )
  pio-usb-v3-request-seq @ pio-usb-v3-completion-seq @ =
  dup if dmb then
;

: require-pio-usb-v3-request-idle ( -- )
  pio-usb-core1-v3-running? averts x-pio-usb-core1-v3-not-running
  pio-usb-v3-request-idle? averts x-pio-usb-v3-command-in-flight
;

: pio-usb-v3-response-ready? ( sequence -- flag )
  pio-usb-v3-completion-seq @ =
  dup if dmb then
;

: wait-pio-usb-v3-response { sequence timeout-ms -- completed? }
  timeout-ms 0 ?do
    sequence pio-usb-v3-response-ready? if true unloop exit then
    pio-usb-core1-v3-running? 0= if false unloop exit then
    1 ms
  loop
  false
;

\ Every request field and any OUT payload must already be complete here.
\ Return the sequence and the epoch captured into this request.
: publish-pio-usb-v3-command { command -- sequence request-epoch }
  pio-usb-core1-v3-running? averts x-pio-usb-core1-v3-not-running
  pio-usb-v3-request-idle? averts x-pio-usb-v3-command-in-flight
  pio-usb-v3-port-epoch @ { request-epoch }
  pio-usb-v3-request-seq @ pio-usb-v3-next-sequence { sequence }
  request-epoch pio-usb-v3-request-epoch !
  command pio-usb-v3-command !
  dmb
  sequence pio-usb-v3-request-seq !
  dmb
  sequence request-epoch
;

\ Execute one prepared command.  A timeout is a terminal condition for this
\ helper: do not publish another command until the board has been reset.
: execute-pio-usb-v3-command
  { command timeout-ms -- request-epoch completion-epoch status }
  command publish-pio-usb-v3-command { sequence request-epoch }
  sequence timeout-ms wait-pio-usb-v3-response
  averts x-pio-usb-v3-timeout
  dmb
  request-epoch pio-usb-v3-completion-epoch @
  pio-usb-v3-command-status @
;

: require-pio-usb-v3-command-ok ( status -- )
  pio-usb-v3-command-ok = averts x-pio-usb-v3-command-failed
;

: pio-usb-v3-le16! { value address -- }
  value $FF and address c!
  value 8 rshift address 1+ c!
;

: prepare-pio-usb-v3-setup
  { request-type request value index length -- }
  request-type pio-usb-v3-valid-byte?
  request pio-usb-v3-valid-byte? and
  value pio-usb-v3-valid-halfword? and
  index pio-usb-v3-valid-halfword? and
  length pio-usb-v3-control-data-size u<= and
  averts x-pio-usb-v3-invalid-argument
  request-type pio-usb-v3-setup c!
  request pio-usb-v3-setup 1+ c!
  value pio-usb-v3-setup 2 + pio-usb-v3-le16!
  index pio-usb-v3-setup 4 + pio-usb-v3-le16!
  length pio-usb-v3-setup 6 + pio-usb-v3-le16!
;

: prepare-pio-usb-v3-control
  { address ep0-mps request-type request value index length -- }
  address 128 u<
  ep0-mps pio-usb-v3-valid-ep0-mps? and
  averts x-pio-usb-v3-invalid-argument
  address pio-usb-v3-device-address !
  ep0-mps pio-usb-v3-ep0-mps !
  length pio-usb-v3-transfer-length !
  request-type request value index length prepare-pio-usb-v3-setup
;

: execute-pio-usb-v3-control ( -- actual-length )
  pio-usb-v3-command-control-transfer pio-usb-v3-control-timeout-ms
  execute-pio-usb-v3-command
  { request-epoch completion-epoch status }
  status require-pio-usb-v3-command-ok
  completion-epoch request-epoch =
  pio-usb-v3-port-epoch @ request-epoch = and
  averts x-pio-usb-v3-stale-completion
  pio-usb-v3-actual-length @
;

\ Perform a device-to-host control transfer.  The returned bytes reside in
\ the fixed shared buffer and are valid until the next command is published.
: pio-usb-control-in-v3
  { address ep0-mps request-type request value index length -- data actual }
  require-pio-usb-v3-request-idle
  request-type $80 and 0<>
  length 0> and
  length pio-usb-v3-control-data-size u<= and
  averts x-pio-usb-v3-invalid-argument
  address ep0-mps request-type request value index length
  prepare-pio-usb-v3-control
  execute-pio-usb-v3-control { actual }
  actual length u<= averts x-pio-usb-v3-command-failed
  pio-usb-v3-data actual
;

\ Perform a host-to-device control transfer with a non-empty data stage.
: pio-usb-control-out-v3
  { data length address ep0-mps request-type request value index -- }
  require-pio-usb-v3-request-idle
  request-type $80 and 0=
  length 0> and
  length pio-usb-v3-control-data-size u<= and
  averts x-pio-usb-v3-invalid-argument
  address ep0-mps request-type request value index length
  prepare-pio-usb-v3-control
  data pio-usb-v3-data length move
  execute-pio-usb-v3-control length =
  averts x-pio-usb-v3-command-failed
;

\ Perform a host-to-device control transfer with no data stage.
: pio-usb-control-no-data-v3
  { address ep0-mps request-type request value index -- }
  require-pio-usb-v3-request-idle
  request-type $80 and 0= averts x-pio-usb-v3-invalid-argument
  address ep0-mps request-type request value index 0
  prepare-pio-usb-v3-control
  execute-pio-usb-v3-control 0=
  averts x-pio-usb-v3-command-failed
;

\ Reset the root port and return its new epoch.  Core 1 increments the epoch
\ when reset starts, so success must complete with exactly next(request_epoch).
: pio-usb-port-reset-v3 ( -- new-epoch )
  require-pio-usb-v3-request-idle
  0 pio-usb-v3-device-address !
  8 pio-usb-v3-ep0-mps !
  0 pio-usb-v3-transfer-length !
  pio-usb-v3-setup 8 0 fill
  pio-usb-v3-command-port-reset pio-usb-v3-reset-timeout-ms
  execute-pio-usb-v3-command
  { request-epoch completion-epoch status }
  status require-pio-usb-v3-command-ok
  completion-epoch request-epoch pio-usb-v3-next-epoch =
  pio-usb-v3-port-epoch @ completion-epoch = and
  pio-usb-v3-port-state @ pio-usb-v3-port-active = and
  averts x-pio-usb-v3-stale-completion
  completion-epoch
;

: pio-usb-v3-valid-device-descriptor-8?
  { data length -- valid? }
  length 8 <> if false exit then
  data c@ 18 =
  data 1+ c@ 1 = and
  data 7 + c@ pio-usb-v3-valid-ep0-mps? and
;

\ First ABI-v3 milestone: reset, then reproduce the verified eight-byte
\ device-descriptor read at address zero with the conservative MPS of eight.
\ Return the shared data address and the exact received length.
: pio-usb-get-device-descriptor-8-v3 ( -- data length )
  pio-usb-port-reset-v3 drop
  0 8 $80 $06 $0100 0 8 pio-usb-control-in-v3
  2dup pio-usb-v3-valid-device-descriptor-8?
  averts x-pio-usb-v3-invalid-descriptor
;
