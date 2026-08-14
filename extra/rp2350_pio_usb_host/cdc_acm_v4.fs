\ Forth-owned CDC ACM class helper for the RP2350 PIO USB ABI-v4 host.
\
\ Load enumeration_v4.fs and full_enumeration_v4.fs first, launch the audited
\ ABI-v4 core 1 image exactly once after reset, and complete root enumeration
\ before calling open-cdc-acm-v4.
\
\ This file is definitions-only.  Loading it initializes RAM-local state and a
\ simple lock, but does not enumerate, issue a USB request, or open an endpoint.
\
\ The class parser walks the stable full-configuration copy.  It selects a CDC
\ ACM communication interface through its ACM and Union functional descriptors,
\ follows the Union subordinate interface number, and derives one bulk OUT and
\ one bulk IN endpoint from that data interface's alternate setting zero.  No
\ endpoint number is hard-coded and the notification interrupt endpoint is not
\ opened by this milestone.
\
\ Every multi-command operation owns both the class lock and the ABI-v4 command
\ lock.  Only *-v4-unlocked mailbox words may be called from those operations.
\ Shared IN data is copied before another command can publish; no shared-buffer
\ pointer is retained in class state.

compile-to-ram

slock import

\ USB descriptor, class, and request values used here.
$02 constant cdc-acm-v4-communications-class
$02 constant cdc-acm-v4-acm-subclass
$0A constant cdc-acm-v4-data-class
$24 constant cdc-acm-v4-class-interface-descriptor
$02 constant cdc-acm-v4-acm-functional-subtype
$06 constant cdc-acm-v4-union-functional-subtype
$02 constant cdc-acm-v4-acm-line-capability

$21 constant cdc-acm-v4-host-to-interface-class
$20 constant cdc-acm-v4-request-set-line-coding
$22 constant cdc-acm-v4-request-set-control-line-state
$0003 constant cdc-acm-v4-dtr-rts

4 constant cdc-acm-v4-at-command-length
7 constant cdc-acm-v4-line-coding-length
256 constant cdc-acm-v4-response-capacity
100 constant cdc-acm-v4-out-timeout-frames
10 constant cdc-acm-v4-in-poll-timeout-frames
100 constant cdc-acm-v4-in-poll-limit

\ Persistent class/session state.  Endpoint addresses and packet sizes are
\ descriptor-derived.  No variable below ever contains pio-usb-v4-data.
variable cdc-acm-v4-initialized
variable cdc-acm-v4-address
variable cdc-acm-v4-ep0-mps
variable cdc-acm-v4-epoch
variable cdc-acm-v4-communication-interface
variable cdc-acm-v4-data-interface
variable cdc-acm-v4-bulk-out-endpoint
variable cdc-acm-v4-bulk-out-mps
variable cdc-acm-v4-bulk-out-interval
variable cdc-acm-v4-bulk-in-endpoint
variable cdc-acm-v4-bulk-in-mps
variable cdc-acm-v4-bulk-in-interval
variable cdc-acm-v4-bulk-out-open
variable cdc-acm-v4-bulk-in-open
variable cdc-acm-v4-response-length
variable cdc-acm-v4-terminal-timeout

cdc-acm-v4-at-command-length buffer: cdc-acm-v4-at-command
cdc-acm-v4-line-coding-length buffer: cdc-acm-v4-line-coding
cdc-acm-v4-response-capacity buffer: cdc-acm-v4-response
slock-size buffer: cdc-acm-v4-slock

\ Scratch state used only while the class lock is held.
variable cdc-acm-v4-scan-interface-active
variable cdc-acm-v4-scan-interface-count
variable cdc-acm-v4-scan-interface-valid
variable cdc-acm-v4-scan-declared-endpoints
variable cdc-acm-v4-scan-endpoint-count
variable cdc-acm-v4-scan-endpoints-valid
variable cdc-acm-v4-scan-out-count
variable cdc-acm-v4-scan-out-endpoint
variable cdc-acm-v4-scan-out-mps
variable cdc-acm-v4-scan-out-interval
variable cdc-acm-v4-scan-in-count
variable cdc-acm-v4-scan-in-endpoint
variable cdc-acm-v4-scan-in-mps
variable cdc-acm-v4-scan-in-interval

: x-cdc-acm-v4-busy ( -- )
  ." another CDC ACM class operation is active" cr
;

: x-cdc-acm-v4-not-enumerated ( -- )
  ." complete ABI-v4 root enumeration before opening CDC ACM" cr
;

: x-cdc-acm-v4-invalid-configuration ( -- )
  ." invalid CDC ACM configuration descriptor tree" cr
;

: x-cdc-acm-v4-not-found ( -- )
  ." no supported CDC ACM Union/data-interface pair was found" cr
;

: x-cdc-acm-v4-already-open ( -- )
  ." CDC ACM is already open or still requires cleanup" cr
;

: x-cdc-acm-v4-not-open ( -- )
  ." CDC ACM is not open for the current USB port epoch" cr
;

: x-cdc-acm-v4-transfer-failed ( -- )
  ." CDC ACM bulk transfer did not complete as required" cr
;

: x-cdc-acm-v4-response-overflow ( -- )
  ." CDC ACM response exceeded its bounded local buffer" cr
;

: x-cdc-acm-v4-ok-timeout ( -- )
  ." CDC ACM response did not contain a complete OK line" cr
;

: with-cdc-acm-v4-lock ( xt -- )
  cdc-acm-v4-slock try-claim-slock
  averts x-cdc-acm-v4-busy
  try
  cdc-acm-v4-slock release-slock
  ?raise
;

: clear-cdc-acm-v4-local-state ( -- )
  false cdc-acm-v4-initialized !
  0 cdc-acm-v4-address !
  0 cdc-acm-v4-ep0-mps !
  0 cdc-acm-v4-epoch !
  0 cdc-acm-v4-communication-interface !
  0 cdc-acm-v4-data-interface !
  0 cdc-acm-v4-bulk-out-endpoint !
  0 cdc-acm-v4-bulk-out-mps !
  0 cdc-acm-v4-bulk-out-interval !
  0 cdc-acm-v4-bulk-in-endpoint !
  0 cdc-acm-v4-bulk-in-mps !
  0 cdc-acm-v4-bulk-in-interval !
  false cdc-acm-v4-bulk-out-open !
  false cdc-acm-v4-bulk-in-open !
  0 cdc-acm-v4-response-length !
  cdc-acm-v4-response cdc-acm-v4-response-capacity 0 fill
;

\ A host-side wait timeout leaves request/completion ownership unknown.  The
\ ABI-v4 transport forbids every later publication until board reset, so this
\ latch is deliberately not cleared by ordinary local-state cleanup.
: cdc-acm-v4-terminal-timeout? ( exception -- timeout? )
  ['] x-pio-usb-v4-timeout =
;

: invalidate-cdc-acm-v4-after-timeout ( -- )
  clear-cdc-acm-v4-local-state
  true cdc-acm-v4-terminal-timeout !
;

: require-cdc-acm-v4-mailbox-usable ( -- )
  cdc-acm-v4-terminal-timeout @ 0=
  averts x-pio-usb-v4-timeout
;

: prepare-cdc-acm-v4-static-data ( -- )
  $41 cdc-acm-v4-at-command c!
  $54 cdc-acm-v4-at-command 1+ c!
  $0D cdc-acm-v4-at-command 2 + c!
  $0A cdc-acm-v4-at-command 3 + c!

  \ 115200 baud, one stop bit, no parity, eight data bits.
  $00 cdc-acm-v4-line-coding c!
  $C2 cdc-acm-v4-line-coding 1+ c!
  $01 cdc-acm-v4-line-coding 2 + c!
  $00 cdc-acm-v4-line-coding 3 + c!
  $00 cdc-acm-v4-line-coding 4 + c!
  $00 cdc-acm-v4-line-coding 5 + c!
  $08 cdc-acm-v4-line-coding 6 + c!
;

\ Validate the stable configuration prefix and the current enumeration epoch.
: require-cdc-acm-v4-enumeration ( -- )
  pio-usb-v4-enumeration-complete?
  averts x-cdc-acm-v4-not-enumerated
  pio-usb-v4-enumerated-configuration-length @ { length }
  length pio-usb-v4-configuration-header-size u>=
  length pio-usb-v4-control-data-size u<= and
  averts x-cdc-acm-v4-invalid-configuration
  pio-usb-v4-enumerated-configuration-descriptors { configuration }
  configuration c@ pio-usb-v4-configuration-header-size =
  configuration 1+ c@ pio-usb-v4-descriptor-configuration = and
  configuration 2 + pio-usb-v4-le16@ length = and
  averts x-cdc-acm-v4-invalid-configuration
  pio-usb-v4-enumerated-epoch @
  require-pio-usb-v4-enumeration-epoch
;

\ Return one framed descriptor from the stable full-configuration copy.
: cdc-acm-v4-descriptor@
  { offset -- descriptor descriptor-length }
  pio-usb-v4-enumerated-configuration-length @ { total-length }
  offset total-length u<
  averts x-cdc-acm-v4-invalid-configuration
  2 total-length offset - u<=
  averts x-cdc-acm-v4-invalid-configuration
  pio-usb-v4-enumerated-configuration-descriptors offset + { descriptor }
  descriptor c@ { descriptor-length }
  descriptor-length 2 u>=
  descriptor-length total-length offset - u<= and
  averts x-cdc-acm-v4-invalid-configuration
  descriptor descriptor-length
;

: cdc-acm-v4-communication-interface?
  { descriptor descriptor-length -- communication? }
  descriptor-length pio-usb-v4-interface-descriptor-size u< if
    false exit
  then
  descriptor 1+ c@ pio-usb-v4-descriptor-interface =
  descriptor 3 + c@ 0= and
  descriptor 5 + c@ cdc-acm-v4-communications-class = and
  descriptor 6 + c@ cdc-acm-v4-acm-subclass = and
;

\ Find the one ACM and one Union functional descriptor owned by a candidate
\ communication interface.  The returned offset/length are meaningful only
\ when valid? is true.
: find-cdc-acm-v4-functional-descriptors
  { interface-offset -- union-offset union-length valid? }
  interface-offset cdc-acm-v4-descriptor@
  { interface interface-length }
  interface 2 + c@ { interface-number }
  interface-offset interface-length + { offset }
  0 { acm-count }
  false { acm-capable }
  0 { union-count }
  false { union-valid }
  0 { union-offset }
  0 { union-length }
  pio-usb-v4-enumerated-configuration-length @ { total-length }
  begin offset total-length u< while
    offset cdc-acm-v4-descriptor@ { descriptor descriptor-length }
    descriptor 1+ c@ pio-usb-v4-descriptor-interface = if
      total-length to offset
    else
      descriptor 1+ c@ cdc-acm-v4-class-interface-descriptor =
      descriptor-length 3 u>= and if
        descriptor 2 + c@ cdc-acm-v4-acm-functional-subtype = if
          1 +to acm-count
          descriptor-length 4 = if
            descriptor 3 + c@
            cdc-acm-v4-acm-line-capability and 0<>
            to acm-capable
          else
            false to acm-capable
          then
        else
          descriptor 2 + c@ cdc-acm-v4-union-functional-subtype = if
            1 +to union-count
            descriptor-length 5 u>= if
              descriptor 3 + c@ interface-number =
              to union-valid
              offset to union-offset
              descriptor-length to union-length
            else
              false to union-valid
            then
          then
        then
      then
      descriptor-length +to offset
    then
  repeat
  union-offset union-length
  acm-count 1 = acm-capable and
  union-count 1 = and union-valid and
;

: clear-cdc-acm-v4-data-scan ( -- )
  false cdc-acm-v4-scan-interface-active !
  0 cdc-acm-v4-scan-interface-count !
  false cdc-acm-v4-scan-interface-valid !
  0 cdc-acm-v4-scan-declared-endpoints !
  0 cdc-acm-v4-scan-endpoint-count !
  true cdc-acm-v4-scan-endpoints-valid !
  0 cdc-acm-v4-scan-out-count !
  0 cdc-acm-v4-scan-out-endpoint !
  0 cdc-acm-v4-scan-out-mps !
  0 cdc-acm-v4-scan-out-interval !
  0 cdc-acm-v4-scan-in-count !
  0 cdc-acm-v4-scan-in-endpoint !
  0 cdc-acm-v4-scan-in-mps !
  0 cdc-acm-v4-scan-in-interval !
;

: save-cdc-acm-v4-scanned-endpoint
  { descriptor descriptor-length -- }
  1 cdc-acm-v4-scan-endpoint-count +!
  descriptor-length pio-usb-v4-endpoint-descriptor-size u< if
    false cdc-acm-v4-scan-endpoints-valid !
    exit
  then
  descriptor 2 + c@ { endpoint-address }
  descriptor 3 + c@ { attributes }
  descriptor 4 + pio-usb-v4-le16@ { max-packet }
  descriptor 6 + c@ { interval }
  endpoint-address $70 and 0=
  endpoint-address $0F and 0<> and
  attributes pio-usb-v4-endpoint-attribute-bulk = and
  max-packet pio-usb-v4-valid-bulk-mps? and
  0= if
    false cdc-acm-v4-scan-endpoints-valid !
    exit
  then
  endpoint-address $80 and 0<> if
    1 cdc-acm-v4-scan-in-count +!
    endpoint-address cdc-acm-v4-scan-in-endpoint !
    max-packet cdc-acm-v4-scan-in-mps !
    interval cdc-acm-v4-scan-in-interval !
  else
    1 cdc-acm-v4-scan-out-count +!
    endpoint-address cdc-acm-v4-scan-out-endpoint !
    max-packet cdc-acm-v4-scan-out-mps !
    interval cdc-acm-v4-scan-out-interval !
  then
;

\ Scan one Union subordinate interface.  It must have exactly one alternate
\ setting zero descriptor of class $0A and exactly two bulk endpoints, one in
\ each direction.
: scan-cdc-acm-v4-data-interface ( interface-number -- valid? )
  { interface-number -- valid? }
  clear-cdc-acm-v4-data-scan
  pio-usb-v4-configuration-header-size { offset }
  pio-usb-v4-enumerated-configuration-length @ { total-length }
  begin offset total-length u< while
    offset cdc-acm-v4-descriptor@ { descriptor descriptor-length }
    descriptor 1+ c@ case
      pio-usb-v4-descriptor-interface of
        false cdc-acm-v4-scan-interface-active !
        descriptor-length pio-usb-v4-interface-descriptor-size u>= if
          descriptor 2 + c@ interface-number =
          descriptor 3 + c@ 0= and if
            1 cdc-acm-v4-scan-interface-count +!
            true cdc-acm-v4-scan-interface-active !
            descriptor 5 + c@ cdc-acm-v4-data-class =
            cdc-acm-v4-scan-interface-valid !
            descriptor 4 + c@
            cdc-acm-v4-scan-declared-endpoints !
          then
        then
      endof
      pio-usb-v4-descriptor-endpoint of
        cdc-acm-v4-scan-interface-active @ if
          descriptor descriptor-length
          save-cdc-acm-v4-scanned-endpoint
        then
      endof
    endcase
    descriptor-length +to offset
  repeat
  cdc-acm-v4-scan-interface-count @ 1 =
  cdc-acm-v4-scan-interface-valid @ and
  cdc-acm-v4-scan-declared-endpoints @ 2 = and
  cdc-acm-v4-scan-endpoint-count @ 2 = and
  cdc-acm-v4-scan-endpoints-valid @ and
  cdc-acm-v4-scan-out-count @ 1 = and
  cdc-acm-v4-scan-in-count @ 1 = and
;

\ Try one communications-interface candidate.  A Union descriptor may name
\ several subordinate interfaces; exactly one of them must satisfy the data
\ interface requirements above.
: try-cdc-acm-v4-communication-interface
  { descriptor interface-offset -- valid? }
  interface-offset find-cdc-acm-v4-functional-descriptors
  { union-offset union-length functional-valid? }
  functional-valid? 0= if false exit then
  union-offset cdc-acm-v4-descriptor@ drop { union }
  0 { data-interface-count }
  0 { selected-data-interface }
  0 { selected-out-endpoint }
  0 { selected-out-mps }
  0 { selected-out-interval }
  0 { selected-in-endpoint }
  0 { selected-in-mps }
  0 { selected-in-interval }
  union-length 4 ?do
    union i + c@ { data-interface }
    data-interface scan-cdc-acm-v4-data-interface if
      1 +to data-interface-count
      data-interface to selected-data-interface
      cdc-acm-v4-scan-out-endpoint @ to selected-out-endpoint
      cdc-acm-v4-scan-out-mps @ to selected-out-mps
      cdc-acm-v4-scan-out-interval @ to selected-out-interval
      cdc-acm-v4-scan-in-endpoint @ to selected-in-endpoint
      cdc-acm-v4-scan-in-mps @ to selected-in-mps
      cdc-acm-v4-scan-in-interval @ to selected-in-interval
    then
  loop
  data-interface-count 1 <> if false exit then
  descriptor 2 + c@ cdc-acm-v4-communication-interface !
  selected-data-interface cdc-acm-v4-data-interface !
  selected-out-endpoint cdc-acm-v4-bulk-out-endpoint !
  selected-out-mps cdc-acm-v4-bulk-out-mps !
  selected-out-interval cdc-acm-v4-bulk-out-interval !
  selected-in-endpoint cdc-acm-v4-bulk-in-endpoint !
  selected-in-mps cdc-acm-v4-bulk-in-mps !
  selected-in-interval cdc-acm-v4-bulk-in-interval !
  true
;

: discover-cdc-acm-v4 ( -- )
  false { found }
  pio-usb-v4-configuration-header-size { offset }
  pio-usb-v4-enumerated-configuration-length @ { total-length }
  begin offset total-length u< found 0= and while
    offset cdc-acm-v4-descriptor@ { descriptor descriptor-length }
    descriptor descriptor-length cdc-acm-v4-communication-interface? if
      descriptor offset try-cdc-acm-v4-communication-interface
      to found
    then
    descriptor-length +to offset
  repeat
  found averts x-cdc-acm-v4-not-found
;

: cdc-acm-v4-endpoint-session-current? ( -- current? )
  cdc-acm-v4-address @ 0<>
  pio-usb-v4-enumeration-complete? and
  cdc-acm-v4-address @ pio-usb-v4-enumerated-address @ = and
  cdc-acm-v4-ep0-mps @ pio-usb-v4-enumerated-ep0-mps @ = and
  cdc-acm-v4-epoch @ pio-usb-v4-enumerated-epoch @ = and
  cdc-acm-v4-epoch @ pio-usb-v4-port-epoch @ = and
  pio-usb-v4-port-state @ pio-usb-v4-port-active = and
  pio-usb-v4-root-connected @ 0<> and
  pio-usb-v4-root-full-speed @ 0<> and
;

: require-cdc-acm-v4-open ( -- )
  require-cdc-acm-v4-mailbox-usable
  cdc-acm-v4-initialized @
  cdc-acm-v4-bulk-out-open @ and
  cdc-acm-v4-bulk-in-open @ and
  cdc-acm-v4-endpoint-session-current? and
  averts x-cdc-acm-v4-not-open
;

\ Execute a higher-level CDC transaction while exclusively owning both the
\ class/session lock and the one ABI-v4 mailbox lock.  The supplied xt may use
\ only CDC words documented as *-unlocked; calling a public CDC operation from
\ it would recursively claim these non-blocking locks and fail.
\
\ Catching a host-side mailbox wait timeout here is essential.  Such a timeout
\ leaves publication/completion ownership unknown, so invalidate the local
\ session and latch the mailbox unusable before either lock is released.
: execute-cdc-acm-v4-transaction-unlocked ( xt -- )
  require-cdc-acm-v4-mailbox-usable
  try { exception }
  exception cdc-acm-v4-terminal-timeout? if
    invalidate-cdc-acm-v4-after-timeout
  then
  exception ?raise
;

: cdc-acm-v4-transaction-with-command-lock ( xt -- )
  ['] execute-cdc-acm-v4-transaction-unlocked
  with-pio-usb-v4-command-lock
;

: with-cdc-acm-v4-transaction ( xt -- )
  ['] cdc-acm-v4-transaction-with-command-lock
  with-cdc-acm-v4-lock
;

: require-cdc-acm-v4-transfer-timeout ( timeout-frames -- )
  dup 0>
  swap pio-usb-v4-endpoint-timeout-max-frames u<= and
  averts x-pio-usb-v4-invalid-argument
;

\ Write the complete caller-owned byte range.  One mailbox publication can
\ carry at most 256 bytes; longer ranges are split, and a PARTIAL completion is
\ resumed without losing the endpoint's transport-owned data toggle.  The
\ timeout applies independently to each bounded publication.  A transport
\ timeout with no progress cannot satisfy write-all and is reported as a CDC
\ transfer failure (unlike the terminal host-side wait timeout latched above).
: cdc-acm-v4-write-all-unlocked
  { data length timeout-frames -- }
  require-cdc-acm-v4-open
  timeout-frames require-cdc-acm-v4-transfer-timeout
  begin length 0> while
    length pio-usb-v4-control-data-size min { requested }
    data requested
    cdc-acm-v4-address @ cdc-acm-v4-bulk-out-endpoint @
    timeout-frames
    pio-usb-endpoint-out-v4-unlocked { actual status }
    status pio-usb-v4-command-ok =
    status pio-usb-v4-command-partial = or
    actual 0> and
    actual requested u<= and
    averts x-cdc-acm-v4-transfer-failed
    actual +to data
    actual negate +to length
  repeat
;

\ Perform one bounded IN publication and copy its ephemeral shared-buffer data
\ before returning.  At most min(capacity, 256) bytes are requested.  A normal
\ endpoint no-data timeout returns zero; OK/PARTIAL data returns its copied byte
\ count.  This word never exposes pio-usb-v4-data to its caller.
: cdc-acm-v4-read-unlocked
  { destination capacity timeout-frames -- received }
  require-cdc-acm-v4-open
  timeout-frames require-cdc-acm-v4-transfer-timeout
  capacity 0= if 0 exit then
  capacity pio-usb-v4-control-data-size min { requested }
  cdc-acm-v4-address @ cdc-acm-v4-bulk-in-endpoint @
  requested timeout-frames
  pio-usb-endpoint-in-v4-unlocked { data actual status }
  status pio-usb-v4-command-error-timeout = if
    actual 0= averts x-cdc-acm-v4-transfer-failed
    0 exit
  then
  status pio-usb-v4-command-ok =
  status pio-usb-v4-command-partial = or
  actual requested u<= and
  averts x-cdc-acm-v4-transfer-failed
  data destination actual move
  actual
;

\ Stand-alone convenience entry points.  A higher-level protocol operation
\ that needs several writes/reads atomically should instead call the unlocked
\ words from one with-cdc-acm-v4-transaction quotation.
: cdc-acm-v4-write-all ( data length timeout-frames -- )
  ['] cdc-acm-v4-write-all-unlocked
  with-cdc-acm-v4-transaction
;

: cdc-acm-v4-read
  ( destination capacity timeout-frames -- received )
  ['] cdc-acm-v4-read-unlocked
  with-cdc-acm-v4-transaction
;

: close-cdc-acm-v4-in-unlocked ( -- )
  cdc-acm-v4-address @ cdc-acm-v4-bulk-in-endpoint @
  pio-usb-endpoint-close-v4-unlocked
  false cdc-acm-v4-bulk-in-open !
;

: close-cdc-acm-v4-out-unlocked ( -- )
  cdc-acm-v4-address @ cdc-acm-v4-bulk-out-endpoint @
  pio-usb-endpoint-close-v4-unlocked
  false cdc-acm-v4-bulk-out-open !
;

\ Try both closes and return the first exception.  A flag is cleared only after
\ that endpoint's CLOSE command succeeds, so a failed cleanup remains visible.
: close-cdc-acm-v4-opened-unlocked ( -- exception )
  cdc-acm-v4-terminal-timeout @ if
    ['] x-pio-usb-v4-timeout exit
  then
  0 { first-exception }
  cdc-acm-v4-bulk-in-open @ if
    ['] close-cdc-acm-v4-in-unlocked try { exception }
    exception cdc-acm-v4-terminal-timeout? if
      invalidate-cdc-acm-v4-after-timeout
      exception exit
    then
    exception 0<> first-exception 0= and if
      exception to first-exception
    then
  then
  cdc-acm-v4-bulk-out-open @ if
    ['] close-cdc-acm-v4-out-unlocked try { exception }
    exception cdc-acm-v4-terminal-timeout? if
      invalidate-cdc-acm-v4-after-timeout
      exception exit
    then
    exception 0<> first-exception 0= and if
      exception to first-exception
    then
  then
  first-exception
;

\ The request order is intentionally the order proven with the target modem:
\ SET_CONTROL_LINE_STATE(DTR|RTS), then SET_LINE_CODING(115200 8N1), then
\ persistent bulk OUT and bulk IN opens.  The interrupt endpoint stays closed.
: do-open-cdc-acm-v4-unlocked ( -- )
  require-cdc-acm-v4-mailbox-usable
  cdc-acm-v4-initialized @ 0=
  cdc-acm-v4-bulk-out-open @ 0= and
  cdc-acm-v4-bulk-in-open @ 0= and
  averts x-cdc-acm-v4-already-open
  clear-cdc-acm-v4-local-state
  require-cdc-acm-v4-enumeration
  discover-cdc-acm-v4
  pio-usb-v4-enumerated-address @ cdc-acm-v4-address !
  pio-usb-v4-enumerated-ep0-mps @ cdc-acm-v4-ep0-mps !
  pio-usb-v4-enumerated-epoch @ cdc-acm-v4-epoch !
  prepare-cdc-acm-v4-static-data

  cdc-acm-v4-address @ cdc-acm-v4-ep0-mps @
  cdc-acm-v4-host-to-interface-class
  cdc-acm-v4-request-set-control-line-state
  cdc-acm-v4-dtr-rts cdc-acm-v4-communication-interface @
  pio-usb-control-no-data-v4-unlocked

  cdc-acm-v4-line-coding cdc-acm-v4-line-coding-length
  cdc-acm-v4-address @ cdc-acm-v4-ep0-mps @
  cdc-acm-v4-host-to-interface-class
  cdc-acm-v4-request-set-line-coding
  0 cdc-acm-v4-communication-interface @
  pio-usb-control-out-v4-unlocked

  cdc-acm-v4-address @ cdc-acm-v4-bulk-out-endpoint @
  pio-usb-v4-endpoint-attribute-bulk
  cdc-acm-v4-bulk-out-mps @ cdc-acm-v4-bulk-out-interval @
  pio-usb-endpoint-open-v4-unlocked
  true cdc-acm-v4-bulk-out-open !

  cdc-acm-v4-address @ cdc-acm-v4-bulk-in-endpoint @
  pio-usb-v4-endpoint-attribute-bulk
  cdc-acm-v4-bulk-in-mps @ cdc-acm-v4-bulk-in-interval @
  pio-usb-endpoint-open-v4-unlocked
  true cdc-acm-v4-bulk-in-open !
  true cdc-acm-v4-initialized !
;

: execute-open-cdc-acm-v4-unlocked ( -- )
  ['] do-open-cdc-acm-v4-unlocked try { exception }
  exception 0<> if
    exception cdc-acm-v4-terminal-timeout? if
      invalidate-cdc-acm-v4-after-timeout
      exception ?raise
    then
    close-cdc-acm-v4-opened-unlocked { cleanup-exception }
    cleanup-exception cdc-acm-v4-terminal-timeout? if
      invalidate-cdc-acm-v4-after-timeout
      cleanup-exception ?raise
    then
    false cdc-acm-v4-initialized !
    cdc-acm-v4-bulk-out-open @ 0=
    cdc-acm-v4-bulk-in-open @ 0= and if
      clear-cdc-acm-v4-local-state
    then
    exception ?raise
  then
;

: open-cdc-acm-v4-with-command-lock ( -- )
  ['] execute-open-cdc-acm-v4-unlocked
  with-pio-usb-v4-command-lock
;

: open-cdc-acm-v4 ( -- )
  ['] open-cdc-acm-v4-with-command-lock
  with-cdc-acm-v4-lock
;

: cdc-acm-v4-response-has-ok-line? ( -- ok? )
  cdc-acm-v4-response-length @ { length }
  length 4 u< if false exit then
  length 3 - 0 ?do
    cdc-acm-v4-response i + c@ $4F =
    cdc-acm-v4-response i 1+ + c@ $4B = and
    cdc-acm-v4-response i 2 + + c@ $0D = and
    cdc-acm-v4-response i 3 + + c@ $0A = and
    i 0= if true else cdc-acm-v4-response i 1- + c@ $0A = then
    and if true unloop exit then
  loop
  false
;

: append-cdc-acm-v4-response { data length -- }
  cdc-acm-v4-response-length @ { old-length }
  length cdc-acm-v4-response-capacity old-length - u<=
  averts x-cdc-acm-v4-response-overflow
  \ data is the ephemeral mailbox buffer; consume it before another command.
  data cdc-acm-v4-response old-length + length move
  length cdc-acm-v4-response-length +!
;

: .cdc-acm-v4-response ( -- )
  ." CDC ACM response ("
  cdc-acm-v4-response-length @ .
  ." bytes):" cr
  cdc-acm-v4-response cdc-acm-v4-response-length @ type cr
;

: do-cdc-acm-v4-at-smoke-unlocked ( -- )
  require-cdc-acm-v4-open
  prepare-cdc-acm-v4-static-data
  0 cdc-acm-v4-response-length !
  cdc-acm-v4-response cdc-acm-v4-response-capacity 0 fill

  cdc-acm-v4-at-command cdc-acm-v4-at-command-length
  cdc-acm-v4-address @ cdc-acm-v4-bulk-out-endpoint @
  cdc-acm-v4-out-timeout-frames
  pio-usb-endpoint-out-v4-unlocked { actual status }
  actual cdc-acm-v4-at-command-length =
  status pio-usb-v4-command-ok = and
  averts x-cdc-acm-v4-transfer-failed

  false { ok? }
  0 { polls }
  begin
    ok? 0=
    polls cdc-acm-v4-in-poll-limit u< and
    cdc-acm-v4-response-length @ cdc-acm-v4-response-capacity u< and
  while
    cdc-acm-v4-response-capacity cdc-acm-v4-response-length @ -
    cdc-acm-v4-bulk-in-mps @ min { requested }
    cdc-acm-v4-address @ cdc-acm-v4-bulk-in-endpoint @
    requested cdc-acm-v4-in-poll-timeout-frames
    pio-usb-endpoint-in-v4-unlocked { data received in-status }
    in-status pio-usb-v4-command-error-timeout = if
      received 0= averts x-cdc-acm-v4-transfer-failed
    else
      in-status pio-usb-v4-command-ok =
      in-status pio-usb-v4-command-partial = or
      averts x-cdc-acm-v4-transfer-failed
      received 0> if data received append-cdc-acm-v4-response then
      cdc-acm-v4-response-has-ok-line? to ok?
    then
    1 +to polls
  repeat
  .cdc-acm-v4-response
  ok? averts x-cdc-acm-v4-ok-timeout
;

: execute-cdc-acm-v4-at-smoke-unlocked ( -- )
  ['] do-cdc-acm-v4-at-smoke-unlocked try { exception }
  exception cdc-acm-v4-terminal-timeout? if
    invalidate-cdc-acm-v4-after-timeout
  then
  exception ?raise
;

: cdc-acm-v4-at-smoke-with-command-lock ( -- )
  ['] execute-cdc-acm-v4-at-smoke-unlocked
  with-pio-usb-v4-command-lock
;

\ Send exactly 41 54 0D 0A.  Empty IN polls time out nonfatally; the loop is
\ bounded by 100 ten-frame polls and a 256-byte local response buffer.  A
\ complete line equal to OK\r\n is required, with an optional echoed AT line.
: cdc-acm-v4-at-smoke ( -- )
  ['] cdc-acm-v4-at-smoke-with-command-lock
  with-cdc-acm-v4-lock
;

: do-close-cdc-acm-v4-unlocked ( -- )
  require-cdc-acm-v4-mailbox-usable
  cdc-acm-v4-bulk-out-open @
  cdc-acm-v4-bulk-in-open @ or 0= if
    clear-cdc-acm-v4-local-state
    exit
  then
  \ A changed epoch means core 1 has already discarded its endpoint state.
  cdc-acm-v4-endpoint-session-current? 0= if
    clear-cdc-acm-v4-local-state
    exit
  then
  close-cdc-acm-v4-opened-unlocked { exception }
  exception cdc-acm-v4-terminal-timeout? if
    invalidate-cdc-acm-v4-after-timeout
    exception ?raise
  then
  false cdc-acm-v4-initialized !
  0 cdc-acm-v4-response-length !
  cdc-acm-v4-bulk-out-open @ 0=
  cdc-acm-v4-bulk-in-open @ 0= and if
    clear-cdc-acm-v4-local-state
  then
  exception ?raise
;

: close-cdc-acm-v4-with-command-lock ( -- )
  ['] do-close-cdc-acm-v4-unlocked
  with-pio-usb-v4-command-lock
;

: close-cdc-acm-v4 ( -- )
  ['] close-cdc-acm-v4-with-command-lock
  with-cdc-acm-v4-lock
;

\ Initialize RAM-local serialization and buffers only; no USB action occurs.
cdc-acm-v4-slock init-slock
false cdc-acm-v4-terminal-timeout !
clear-cdc-acm-v4-local-state
prepare-cdc-acm-v4-static-data
