\ Bounded BleuIO command layer for the RP2350 PIO USB ABI-v4 CDC host.
\
\ Load enumeration_v4.fs, full_enumeration_v4.fs, and cdc_acm_v4.fs first.
\ Open and enumerate the root device before invoking bleuio::open.
\
\ This file is definitions-only.  Loading it allocates and initializes
\ RAM-local parser state and a lock; it performs no USB I/O.  The command
\ engine uses the CDC transaction boundary, so one command owns both the CDC
\ class lock and the global ABI-v4 mailbox lock from write through response.
\
\ Responses are retained verbatim, including CRLF framing and an optional
\ command echo.  The parser accepts the default-mode OK/ERROR terminals and
\ ATV1's transitional VERBOSE ON terminal.  In verbose mode it correlates C,
\ A, and E records by command index and treats a nonzero A.err as a normal
\ protocol result rather than a transport exception.
\
\ This first milestone is deliberately synchronous.  It does not start a
\ scan, retain asynchronous events, or create a background receive task.

defined? pio-usb-host-persistent-build [if]
  compile-to-flash
[else]
  compile-to-ram
[then]

begin-module bleuio

  slock import

  0 constant result-none
  1 constant result-ok
  2 constant result-error

  begin-module bleuio-internal

    192 constant command-capacity
    2048 constant response-capacity
    512 constant line-capacity
    256 constant read-capacity

    100 constant write-timeout-frames
    10 constant read-timeout-frames
    300 constant command-poll-limit

    variable command-length
    variable response-length
    variable line-length
    variable command-result
    variable command-error-code
    variable verbose-command-index
    variable verbose-command-seen
    variable verbose-ack-seen
    variable response-complete
    variable response-overflow
    variable line-overflow
    variable malformed-response
    variable resync-required
    variable bootstrap-complete

    command-capacity 2 + buffer: command-buffer
    response-capacity buffer: response-buffer
    line-capacity buffer: line-buffer
    read-capacity buffer: read-buffer
    slock-size buffer: command-slock

    : x-command-busy ( -- )
      ." another BleuIO command operation is active" cr
    ;

    : x-invalid-command ( -- )
      ." invalid BleuIO command" cr
    ;

    : x-response-timeout ( -- )
      ." BleuIO command response did not terminate within its bounded poll limit" cr
    ;

    : x-response-overflow ( -- )
      ." BleuIO command response exceeded its bounded local buffer" cr
    ;

    : x-malformed-response ( -- )
      ." malformed BleuIO verbose command response" cr
    ;

    : x-resync-required ( -- )
      ." BleuIO receive stream requires target reset and re-enumeration" cr
    ;

    : with-command-lock ( xt -- )
      command-slock try-claim-slock
      averts x-command-busy
      try
      command-slock release-slock
      ?raise
    ;

    : clear-response-state ( -- )
      0 response-length !
      0 line-length !
      result-none command-result !
      0 command-error-code !
      0 verbose-command-index !
      false verbose-command-seen !
      false verbose-ack-seen !
      false response-complete !
      false response-overflow !
      false line-overflow !
      false malformed-response !
      response-buffer response-capacity 0 fill
      line-buffer line-capacity 0 fill
    ;

    : command-valid? { data length -- valid? }
      length 0>
      length command-capacity u<= and
      0= if false exit then
      length 0 ?do
        data i + c@ dup $0D = swap $0A = or if
          false unloop exit
        then
      loop
      true
    ;

    : prepare-command { data length -- }
      data length command-valid?
      averts x-invalid-command
      data command-buffer length move
      $0D command-buffer length + c!
      $0A command-buffer length 1+ + c!
      length command-length !
      clear-response-state
    ;

    : line= { data length expected expected-length -- equal? }
      data length expected expected-length equal-strings?
    ;

    : line-starts-with?
      { data length prefix prefix-length -- starts? }
      length prefix-length u< if false exit then
      data prefix-length prefix prefix-length equal-strings?
    ;

    : decimal-prefix@
      { data length -- value consumed valid? }
      0 { value }
      0 { consumed }
      begin consumed length u< while
        data consumed + c@ { character }
        character [char] 0 >= character [char] 9 <= and 0= if
          value consumed consumed 0> exit
        then
        \ Command indices and BleuIO error codes are small.  This bound also
        \ prevents an untrusted, overlong digit string from wrapping a cell.
        consumed 9 u>= if 0 0 false exit then
        value 10 * character [char] 0 - + to value
        1 +to consumed
      repeat
      value consumed consumed 0>
    ;

    : json-record-type? { data length record-type -- record? }
      length 7 u>=
      data c@ [char] { = and
      data 1+ c@ [char] " = and
      data 2 + c@ record-type = and
      data 3 + c@ [char] " = and
      data 4 + c@ [char] : = and
    ;

    : json-record-index@
      { data length record-type -- index valid? }
      data length record-type json-record-type? 0= if
        0 false exit
      then
      data 5 + length 5 - decimal-prefix@
      { index consumed valid? }
      valid? 0= if 0 false exit then
      consumed length 5 - u< 0= if 0 false exit then
      data 5 + consumed + c@ [char] , = if
        index true
      else
        0 false
      then
    ;

    : error-marker-at? { data length offset -- marker? }
      6 length offset - u<=
      offset length u<= and 0= if false exit then
      data offset + c@ [char] " =
      data offset 1+ + c@ [char] e = and
      data offset 2 + + c@ [char] r = and
      data offset 3 + + c@ [char] r = and
      data offset 4 + + c@ [char] " = and
      data offset 5 + + c@ [char] : = and
    ;

    : json-error-code@ { data length -- error-code valid? }
      length 6 u< if 0 false exit then
      length 5 - 0 ?do
        data length i error-marker-at? if
          data i 6 + + length i 6 + - decimal-prefix@
          { error-code consumed valid? }
          consumed drop
          error-code valid?
          unloop exit
        then
      loop
      0 false
    ;

    : echoed-command? { data length -- echo? }
      data length command-buffer command-length @ line=
    ;

    : default-error-line? { data length -- error? }
      data length s" ERROR" line-starts-with? 0= if false exit then
      length 5 = if true exit then
      data 5 + c@ dup bl = swap [char] : = or
    ;

    : process-command-record { data length -- }
      verbose-command-seen @ if exit then
      data length [char] C json-record-index@
      { index valid? }
      valid? if
        index verbose-command-index !
        true verbose-command-seen !
      then
    ;

    : process-ack-record { data length -- }
      verbose-command-seen @ 0= if exit then
      data length [char] A json-record-index@
      { index valid? }
      valid? 0= if exit then
      index verbose-command-index @ <> if exit then
      data length json-error-code@
      { error-code error-valid? }
      error-valid? if
        error-code command-error-code !
        true verbose-ack-seen !
      else
        true malformed-response !
      then
    ;

    : process-end-record { data length -- }
      verbose-command-seen @ 0= if exit then
      data length [char] E json-record-index@
      { index valid? }
      valid? 0= if exit then
      index verbose-command-index @ <> if exit then
      verbose-ack-seen @ if
        command-error-code @ 0= if result-ok else result-error then
        command-result !
      else
        true malformed-response !
      then
      true response-complete !
    ;

    : process-line { data length -- }
      response-complete @ if exit then
      length 0= if exit then
      data length echoed-command? if exit then
      data length s" OK" line= if
        result-ok command-result !
        0 command-error-code !
        true response-complete !
        exit
      then
      data length default-error-line? if
        result-error command-result !
        -1 command-error-code !
        true response-complete !
        exit
      then
      \ ATV1 changes formats while it is replying, so its successful terminal
      \ is neither default-mode OK nor a verbose-mode E record.
      data length s" VERBOSE ON" line= if
        result-ok command-result !
        0 command-error-code !
        true response-complete !
        exit
      then
      data length process-command-record
      data length process-ack-record
      data length process-end-record
    ;

    : append-response-byte ( byte -- )
      response-length @ response-capacity u< if
        response-buffer response-length @ + c!
        1 response-length +!
      else
        drop
        true response-overflow !
      then
    ;

    : append-line-byte ( byte -- )
      line-length @ line-capacity u< if
        line-buffer line-length @ + c!
        1 line-length +!
      else
        drop
        true line-overflow !
        true response-overflow !
      then
    ;

    : finish-line ( -- )
      line-overflow @ if
        \ The matching E record is short and will arrive on a later line; keep
        \ draining even though this oversized line cannot be classified.
        false line-overflow !
      else
        line-length @ { length }
        length 0> if
          line-buffer length 1- + c@ $0D = if -1 +to length then
        then
        line-buffer length process-line
      then
      0 line-length !
    ;

    : consume-response-byte ( byte -- )
      response-complete @ if drop exit then
      dup append-response-byte
      dup $0A = if
        drop finish-line
      else
        append-line-byte
      then
    ;

    : consume-response { data length -- }
      length 0 ?do
        response-complete @ if unloop exit then
        data i + c@ consume-response-byte
      loop
    ;

    : do-command-transaction ( -- )
      command-buffer command-length @ 2 + write-timeout-frames
      cdc-acm-v4-write-all-unlocked

      0 { polls }
      begin
        response-complete @ 0=
        polls command-poll-limit u< and
      while
        read-buffer read-capacity read-timeout-frames
        cdc-acm-v4-read-unlocked { received }
        received 0> if read-buffer received consume-response then
        1 +to polls
      repeat

      response-complete @ 0= if
        true resync-required !
        ['] x-response-timeout ?raise
      then
      malformed-response @ if ['] x-malformed-response ?raise then
      response-overflow @ if ['] x-response-overflow ?raise then
    ;

    : execute-command { data length -- success? }
      resync-required @ 0=
      averts x-resync-required
      data length prepare-command
      ['] do-command-transaction
      ['] with-cdc-acm-v4-transaction try { exception }
      exception 0<> if
        \ Any transport failure may have occurred after some command bytes
        \ were accepted.  Do not blindly publish another command on that RX
        \ stream.  Keep the latch across ordinary close/open; recover with a
        \ target reset and fresh enumeration.  A fully drained local overflow
        \ is raised inside the transaction and deliberately remains synced.
        exception ['] x-response-overflow <> if
          true resync-required !
        then
        exception ?raise
      then
      command-result @ result-ok =
    ;

    : execute-bootstrap ( -- success? )
      false bootstrap-complete !
      s" ATV1" execute-command 0= if false exit then
      s" ATE0" execute-command 0= if false exit then
      s" ATA0" execute-command 0= if false exit then
      s" ATEW0" execute-command 0= if false exit then
      s" ATDS0" execute-command 0= if false exit then
      true bootstrap-complete !
      true
    ;

    : execute-open ( -- success? )
      resync-required @ 0=
      averts x-resync-required
      open-cdc-acm-v4
      false bootstrap-complete !
      execute-bootstrap
    ;

    : execute-close ( -- )
      false bootstrap-complete !
      close-cdc-acm-v4
    ;

  end-module> import

  \ Return the most recently retained raw response.  It remains valid until
  \ the next command starts; callers must copy it before issuing that command.
  : response@ ( -- data length )
    response-buffer response-length @
  ;

  : result@ ( -- result ) command-result @ ;

  \ Verbose errors return the numeric A.err value.  A default-mode ERROR has
  \ no numeric code and returns -1.  Success and no-result states return zero.
  : error-code@ ( -- error-code ) command-error-code @ ;

  \ Once set, this latch survives close/open.  This milestone has no general
  \ RX-drain/resynchronization engine; reset and freshly enumerate the target.
  : resync-required? ( -- required? ) resync-required @ ;

  : bootstrap-complete? ( -- complete? ) bootstrap-complete @ ;

  : .response ( -- )
    response-length @ 0= if
      ." (empty BleuIO response)" cr
      exit
    then
    response-buffer response-length @ type
    response-buffer response-length @ 1- + c@ $0A <> if cr then
    response-overflow @ if ." [BleuIO response truncated]" cr then
  ;

  \ Send one command without CR or LF.  CRLF is appended.  The complete raw
  \ response is retained for response@/.response.  Return true for OK,
  \ VERBOSE ON, or a verbose A.err=0 plus matching E; return false for ERROR
  \ or nonzero A.err.  Transport, framing, timeout, and capacity failures raise.
  : command ( data length -- success? )
    ['] execute-command with-command-lock
  ;

  : at ( -- success? ) s" AT" command ;
  : ati ( -- success? ) s" ATI" command ;
  : central ( -- success? ) s" AT+CENTRAL" command ;
  : peripheral ( -- success? ) s" AT+PERIPHERAL" command ;
  : gap-status ( -- success? ) s" AT+GAPSTATUS" command ;

  \ Select the machine-readable response mode and deterministic low-noise
  \ settings used by later BleuIO Forth layers.  Stop on the first protocol
  \ ERROR and leave that command's response available to the caller.  The
  \ complete sequence is atomic with respect to every other BleuIO command.
  : bootstrap ( -- success? )
    ['] execute-bootstrap with-command-lock
  ;

  \ Open the descriptor-derived CDC ACM function and apply the BleuIO
  \ bootstrap.  If the physical bulk pipes are retained in the current port
  \ epoch, reuse them so their host/device data toggles remain continuous, but
  \ always run the bootstrap again.  A protocol ERROR returns false and
  \ deliberately leaves CDC logically open so response@ can be inspected and
  \ a raw recovery command can be sent.
  : open ( -- success? )
    ['] execute-open with-command-lock
  ;

  \ Logically close and quiesce BleuIO.  CDC keeps a complete current pair of
  \ physical bulk pipes for the next open; detach or an epoch change causes
  \ that stale local pipe state to be discarded without publishing CLOSE.
  : close ( -- )
    ['] execute-close with-command-lock
  ;

  : init-bleuio-state ( -- )
    command-slock init-slock
    false resync-required !
    false bootstrap-complete !
    0 command-length !
    clear-response-state
  ;

  initializer init-bleuio-state

end-module
