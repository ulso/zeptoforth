\ RP2350 core 1 PIO USB ABI-v4 helper for zeptoforth 1.16.5.
\
\ This file only defines words.  It does not load the raw image, launch core 1,
\ reset the USB port, or enumerate a device when the file is loaded.
\
\ DANGER:
\
\ * Start from a fresh board reset.  Load the matching core1_pio_usb.bin at
\   $20060000 and byte-verify it before calling launch-pio-usb-core1-v4.
\ * The addresses below are pinned to the audited ABI-v4 image whose initialized
\   copy ends at $20064AB0.  Re-derive them after every changed core 1 build.
\   Reference BIN SHA-256:
\   637f9b88b15b344a2147203eaa373d571224c6ff1eaa4e56d215fa9d780f50cf
\ * Launch core 1 exactly once.  Reset the board before loading or launching a
\   different image, or before reusing PIO0, DMA0, GP24, or GP25.
\ * Request fields and any OUT payload are written before request_seq is
\   published.  Response fields and any IN payload are read only after the
\   matching completion_seq has been observed.
\ * The shared-data address returned by an IN helper remains valid, but its
\   contents are overwritten by the next control or endpoint command.
\ * Every public mailbox command takes one global non-blocking simple lock.
\   Words ending in -unlocked are for a caller already holding that lock.

defined? pio-usb-host-persistent-build [if]
  compile-to-flash
[else]
  compile-to-ram
[then]

core1 import
interrupt import
slock import

\ Audited ABI-v4 image and launch addresses.
$20060000 constant pio-usb-core1-v4-vector-table
$200604C0 constant pio-usb-core1-v4-entry
$20064AB0 constant pio-usb-core1-v4-copy-end
$20080E00 constant pio-usb-core1-v4-shared
$20082000 constant pio-usb-core1-v4-rstack
25 constant pio-usb-core1-v4-sio-fifo-irq

\ ABI v4 telemetry and metadata.
pio-usb-core1-v4-shared $00 + constant pio-usb-v4-magic
pio-usb-core1-v4-shared $04 + constant pio-usb-v4-abi-version
pio-usb-core1-v4-shared $08 + constant pio-usb-v4-image-kind
pio-usb-core1-v4-shared $0C + constant pio-usb-v4-phase
pio-usb-core1-v4-shared $10 + constant pio-usb-v4-heartbeat
pio-usb-core1-v4-shared $14 + constant pio-usb-v4-fault
pio-usb-core1-v4-shared $18 + constant pio-usb-v4-clock-hz
pio-usb-core1-v4-shared $1C + constant pio-usb-v4-frame-count
pio-usb-core1-v4-shared $20 + constant pio-usb-v4-line-state
pio-usb-core1-v4-shared $24 + constant pio-usb-v4-root-event
pio-usb-core1-v4-shared $28 + constant pio-usb-v4-root-ints
pio-usb-core1-v4-shared $2C + constant pio-usb-v4-root-connected
pio-usb-core1-v4-shared $30 + constant pio-usb-v4-root-full-speed
pio-usb-core1-v4-shared $34 + constant pio-usb-v4-resource-busy
pio-usb-core1-v4-shared $38 + constant pio-usb-v4-pio-ctrl-before
pio-usb-core1-v4-shared $3C + constant pio-usb-v4-dma-ctrl-before
pio-usb-core1-v4-shared $40 + constant pio-usb-v4-pio-ctrl
pio-usb-core1-v4-shared $44 + constant pio-usb-v4-dma-ctrl
pio-usb-core1-v4-shared $48 + constant pio-usb-v4-abi-bytes
pio-usb-core1-v4-shared $4C + constant pio-usb-v4-capabilities
pio-usb-core1-v4-shared $50 + constant pio-usb-v4-data-capacity
pio-usb-core1-v4-shared $54 + constant pio-usb-v4-port-epoch
pio-usb-core1-v4-shared $58 + constant pio-usb-v4-port-state

\ Core 0 request, published by writing request_seq last.
pio-usb-core1-v4-shared $5C + constant pio-usb-v4-request-seq
pio-usb-core1-v4-shared $60 + constant pio-usb-v4-command
pio-usb-core1-v4-shared $64 + constant pio-usb-v4-request-epoch
pio-usb-core1-v4-shared $68 + constant pio-usb-v4-device-address
pio-usb-core1-v4-shared $6C + constant pio-usb-v4-ep0-mps
pio-usb-core1-v4-shared $70 + constant pio-usb-v4-transfer-length
pio-usb-core1-v4-shared $74 + constant pio-usb-v4-setup

\ Core 1 response, published by writing completion_seq last.
pio-usb-core1-v4-shared $7C + constant pio-usb-v4-completion-seq
pio-usb-core1-v4-shared $80 + constant pio-usb-v4-command-status
pio-usb-core1-v4-shared $84 + constant pio-usb-v4-command-phase
pio-usb-core1-v4-shared $88 + constant pio-usb-v4-completion-epoch
pio-usb-core1-v4-shared $8C + constant pio-usb-v4-actual-length
pio-usb-core1-v4-shared $90 + constant pio-usb-v4-completion-frame
pio-usb-core1-v4-shared $94 + constant pio-usb-v4-deadline-frame
pio-usb-core1-v4-shared $98 + constant pio-usb-v4-endpoint-complete
pio-usb-core1-v4-shared $9C + constant pio-usb-v4-endpoint-error
pio-usb-core1-v4-shared $A0 + constant pio-usb-v4-endpoint-stalled
pio-usb-core1-v4-shared $A4 + constant pio-usb-v4-failure-detail
pio-usb-core1-v4-shared $A8 + constant pio-usb-v4-endpoint-address
pio-usb-core1-v4-shared $AC + constant pio-usb-v4-endpoint-attributes
pio-usb-core1-v4-shared $B0 + constant pio-usb-v4-endpoint-max-packet
pio-usb-core1-v4-shared $B4 + constant pio-usb-v4-endpoint-interval
pio-usb-core1-v4-shared $B8 + constant pio-usb-v4-transfer-timeout-frames
pio-usb-core1-v4-shared $100 + constant pio-usb-v4-data

\ Fixed ABI values.
$43505531 constant pio-usb-v4-abi-magic       \ "CPU1"
4 constant pio-usb-v4-abi
512 constant pio-usb-v4-abi-size
256 constant pio-usb-v4-control-data-size
2 constant pio-usb-v4-frame-image
4 constant pio-usb-v4-running-phase
1 constant pio-usb-v4-cap-port-reset
2 constant pio-usb-v4-cap-control-transfer
4 constant pio-usb-v4-cap-endpoint-lifecycle
8 constant pio-usb-v4-cap-endpoint-transfer
$0F constant pio-usb-v4-required-capabilities
2 constant pio-usb-v4-port-active

0 constant pio-usb-v4-command-none
1 constant pio-usb-v4-command-port-reset
2 constant pio-usb-v4-command-control-transfer
3 constant pio-usb-v4-command-endpoint-open
4 constant pio-usb-v4-command-endpoint-close
5 constant pio-usb-v4-command-endpoint-transfer

0 constant pio-usb-v4-command-idle
1 constant pio-usb-v4-command-busy
2 constant pio-usb-v4-command-ok
3 constant pio-usb-v4-command-partial
$80 constant pio-usb-v4-command-error-invalid
$81 constant pio-usb-v4-command-error-stale-epoch
$82 constant pio-usb-v4-command-error-not-connected
$83 constant pio-usb-v4-command-error-not-full-speed
$84 constant pio-usb-v4-command-error-port-state
$85 constant pio-usb-v4-command-error-endpoint-open
$86 constant pio-usb-v4-command-error-setup-start
$87 constant pio-usb-v4-command-error-data-start
$88 constant pio-usb-v4-command-error-status-start
$89 constant pio-usb-v4-command-error-transfer
$8A constant pio-usb-v4-command-error-stall
$8B constant pio-usb-v4-command-error-timeout
$8C constant pio-usb-v4-command-error-disconnect
$8D constant pio-usb-v4-command-error-endpoint-not-open
$8E constant pio-usb-v4-command-error-endpoint-already-open
$8F constant pio-usb-v4-command-error-endpoint-busy

0 constant pio-usb-v4-command-phase-idle
1 constant pio-usb-v4-command-phase-reset
2 constant pio-usb-v4-command-phase-recovery
3 constant pio-usb-v4-command-phase-setup
4 constant pio-usb-v4-command-phase-data
5 constant pio-usb-v4-command-phase-status
6 constant pio-usb-v4-command-phase-complete
7 constant pio-usb-v4-command-phase-endpoint-data
$80 constant pio-usb-v4-command-phase-error

2 constant pio-usb-v4-endpoint-attribute-bulk
3 constant pio-usb-v4-endpoint-attribute-interrupt

250 constant pio-usb-v4-reset-timeout-ms
500 constant pio-usb-v4-control-timeout-ms
500 constant pio-usb-v4-endpoint-lifecycle-timeout-ms
1000 constant pio-usb-v4-endpoint-timeout-max-frames
100 constant pio-usb-v4-endpoint-response-grace-ms

variable pio-usb-core1-v4-launched
slock-size buffer: pio-usb-v4-command-slock

: x-pio-usb-core1-v4-image-missing ( -- )
  ." the audited ABI-v4 core 1 image is not loaded" cr
;

: x-pio-usb-core1-v4-already-launched ( -- )
  ." core 1 is already running; reset before relaunching" cr
;

: x-pio-usb-core1-v4-not-running ( -- )
  ." the audited ABI-v4 PIO USB core 1 image is not running" cr
;

: x-pio-usb-v4-command-in-flight ( -- )
  ." a PIO USB core 1 command is already in flight" cr
;

: x-pio-usb-v4-command-lock-busy ( -- )
  ." another PIO USB mailbox command is active" cr
;

: x-pio-usb-v4-timeout ( -- )
  ." timed out waiting for the PIO USB core 1 response" cr
;

: x-pio-usb-v4-command-failed ( -- )
  ." the PIO USB core 1 command failed; inspect command_status" cr
;

: x-pio-usb-v4-stale-completion ( -- )
  ." the PIO USB port epoch changed unexpectedly" cr
;

: x-pio-usb-v4-invalid-argument ( -- )
  ." invalid PIO USB command argument" cr
;

: x-pio-usb-v4-invalid-descriptor ( -- )
  ." invalid first eight bytes of the USB device descriptor" cr
;

\ Execute a quotation while exclusively owning the ABI-v4 mailbox.  Unlike a
\ blocking lock this fails immediately on concurrency or same-task recursion.
: with-pio-usb-v4-command-lock ( xt -- )
  pio-usb-v4-command-slock try-claim-slock
  averts x-pio-usb-v4-command-lock-busy
  try
  pio-usb-v4-command-slock release-slock
  ?raise
;

: pio-usb-core1-v4-image-present? ( -- flag )
  pio-usb-core1-v4-vector-table @ pio-usb-core1-v4-rstack =
  pio-usb-core1-v4-vector-table 4 + @
  pio-usb-core1-v4-entry 1 or = and
;

: pio-usb-core1-v4-abi? ( -- flag )
  pio-usb-v4-magic @ pio-usb-v4-abi-magic =
  pio-usb-v4-abi-version @ pio-usb-v4-abi = and
  pio-usb-v4-image-kind @ pio-usb-v4-frame-image = and
  pio-usb-v4-abi-bytes @ pio-usb-v4-abi-size = and
  pio-usb-v4-data-capacity @ pio-usb-v4-control-data-size = and
  pio-usb-v4-capabilities @
  pio-usb-v4-required-capabilities and
  pio-usb-v4-required-capabilities = and
;

: pio-usb-core1-v4-running? ( -- flag )
  pio-usb-core1-v4-abi?
  pio-usb-v4-phase @ pio-usb-v4-running-phase = and
  pio-usb-v4-fault @ 0= and
;

\ A changing heartbeat protects against loading this helper a second time and
\ clearing its RAM-local launch guard while the ABI-v4 image is still alive.
: pio-usb-core1-v4-alive? ( -- flag )
  pio-usb-v4-heartbeat @ 10 ms pio-usb-v4-heartbeat @ <>
;

: wait-pio-usb-core1-v4-start ( -- started? )
  1000 0 do
    pio-usb-core1-v4-running? if true unloop exit then
    1 ms
  loop
  false
;

: launch-pio-usb-core1-v4 ( -- )
  pio-usb-core1-v4-image-present?
  averts x-pio-usb-core1-v4-image-missing
  pio-usb-core1-v4-launched @ pio-usb-core1-v4-alive? or
  triggers x-pio-usb-core1-v4-already-launched
  true pio-usb-core1-v4-launched !
  disable-int
  pio-usb-core1-v4-sio-fifo-irq NVIC_ICER_CLRENA!
  pio-usb-core1-v4-sio-fifo-irq NVIC_ICPR_CLRPEND!
  pio-usb-core1-v4-entry pio-usb-core1-v4-rstack
  pio-usb-core1-v4-vector-table launch-core1
  pio-usb-core1-v4-sio-fifo-irq NVIC_ICER_CLRENA!
  pio-usb-core1-v4-sio-fifo-irq NVIC_ICPR_CLRPEND!
  dmb dsb isb
  enable-int
  wait-pio-usb-core1-v4-start averts x-pio-usb-core1-v4-not-running
;

: pio-usb-v4-valid-ep0-mps? ( mps -- flag )
  dup 8 = over 16 = or over 32 = or swap 64 = or
;

: pio-usb-v4-valid-byte? ( u -- flag ) 256 u< ;
: pio-usb-v4-valid-halfword? ( u -- flag ) 65536 u< ;

: pio-usb-v4-valid-device-address? ( address -- valid? )
  dup 0> swap 128 u< and
;

: pio-usb-v4-valid-data-endpoint-address? ( endpoint-address -- valid? )
  dup pio-usb-v4-valid-byte?
  over $70 and 0= and
  swap $0F and 0<> and
;

: clear-pio-usb-v4-endpoint-request ( -- )
  0 pio-usb-v4-endpoint-address !
  0 pio-usb-v4-endpoint-attributes !
  0 pio-usb-v4-endpoint-max-packet !
  0 pio-usb-v4-endpoint-interval !
  0 pio-usb-v4-transfer-timeout-frames !
;

: pio-usb-v4-next-sequence ( sequence -- next-sequence )
  1+ dup 0= if drop 1 then
;

: pio-usb-v4-next-epoch ( epoch -- next-epoch )
  1+ dup 0= if drop 1 then
;

: pio-usb-v4-request-idle? ( -- flag )
  pio-usb-v4-request-seq @ pio-usb-v4-completion-seq @ =
  dup if dmb then
;

: require-pio-usb-v4-request-idle ( -- )
  pio-usb-core1-v4-running? averts x-pio-usb-core1-v4-not-running
  pio-usb-v4-request-idle? averts x-pio-usb-v4-command-in-flight
;

: pio-usb-v4-response-ready? ( sequence -- flag )
  pio-usb-v4-completion-seq @ =
  dup if dmb then
;

: wait-pio-usb-v4-response { sequence timeout-ms -- completed? }
  timeout-ms 0 ?do
    sequence pio-usb-v4-response-ready? if true unloop exit then
    pio-usb-core1-v4-running? 0= if false unloop exit then
    1 ms
  loop
  false
;

\ Every request field and any OUT payload must already be complete here.
\ Return the sequence and the epoch captured into this request.
: publish-pio-usb-v4-command-unlocked
  { command -- sequence request-epoch }
  pio-usb-core1-v4-running? averts x-pio-usb-core1-v4-not-running
  pio-usb-v4-request-idle? averts x-pio-usb-v4-command-in-flight
  pio-usb-v4-port-epoch @ { request-epoch }
  pio-usb-v4-request-seq @ pio-usb-v4-next-sequence { sequence }
  request-epoch pio-usb-v4-request-epoch !
  command pio-usb-v4-command !
  dmb
  sequence pio-usb-v4-request-seq !
  dmb
  sequence request-epoch
;

\ Execute one prepared command.  A timeout is a terminal condition for this
\ helper: do not publish another command until the board has been reset.
: execute-pio-usb-v4-command-unlocked
  { command timeout-ms -- request-epoch completion-epoch status }
  command publish-pio-usb-v4-command-unlocked { sequence request-epoch }
  sequence timeout-ms wait-pio-usb-v4-response
  averts x-pio-usb-v4-timeout
  dmb
  request-epoch pio-usb-v4-completion-epoch @
  pio-usb-v4-command-status @
;

: require-pio-usb-v4-command-ok ( status -- )
  pio-usb-v4-command-ok = averts x-pio-usb-v4-command-failed
;

: pio-usb-v4-le16! { value address -- }
  value $FF and address c!
  value 8 rshift address 1+ c!
;

: prepare-pio-usb-v4-setup
  { request-type request value index length -- }
  request-type pio-usb-v4-valid-byte?
  request pio-usb-v4-valid-byte? and
  value pio-usb-v4-valid-halfword? and
  index pio-usb-v4-valid-halfword? and
  length pio-usb-v4-control-data-size u<= and
  averts x-pio-usb-v4-invalid-argument
  request-type pio-usb-v4-setup c!
  request pio-usb-v4-setup 1+ c!
  value pio-usb-v4-setup 2 + pio-usb-v4-le16!
  index pio-usb-v4-setup 4 + pio-usb-v4-le16!
  length pio-usb-v4-setup 6 + pio-usb-v4-le16!
;

: prepare-pio-usb-v4-control
  { address ep0-mps request-type request value index length -- }
  address 128 u<
  ep0-mps pio-usb-v4-valid-ep0-mps? and
  averts x-pio-usb-v4-invalid-argument
  clear-pio-usb-v4-endpoint-request
  address pio-usb-v4-device-address !
  ep0-mps pio-usb-v4-ep0-mps !
  length pio-usb-v4-transfer-length !
  request-type request value index length prepare-pio-usb-v4-setup
;

: execute-pio-usb-v4-control-unlocked ( -- actual-length )
  pio-usb-v4-command-control-transfer pio-usb-v4-control-timeout-ms
  execute-pio-usb-v4-command-unlocked
  { request-epoch completion-epoch status }
  status require-pio-usb-v4-command-ok
  pio-usb-v4-command-phase @ pio-usb-v4-command-phase-complete =
  averts x-pio-usb-v4-command-failed
  completion-epoch request-epoch =
  pio-usb-v4-port-epoch @ request-epoch = and
  averts x-pio-usb-v4-stale-completion
  pio-usb-v4-actual-length @
;

\ Perform a device-to-host control transfer.  The returned bytes reside in
\ the fixed shared buffer and are valid until the next command is published.
: pio-usb-control-in-v4-unlocked
  { address ep0-mps request-type request value index length -- data actual }
  require-pio-usb-v4-request-idle
  request-type $80 and 0<>
  length 0> and
  length pio-usb-v4-control-data-size u<= and
  averts x-pio-usb-v4-invalid-argument
  address ep0-mps request-type request value index length
  prepare-pio-usb-v4-control
  execute-pio-usb-v4-control-unlocked { actual }
  actual length u<= averts x-pio-usb-v4-command-failed
  pio-usb-v4-data actual
;

\ Perform a host-to-device control transfer with a non-empty data stage.
: pio-usb-control-out-v4-unlocked
  { data length address ep0-mps request-type request value index -- }
  require-pio-usb-v4-request-idle
  request-type $80 and 0=
  length 0> and
  length pio-usb-v4-control-data-size u<= and
  averts x-pio-usb-v4-invalid-argument
  address ep0-mps request-type request value index length
  prepare-pio-usb-v4-control
  data pio-usb-v4-data length move
  execute-pio-usb-v4-control-unlocked length =
  averts x-pio-usb-v4-command-failed
;

\ Perform a host-to-device control transfer with no data stage.
: pio-usb-control-no-data-v4-unlocked
  { address ep0-mps request-type request value index -- }
  require-pio-usb-v4-request-idle
  request-type $80 and 0= averts x-pio-usb-v4-invalid-argument
  address ep0-mps request-type request value index 0
  prepare-pio-usb-v4-control
  execute-pio-usb-v4-control-unlocked 0=
  averts x-pio-usb-v4-command-failed
;

\ Reset the root port and return its new epoch.  Core 1 increments the epoch
\ when reset starts, so success must complete with exactly next(request_epoch).
: pio-usb-port-reset-v4-unlocked ( -- new-epoch )
  require-pio-usb-v4-request-idle
  clear-pio-usb-v4-endpoint-request
  0 pio-usb-v4-device-address !
  8 pio-usb-v4-ep0-mps !
  0 pio-usb-v4-transfer-length !
  pio-usb-v4-setup 8 0 fill
  pio-usb-v4-command-port-reset pio-usb-v4-reset-timeout-ms
  execute-pio-usb-v4-command-unlocked
  { request-epoch completion-epoch status }
  status require-pio-usb-v4-command-ok
  pio-usb-v4-command-phase @ pio-usb-v4-command-phase-complete =
  averts x-pio-usb-v4-command-failed
  completion-epoch request-epoch pio-usb-v4-next-epoch =
  pio-usb-v4-port-epoch @ completion-epoch = and
  pio-usb-v4-port-state @ pio-usb-v4-port-active = and
  averts x-pio-usb-v4-stale-completion
  completion-epoch
;

: require-pio-usb-v4-command-epoch
  { request-epoch completion-epoch -- }
  completion-epoch request-epoch =
  pio-usb-v4-port-epoch @ request-epoch = and
  averts x-pio-usb-v4-stale-completion
;

: pio-usb-v4-valid-bulk-mps? ( max-packet -- valid? )
  dup 8 = over 16 = or over 32 = or swap 64 = or
;

: pio-usb-v4-valid-endpoint-open?
  { attributes max-packet interval -- valid? }
  interval pio-usb-v4-valid-byte? 0= if false exit then
  attributes pio-usb-v4-endpoint-attribute-bulk = if
    max-packet pio-usb-v4-valid-bulk-mps?
  else
    attributes pio-usb-v4-endpoint-attribute-interrupt =
    max-packet 0> and
    max-packet 64 u<= and
    interval 0<> and
  then
;

: prepare-pio-usb-v4-endpoint-reference-unlocked
  { address endpoint-address -- }
  require-pio-usb-v4-request-idle
  address pio-usb-v4-valid-device-address?
  endpoint-address pio-usb-v4-valid-data-endpoint-address? and
  averts x-pio-usb-v4-invalid-argument
  clear-pio-usb-v4-endpoint-request
  address pio-usb-v4-device-address !
  0 pio-usb-v4-ep0-mps !
  0 pio-usb-v4-transfer-length !
  pio-usb-v4-setup 8 0 fill
  endpoint-address pio-usb-v4-endpoint-address !
;

: execute-pio-usb-v4-endpoint-lifecycle-unlocked ( command -- )
  pio-usb-v4-endpoint-lifecycle-timeout-ms
  execute-pio-usb-v4-command-unlocked
  { request-epoch completion-epoch status }
  request-epoch completion-epoch require-pio-usb-v4-command-epoch
  status require-pio-usb-v4-command-ok
  pio-usb-v4-command-phase @ pio-usb-v4-command-phase-complete =
  averts x-pio-usb-v4-command-failed
  pio-usb-v4-actual-length @ 0=
  averts x-pio-usb-v4-command-failed
;

: pio-usb-endpoint-open-v4-unlocked
  { address endpoint-address attributes max-packet interval -- }
  endpoint-address pio-usb-v4-valid-data-endpoint-address?
  attributes max-packet interval pio-usb-v4-valid-endpoint-open? and
  averts x-pio-usb-v4-invalid-argument
  address endpoint-address prepare-pio-usb-v4-endpoint-reference-unlocked
  attributes pio-usb-v4-endpoint-attributes !
  max-packet pio-usb-v4-endpoint-max-packet !
  interval pio-usb-v4-endpoint-interval !
  pio-usb-v4-command-endpoint-open
  execute-pio-usb-v4-endpoint-lifecycle-unlocked
;

: pio-usb-endpoint-close-v4-unlocked
  { address endpoint-address -- }
  address endpoint-address prepare-pio-usb-v4-endpoint-reference-unlocked
  pio-usb-v4-command-endpoint-close
  execute-pio-usb-v4-endpoint-lifecycle-unlocked
;

: prepare-pio-usb-v4-endpoint-transfer-unlocked
  { address endpoint-address length timeout-frames -- }
  length 0>
  length pio-usb-v4-control-data-size u<= and
  timeout-frames 0> and
  timeout-frames pio-usb-v4-endpoint-timeout-max-frames u<= and
  averts x-pio-usb-v4-invalid-argument
  address endpoint-address prepare-pio-usb-v4-endpoint-reference-unlocked
  length pio-usb-v4-transfer-length !
  timeout-frames pio-usb-v4-transfer-timeout-frames !
;

: pio-usb-v4-nonfatal-endpoint-status? ( status -- nonfatal? )
  dup pio-usb-v4-command-ok =
  over pio-usb-v4-command-partial = or
  swap pio-usb-v4-command-error-timeout = or
;

: execute-pio-usb-v4-endpoint-transfer-unlocked
  { requested-length timeout-frames -- actual status }
  pio-usb-v4-command-endpoint-transfer
  timeout-frames pio-usb-v4-endpoint-response-grace-ms +
  execute-pio-usb-v4-command-unlocked
  { request-epoch completion-epoch status }
  request-epoch completion-epoch require-pio-usb-v4-command-epoch
  status pio-usb-v4-nonfatal-endpoint-status?
  averts x-pio-usb-v4-command-failed
  status pio-usb-v4-command-error-timeout = if
    pio-usb-v4-command-phase @ pio-usb-v4-command-phase-error =
  else
    pio-usb-v4-command-phase @ pio-usb-v4-command-phase-complete =
  then
  averts x-pio-usb-v4-command-failed
  pio-usb-v4-actual-length @ { actual }
  actual requested-length u<=
  status pio-usb-v4-command-error-timeout = if actual 0= else true then and
  status pio-usb-v4-command-partial = if actual 0> else true then and
  averts x-pio-usb-v4-command-failed
  actual status
;

: pio-usb-endpoint-in-v4-unlocked
  { address endpoint-address length timeout-frames -- data actual status }
  endpoint-address $80 and 0<>
  averts x-pio-usb-v4-invalid-argument
  address endpoint-address length timeout-frames
  prepare-pio-usb-v4-endpoint-transfer-unlocked
  length timeout-frames execute-pio-usb-v4-endpoint-transfer-unlocked
  { actual status }
  pio-usb-v4-data actual status
;

: pio-usb-endpoint-out-v4-unlocked
  { data length address endpoint-address timeout-frames -- actual status }
  endpoint-address $80 and 0=
  averts x-pio-usb-v4-invalid-argument
  address endpoint-address length timeout-frames
  prepare-pio-usb-v4-endpoint-transfer-unlocked
  data pio-usb-v4-data length move
  length timeout-frames execute-pio-usb-v4-endpoint-transfer-unlocked
  { actual status }
  status pio-usb-v4-command-ok = if
    actual length = averts x-pio-usb-v4-command-failed
  then
  actual status
;

\ Public command words acquire the one global mailbox lock.  The endpoint
\ open descriptor selects bulk versus interrupt behavior for later transfers.
: pio-usb-port-reset-v4 ( -- new-epoch )
  [: pio-usb-port-reset-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-control-in-v4
  ( address ep0-mps request-type request value index length -- data actual )
  [: pio-usb-control-in-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-control-out-v4
  ( data length address ep0-mps request-type request value index -- )
  [: pio-usb-control-out-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-control-no-data-v4
  ( address ep0-mps request-type request value index -- )
  [: pio-usb-control-no-data-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-endpoint-open-v4
  ( address endpoint-address attributes max-packet interval -- )
  [: pio-usb-endpoint-open-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-endpoint-close-v4 ( address endpoint-address -- )
  [: pio-usb-endpoint-close-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-endpoint-in-v4
  ( address endpoint-address length timeout-frames -- data actual status )
  [: pio-usb-endpoint-in-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-endpoint-out-v4
  ( data length address endpoint-address timeout-frames -- actual status )
  [: pio-usb-endpoint-out-v4-unlocked ;]
  with-pio-usb-v4-command-lock
;

: pio-usb-bulk-in-v4
  ( address endpoint-address length timeout-frames -- data actual status )
  pio-usb-endpoint-in-v4
;

: pio-usb-bulk-out-v4
  ( data length address endpoint-address timeout-frames -- actual status )
  pio-usb-endpoint-out-v4
;

: pio-usb-interrupt-in-v4
  ( address endpoint-address length timeout-frames -- data actual status )
  pio-usb-endpoint-in-v4
;

: pio-usb-interrupt-out-v4
  ( data length address endpoint-address timeout-frames -- actual status )
  pio-usb-endpoint-out-v4
;

: pio-usb-v4-valid-device-descriptor-8?
  { data length -- valid? }
  length 8 <> if false exit then
  data c@ 18 =
  data 1+ c@ 1 = and
  data 7 + c@ pio-usb-v4-valid-ep0-mps? and
;

\ First ABI-v4 milestone: reset, then reproduce the verified eight-byte
\ device-descriptor read at address zero with the conservative MPS of eight.
\ Return the shared data address and the exact received length.
: pio-usb-get-device-descriptor-8-v4-unlocked ( -- data length epoch )
  pio-usb-port-reset-v4-unlocked { epoch }
  0 8 $80 $06 $0100 0 8 pio-usb-control-in-v4-unlocked
  2dup pio-usb-v4-valid-device-descriptor-8?
  averts x-pio-usb-v4-invalid-descriptor
  epoch
;

: pio-usb-get-device-descriptor-8-v4 ( -- data length )
  [: pio-usb-get-device-descriptor-8-v4-unlocked ;]
  with-pio-usb-v4-command-lock
  drop
;

\ Initialize only RAM-local serialization state; no mailbox command is sent.
\ `initializer` executes this immediately for an ordinary RAM load and chains
\ it into `init` when this file is included in a persistent firmware image.
: init-pio-usb-v4-command-state ( -- )
  pio-usb-core1-v4-alive? 0= if
    false pio-usb-core1-v4-launched !
  then
  pio-usb-v4-command-slock init-slock
;

initializer init-pio-usb-v4-command-state
