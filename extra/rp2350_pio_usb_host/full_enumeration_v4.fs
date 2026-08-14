\ RP2350 PIO USB ABI-v4 root-device control-only enumeration helper.
\
\ Load enumeration_v4.fs first.  This file only defines words and initializes
\ RAM-local state.  It does not launch core 1, reset the port, or enumerate a
\ device when loaded.
\
\ pio-usb-enumerate-root-control-v4 performs the standard address-zero flow,
\ parses one complete configuration descriptor tree, and selects that
\ configuration.  It deliberately does not open endpoints or implement CDC.
\ All control transfers are bounded by enumeration_v4.fs.  The global command
\ lock is held across the whole sequence, so no other mailbox client can
\ interleave a command between reset and SET_CONFIGURATION.
\
\ The diagnostic buffers are valid only when
\ pio-usb-v4-enumeration-complete? returns true.  A failed attempt leaves the
\ stage and exception variables set, but clears the complete flag.

compile-to-ram

\ Standard USB requests and descriptor types used by this milestone.
$05 constant pio-usb-v4-request-set-address
$06 constant pio-usb-v4-request-get-descriptor
$08 constant pio-usb-v4-request-get-configuration
$09 constant pio-usb-v4-request-set-configuration
$01 constant pio-usb-v4-descriptor-device
$02 constant pio-usb-v4-descriptor-configuration
$04 constant pio-usb-v4-descriptor-interface
$05 constant pio-usb-v4-descriptor-endpoint
$0100 constant pio-usb-v4-device-descriptor-value
$0200 constant pio-usb-v4-configuration-descriptor-value
1 constant pio-usb-v4-root-device-address

18 constant pio-usb-v4-device-descriptor-size
9 constant pio-usb-v4-configuration-header-size
9 constant pio-usb-v4-interface-descriptor-size
7 constant pio-usb-v4-endpoint-descriptor-size

\ One configuration is at most the 256-byte ABI-v4 control buffer.  These
\ table capacities exceed what can physically fit after its nine-byte header.
28 constant pio-usb-v4-max-interface-descriptors
36 constant pio-usb-v4-max-endpoint-descriptors
8 constant pio-usb-v4-interface-record-size
8 constant pio-usb-v4-endpoint-record-size

\ Enumeration progress, useful after an exception.
0 constant pio-usb-v4-enumeration-idle
1 constant pio-usb-v4-enumeration-reset-get-device-8
2 constant pio-usb-v4-enumeration-set-address
3 constant pio-usb-v4-enumeration-get-device-18
4 constant pio-usb-v4-enumeration-get-configuration-9
5 constant pio-usb-v4-enumeration-get-configuration-full
6 constant pio-usb-v4-enumeration-parse-configuration
7 constant pio-usb-v4-enumeration-set-configuration
8 constant pio-usb-v4-enumeration-get-configuration
9 constant pio-usb-v4-enumeration-complete

variable pio-usb-v4-enumeration-stage
variable pio-usb-v4-enumeration-last-exception
variable pio-usb-v4-enumerated
variable pio-usb-v4-enumerated-address
variable pio-usb-v4-enumerated-configuration
variable pio-usb-v4-enumerated-vid
variable pio-usb-v4-enumerated-pid
variable pio-usb-v4-enumerated-ep0-mps
variable pio-usb-v4-enumerated-epoch
variable pio-usb-v4-enumerated-configuration-length
variable pio-usb-v4-declared-interface-count
variable pio-usb-v4-unique-interface-count
variable pio-usb-v4-interface-descriptor-count
variable pio-usb-v4-endpoint-descriptor-count
variable pio-usb-v4-other-descriptor-count

pio-usb-v4-device-descriptor-size
buffer: pio-usb-v4-enumerated-device-descriptor
pio-usb-v4-configuration-header-size
buffer: pio-usb-v4-enumerated-configuration-header
pio-usb-v4-control-data-size
buffer: pio-usb-v4-enumerated-configuration-descriptors
256 buffer: pio-usb-v4-interface-seen
pio-usb-v4-max-interface-descriptors pio-usb-v4-interface-record-size *
buffer: pio-usb-v4-interface-records
pio-usb-v4-max-endpoint-descriptors pio-usb-v4-endpoint-record-size *
buffer: pio-usb-v4-endpoint-records

: x-pio-usb-v4-invalid-device-descriptor ( -- )
  ." invalid USB device descriptor" cr
;

: x-pio-usb-v4-invalid-configuration ( -- )
  ." invalid USB configuration descriptor tree" cr
;

: x-pio-usb-v4-diagnostic-table-full ( -- )
  ." USB descriptor diagnostic table is full" cr
;

: pio-usb-v4-le16@ ( address -- value )
  dup c@ swap 1+ c@ 8 lshift or
;

: pio-usb-v4-bytes-equal? { first second length -- equal? }
  length 0 ?do
    first i + c@ second i + c@ <> if false unloop exit then
  loop
  true
;

: pio-usb-v4-enumeration-complete? ( -- complete? )
  pio-usb-v4-enumerated @
;

: pio-usb-v4-interface-record@ ( index -- record )
  dup pio-usb-v4-interface-descriptor-count @ u<
  averts x-pio-usb-v4-invalid-argument
  pio-usb-v4-interface-record-size * pio-usb-v4-interface-records +
;

: pio-usb-v4-endpoint-record@ ( index -- record )
  dup pio-usb-v4-endpoint-descriptor-count @ u<
  averts x-pio-usb-v4-invalid-argument
  pio-usb-v4-endpoint-record-size * pio-usb-v4-endpoint-records +
;

: clear-pio-usb-v4-enumeration-diagnostics ( -- )
  false pio-usb-v4-enumerated !
  pio-usb-v4-enumeration-idle pio-usb-v4-enumeration-stage !
  0 pio-usb-v4-enumeration-last-exception !
  0 pio-usb-v4-enumerated-address !
  0 pio-usb-v4-enumerated-configuration !
  0 pio-usb-v4-enumerated-vid !
  0 pio-usb-v4-enumerated-pid !
  0 pio-usb-v4-enumerated-ep0-mps !
  0 pio-usb-v4-enumerated-epoch !
  0 pio-usb-v4-enumerated-configuration-length !
  0 pio-usb-v4-declared-interface-count !
  0 pio-usb-v4-unique-interface-count !
  0 pio-usb-v4-interface-descriptor-count !
  0 pio-usb-v4-endpoint-descriptor-count !
  0 pio-usb-v4-other-descriptor-count !
  pio-usb-v4-enumerated-device-descriptor
  pio-usb-v4-device-descriptor-size 0 fill
  pio-usb-v4-enumerated-configuration-header
  pio-usb-v4-configuration-header-size 0 fill
  pio-usb-v4-enumerated-configuration-descriptors
  pio-usb-v4-control-data-size 0 fill
  pio-usb-v4-interface-seen 256 0 fill
  pio-usb-v4-interface-records
  pio-usb-v4-max-interface-descriptors
  pio-usb-v4-interface-record-size * 0 fill
  pio-usb-v4-endpoint-records
  pio-usb-v4-max-endpoint-descriptors
  pio-usb-v4-endpoint-record-size * 0 fill
;

: require-pio-usb-v4-device-descriptor-18
  { data length expected-mps -- }
  length pio-usb-v4-device-descriptor-size =
  data c@ pio-usb-v4-device-descriptor-size = and
  data 1+ c@ pio-usb-v4-descriptor-device = and
  data 7 + c@ expected-mps = and
  data 7 + c@ pio-usb-v4-valid-ep0-mps? and
  data 17 + c@ 0<> and
  averts x-pio-usb-v4-invalid-device-descriptor
;

: save-pio-usb-v4-device-descriptor
  { data length expected-mps -- }
  data length expected-mps require-pio-usb-v4-device-descriptor-18
  data pio-usb-v4-enumerated-device-descriptor length move
  data 7 + c@ pio-usb-v4-enumerated-ep0-mps !
  data 8 + pio-usb-v4-le16@ pio-usb-v4-enumerated-vid !
  data 10 + pio-usb-v4-le16@ pio-usb-v4-enumerated-pid !
;

\ Validate the nine-byte prefix and return wTotalLength.  This helper also
\ records the fields which the full descriptor must reproduce exactly.
: save-pio-usb-v4-configuration-header { data length -- total-length }
  length pio-usb-v4-configuration-header-size =
  data c@ pio-usb-v4-configuration-header-size = and
  data 1+ c@ pio-usb-v4-descriptor-configuration = and
  averts x-pio-usb-v4-invalid-configuration
  data 2 + pio-usb-v4-le16@ { total-length }
  total-length pio-usb-v4-configuration-header-size u>=
  total-length pio-usb-v4-control-data-size u<= and
  data 4 + c@ 0<> and
  data 5 + c@ 0<> and
  data 7 + c@ $9F and $80 = and
  averts x-pio-usb-v4-invalid-configuration
  total-length pio-usb-v4-enumerated-configuration-length !
  data 4 + c@ pio-usb-v4-declared-interface-count !
  data 5 + c@ pio-usb-v4-enumerated-configuration !
  data pio-usb-v4-enumerated-configuration-header length move
  total-length
;

: require-pio-usb-v4-full-configuration-header
  { data length -- }
  length pio-usb-v4-enumerated-configuration-length @ =
  data pio-usb-v4-enumerated-configuration-header
  pio-usb-v4-configuration-header-size pio-usb-v4-bytes-equal? and
  averts x-pio-usb-v4-invalid-configuration
;

: close-pio-usb-v4-interface
  { have-interface expected-endpoints seen-endpoints -- }
  have-interface if
    seen-endpoints expected-endpoints =
    averts x-pio-usb-v4-invalid-configuration
  then
;

\ Interface record layout: number, alternate, declared endpoint count, class,
\ subclass, protocol, string index, source-descriptor offset.
: save-pio-usb-v4-interface-record { descriptor offset -- }
  pio-usb-v4-interface-descriptor-count @
  dup pio-usb-v4-max-interface-descriptors u<
  averts x-pio-usb-v4-diagnostic-table-full
  pio-usb-v4-interface-record-size *
  pio-usb-v4-interface-records + { record }
  descriptor 2 + c@ record c!
  descriptor 3 + c@ record 1+ c!
  descriptor 4 + c@ record 2 + c!
  descriptor 5 + c@ record 3 + c!
  descriptor 6 + c@ record 4 + c!
  descriptor 7 + c@ record 5 + c!
  descriptor 8 + c@ record 6 + c!
  offset record 7 + c!
  1 pio-usb-v4-interface-descriptor-count +!
;

: pio-usb-v4-interface-record-exists?
  { interface alternate -- exists? }
  pio-usb-v4-interface-descriptor-count @ 0 ?do
    i pio-usb-v4-interface-record@ { record }
    record c@ interface =
    record 1+ c@ alternate = and if true unloop exit then
  loop
  false
;

: mark-pio-usb-v4-interface { number alternate -- }
  pio-usb-v4-interface-seen number + { seen }
  seen c@ 0= if 1 pio-usb-v4-unique-interface-count +! then
  seen c@ 1 or alternate 0= if 2 or then seen c!
;

\ Endpoint record layout: address, attributes, max-packet LE16, interval,
\ owning interface, owning alternate setting, source-descriptor offset.
: save-pio-usb-v4-endpoint-record
  { descriptor offset interface alternate -- }
  descriptor 2 + c@ { endpoint-address }
  descriptor 3 + c@ { attributes }
  descriptor 4 + pio-usb-v4-le16@ { max-packet }
  endpoint-address $70 and 0=
  endpoint-address $0F and 0<> and
  attributes $C0 and 0= and
  attributes 3 and 0<> and
  attributes 3 and 1 = if
    \ Isochronous endpoints define synchronization and usage in bits 5..2.
    true
  else
    \ Those bits are reserved for control, bulk, and interrupt endpoints.
    attributes $3C and 0=
  then and
  max-packet $F800 and 0= and
  max-packet $07FF and 0<> and
  averts x-pio-usb-v4-invalid-configuration
  pio-usb-v4-endpoint-descriptor-count @
  dup pio-usb-v4-max-endpoint-descriptors u<
  averts x-pio-usb-v4-diagnostic-table-full
  pio-usb-v4-endpoint-record-size *
  pio-usb-v4-endpoint-records + { record }
  endpoint-address record c!
  attributes record 1+ c!
  descriptor 4 + c@ record 2 + c!
  descriptor 5 + c@ record 3 + c!
  descriptor 6 + c@ record 4 + c!
  interface record 5 + c!
  alternate record 6 + c!
  offset record 7 + c!
  1 pio-usb-v4-endpoint-descriptor-count +!
;

: pio-usb-v4-endpoint-record-exists?
  { endpoint-address interface alternate -- exists? }
  pio-usb-v4-endpoint-descriptor-count @ 0 ?do
    i pio-usb-v4-endpoint-record@ { record }
    record c@ endpoint-address =
    record 5 + c@ interface = and
    record 6 + c@ alternate = and if true unloop exit then
  loop
  false
;

: pio-usb-v4-endpoint-owned-by-other-interface?
  { endpoint-address interface -- owned-by-other? }
  pio-usb-v4-endpoint-descriptor-count @ 0 ?do
    i pio-usb-v4-endpoint-record@ { record }
    record c@ endpoint-address =
    record 5 + c@ interface <> and if true unloop exit then
  loop
  false
;

: require-pio-usb-v4-interface-alt-zero ( -- )
  256 0 do
    pio-usb-v4-interface-seen i + c@ dup 0<> if
      2 and 0<> averts x-pio-usb-v4-invalid-configuration
    else
      drop
    then
  loop
;

\ Walk by bLength, rejecting zero/truncated descriptors before inspecting
\ type-specific fields.  Unknown class/vendor descriptors are safely skipped.
: walk-pio-usb-v4-configuration { data total-length -- }
  false { have-interface }
  0 { current-interface }
  0 { current-alternate }
  0 { expected-endpoints }
  0 { seen-endpoints }
  data c@ { offset }
  begin offset total-length u< while
    2 total-length offset - u<=
    averts x-pio-usb-v4-invalid-configuration
    data offset + { descriptor }
    descriptor c@ { descriptor-length }
    2 descriptor-length u<=
    descriptor-length total-length offset - u<= and
    averts x-pio-usb-v4-invalid-configuration
    descriptor 1+ c@ case
      pio-usb-v4-descriptor-configuration of
        false averts x-pio-usb-v4-invalid-configuration
      endof
      pio-usb-v4-descriptor-interface of
        descriptor-length pio-usb-v4-interface-descriptor-size u>=
        averts x-pio-usb-v4-invalid-configuration
        have-interface expected-endpoints seen-endpoints
        close-pio-usb-v4-interface
        descriptor 2 + c@ to current-interface
        descriptor 3 + c@ to current-alternate
        current-interface pio-usb-v4-declared-interface-count @ u<
        averts x-pio-usb-v4-invalid-configuration
        current-interface current-alternate
        pio-usb-v4-interface-record-exists? 0=
        averts x-pio-usb-v4-invalid-configuration
        descriptor offset save-pio-usb-v4-interface-record
        current-interface current-alternate mark-pio-usb-v4-interface
        descriptor 4 + c@ to expected-endpoints
        0 to seen-endpoints
        true to have-interface
      endof
      pio-usb-v4-descriptor-endpoint of
        have-interface averts x-pio-usb-v4-invalid-configuration
        descriptor-length pio-usb-v4-endpoint-descriptor-size u>=
        averts x-pio-usb-v4-invalid-configuration
        descriptor 2 + c@ current-interface current-alternate
        pio-usb-v4-endpoint-record-exists? 0=
        averts x-pio-usb-v4-invalid-configuration
        descriptor 2 + c@ current-interface
        pio-usb-v4-endpoint-owned-by-other-interface? 0=
        averts x-pio-usb-v4-invalid-configuration
        descriptor offset current-interface current-alternate
        save-pio-usb-v4-endpoint-record
        1 +to seen-endpoints
        seen-endpoints expected-endpoints u<=
        averts x-pio-usb-v4-invalid-configuration
      endof
      1 pio-usb-v4-other-descriptor-count +!
    endcase
    descriptor-length +to offset
  repeat
  offset total-length = averts x-pio-usb-v4-invalid-configuration
  have-interface expected-endpoints seen-endpoints
  close-pio-usb-v4-interface
  require-pio-usb-v4-interface-alt-zero
  pio-usb-v4-unique-interface-count @
  pio-usb-v4-declared-interface-count @ =
  averts x-pio-usb-v4-invalid-configuration
;

: require-pio-usb-v4-enumeration-epoch ( session-epoch -- )
  pio-usb-v4-port-epoch @ =
  pio-usb-v4-port-state @ pio-usb-v4-port-active = and
  pio-usb-v4-root-connected @ 0<> and
  pio-usb-v4-root-full-speed @ 0<> and
  averts x-pio-usb-v4-stale-completion
;

: pio-usb-enumerate-root-control-v4-unlocked ( -- )
  clear-pio-usb-v4-enumeration-diagnostics

  pio-usb-v4-enumeration-reset-get-device-8
  pio-usb-v4-enumeration-stage !
  pio-usb-get-device-descriptor-8-v4-unlocked
  { device-8 device-8-length session-epoch }
  device-8 7 + c@ { ep0-mps }

  pio-usb-v4-enumeration-set-address pio-usb-v4-enumeration-stage !
  session-epoch require-pio-usb-v4-enumeration-epoch
  0 ep0-mps 0 pio-usb-v4-request-set-address
  pio-usb-v4-root-device-address 0 pio-usb-control-no-data-v4-unlocked
  2 ms

  pio-usb-v4-enumeration-get-device-18 pio-usb-v4-enumeration-stage !
  session-epoch require-pio-usb-v4-enumeration-epoch
  pio-usb-v4-root-device-address ep0-mps $80
  pio-usb-v4-request-get-descriptor pio-usb-v4-device-descriptor-value
  0 pio-usb-v4-device-descriptor-size pio-usb-control-in-v4-unlocked
  { device-18 device-18-length }
  device-18 device-18-length ep0-mps
  save-pio-usb-v4-device-descriptor

  pio-usb-v4-enumeration-get-configuration-9
  pio-usb-v4-enumeration-stage !
  session-epoch require-pio-usb-v4-enumeration-epoch
  pio-usb-v4-root-device-address ep0-mps $80
  pio-usb-v4-request-get-descriptor
  pio-usb-v4-configuration-descriptor-value 0
  pio-usb-v4-configuration-header-size pio-usb-control-in-v4-unlocked
  save-pio-usb-v4-configuration-header { total-length }

  pio-usb-v4-enumeration-get-configuration-full
  pio-usb-v4-enumeration-stage !
  session-epoch require-pio-usb-v4-enumeration-epoch
  pio-usb-v4-root-device-address ep0-mps $80
  pio-usb-v4-request-get-descriptor
  pio-usb-v4-configuration-descriptor-value 0 total-length
  pio-usb-control-in-v4-unlocked { configuration actual-length }
  configuration actual-length
  require-pio-usb-v4-full-configuration-header
  configuration pio-usb-v4-enumerated-configuration-descriptors
  actual-length move

  pio-usb-v4-enumeration-parse-configuration
  pio-usb-v4-enumeration-stage !
  pio-usb-v4-enumerated-configuration-descriptors actual-length
  walk-pio-usb-v4-configuration

  pio-usb-v4-enumeration-set-configuration
  pio-usb-v4-enumeration-stage !
  session-epoch require-pio-usb-v4-enumeration-epoch
  pio-usb-v4-root-device-address ep0-mps 0
  pio-usb-v4-request-set-configuration
  pio-usb-v4-enumerated-configuration @ 0
  pio-usb-control-no-data-v4-unlocked

  pio-usb-v4-enumeration-get-configuration
  pio-usb-v4-enumeration-stage !
  session-epoch require-pio-usb-v4-enumeration-epoch
  pio-usb-v4-root-device-address ep0-mps $80
  pio-usb-v4-request-get-configuration 0 0 1
  pio-usb-control-in-v4-unlocked { selected selected-length }
  selected-length 1 =
  selected c@ pio-usb-v4-enumerated-configuration @ = and
  averts x-pio-usb-v4-invalid-configuration

  session-epoch require-pio-usb-v4-enumeration-epoch
  session-epoch pio-usb-v4-enumerated-epoch !
  pio-usb-v4-root-device-address pio-usb-v4-enumerated-address !
  true pio-usb-v4-enumerated !
  pio-usb-v4-enumeration-complete pio-usb-v4-enumeration-stage !
;

\ Enumerate once and return stable summary values.  The inner try records the
\ exact exception while the outer ABI-v4 command-lock helper always unlocks.
: execute-pio-usb-enumerate-root-control-v4 ( -- )
  ['] pio-usb-enumerate-root-control-v4-unlocked try
  dup pio-usb-v4-enumeration-last-exception !
  ?raise
;

: pio-usb-enumerate-root-control-v4 ( -- address configuration vid pid )
  [: execute-pio-usb-enumerate-root-control-v4 ;]
  with-pio-usb-v4-command-lock
  pio-usb-v4-enumerated-address @
  pio-usb-v4-enumerated-configuration @
  pio-usb-v4-enumerated-vid @
  pio-usb-v4-enumerated-pid @
;

\ Initialize only RAM-local diagnostics; no USB or core-1 action is performed.
clear-pio-usb-v4-enumeration-diagnostics
