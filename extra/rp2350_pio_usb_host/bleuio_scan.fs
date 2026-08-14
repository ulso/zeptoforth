\ Bounded synchronous BleuIO GAP scan support for the RP2350 PIO USB host.
\
\ Load bleuio.fs first.  This file extends its existing modules but does not
\ perform USB I/O while loading.  A finite scan owns the BleuIO command lock
\ and one CDC transaction from AT+GAPSCAN=<seconds> through the matching SE
\ record.  Consequently no other BleuIO command can split the receive stream.
\
\ Up to 32 complete verbose S records are retained verbatim, without CRLF.
\ Additional records are drained and counted.  The most recent unclassified
\ line and the matching SE line are retained separately for diagnostics.

defined? pio-usb-host-persistent-build [if]
  compile-to-flash
[else]
  compile-to-ram
[then]

continue-module bleuio

  continue-module bleuio-internal

    1 constant min-gap-scan-seconds
    30 constant max-gap-scan-seconds

    32 constant scan-result-capacity
    256 constant scan-record-capacity
    512 constant scan-line-capacity
    256 constant scan-diagnostic-capacity

    2_000_000 constant scan-command-timeout-us
    2_000_000 constant scan-finish-grace-us
    2_000_000 constant scan-stop-drain-us
    500_000 constant scan-trailing-line-drain-us

    variable scan-seconds
    variable scan-result-count
    variable scan-dropped-count
    variable scan-oversize-count
    variable scan-unknown-count
    variable scan-diagnostic-truncated-count
    variable scan-last-unknown-length
    variable scan-end-length
    variable scan-line-length
    variable scan-line-overflow
    variable scan-running
    variable scan-stop-requested
    variable scan-stop-sent
    variable scan-aborted
    variable scan-timed-out
    variable scan-finished
    variable scan-started-at
    variable scan-stop-sent-at
    variable scan-finished-at

    scan-result-capacity cells buffer: scan-result-lengths
    scan-result-capacity scan-record-capacity * buffer: scan-result-buffer
    scan-line-capacity buffer: scan-line-buffer
    scan-diagnostic-capacity buffer: scan-last-unknown-buffer
    scan-diagnostic-capacity buffer: scan-end-buffer
    32 buffer: scan-command-build-buffer
    1 buffer: scan-etx-buffer

    : x-invalid-gap-scan-seconds ( -- )
      ." BleuIO GAP scan duration must be from 1 through 30 seconds" cr
    ;

    : x-scan-not-ready ( -- )
      ." BleuIO must be open and bootstrapped before scanning" cr
    ;

    : x-scan-timeout ( -- )
      ." BleuIO GAP scan did not resynchronize at its matching SE record" cr
    ;

    : x-invalid-scan-result ( -- )
      ." invalid BleuIO scan result index" cr
    ;

    : valid-gap-scan-seconds? ( seconds -- valid? )
      dup min-gap-scan-seconds >=
      swap max-gap-scan-seconds <= and
    ;

    : elapsed-us? { start duration -- elapsed? }
      timer::us-counter-lsb start - duration u>=
    ;

    : line-contains?
      { data length expected expected-length -- found? }
      expected-length 0= if true exit then
      length expected-length u< if false exit then
      length expected-length - 1+ 0 ?do
        data i + expected-length expected expected-length equal-strings? if
          true unloop exit
        then
      loop
      false
    ;

    : scan-end-index@ { data length -- index valid? }
      length 9 u< if 0 false exit then
      data c@ [char] { <>
      data 1+ c@ [char] " <> or
      data 2 + c@ [char] S <> or
      data 3 + c@ [char] E <> or
      data 4 + c@ [char] " <> or
      data 5 + c@ [char] : <> or if
        0 false exit
      then
      data 6 + length 6 - decimal-prefix@
      { index consumed valid? }
      valid? 0= if 0 false exit then
      consumed length 6 - u< 0= if 0 false exit then
      data 6 + consumed + c@ [char] , = if
        index true
      else
        0 false
      then
    ;

    : scan-result-address ( index -- address )
      scan-record-capacity * scan-result-buffer +
    ;

    : clear-scan-storage ( -- )
      0 scan-result-count !
      0 scan-dropped-count !
      0 scan-oversize-count !
      0 scan-unknown-count !
      0 scan-diagnostic-truncated-count !
      0 scan-last-unknown-length !
      0 scan-end-length !
      0 scan-line-length !
      false scan-line-overflow !
      false scan-stop-requested !
      false scan-stop-sent !
      false scan-aborted !
      false scan-timed-out !
      false scan-finished !
      0 scan-started-at !
      0 scan-stop-sent-at !
      0 scan-finished-at !
      scan-result-lengths scan-result-capacity cells 0 fill
      scan-result-buffer
      scan-result-capacity scan-record-capacity * 0 fill
      scan-line-buffer scan-line-capacity 0 fill
      scan-last-unknown-buffer scan-diagnostic-capacity 0 fill
      scan-end-buffer scan-diagnostic-capacity 0 fill
    ;

    : save-diagnostic-line
      { data length destination saved-length -- }
      length scan-diagnostic-capacity u> if
        1 scan-diagnostic-truncated-count +!
      then
      length scan-diagnostic-capacity min { saved }
      data destination saved move
      saved saved-length !
    ;

    : save-unknown-line { data length -- }
      1 scan-unknown-count +!
      data length scan-last-unknown-buffer scan-last-unknown-length
      save-diagnostic-line
    ;

    : save-scan-end-line { data length -- }
      data length scan-end-buffer scan-end-length save-diagnostic-line
    ;

    : save-scan-result { data length -- }
      length scan-record-capacity u> if
        1 scan-oversize-count +!
        exit
      then
      scan-result-count @ scan-result-capacity u>= if
        1 scan-dropped-count +!
        exit
      then
      scan-result-count @ { index }
      data index scan-result-address length move
      length scan-result-lengths index cells + !
      1 scan-result-count +!
    ;

    : mark-scan-finished ( -- )
      timer::us-counter-lsb scan-finished-at !
      true scan-finished !
      false scan-running !
    ;

    : command-envelope-line? { data length -- known? }
      data length echoed-command? if true exit then
      data length [char] C json-record-type? if true exit then
      data length [char] A json-record-type? if true exit then
      data length [char] R json-record-type? if true exit then
      data length [char] E json-record-type?
    ;

    : matching-scan-result? { data length -- matching? }
      data length [char] S json-record-index@
      { index valid? }
      valid?
      verbose-command-seen @ and
      index verbose-command-index @ = and
    ;

    : matching-scan-end? { data length -- matching? }
      data length scan-end-index@
      { index valid? }
      valid?
      verbose-command-seen @ and
      index verbose-command-index @ = and 0= if false exit then
      data length s\" \"action\":\"scan completed\"" line-contains?
    ;

    : process-scan-line { data length -- }
      length 0= if exit then

      data length matching-scan-result? if
        response-complete @ command-result @ result-ok = and if
          data length save-scan-result
        else
          data length save-unknown-line
        then
        exit
      then

      data length matching-scan-end? if
        response-complete @ command-result @ result-ok = and if
          data length save-scan-end-line
          mark-scan-finished
        else
          data length save-unknown-line
        then
        exit
      then

      response-complete @ 0= if
        data length process-line
        response-complete @ if
          command-result @ result-ok = if
            true scan-running !
          else
            mark-scan-finished
          then
        then
        data length command-envelope-line? 0= if
          data length save-unknown-line
        then
      else
        data length save-unknown-line
      then
    ;

    : finish-scan-line ( -- )
      scan-line-overflow @ if
        1 scan-oversize-count +!
      else
        scan-line-length @ { length }
        length 0> if
          scan-line-buffer length 1- + c@ $0D = if -1 +to length then
        then
        scan-line-buffer length process-scan-line
      then
      0 scan-line-length !
      false scan-line-overflow !
    ;

    : append-scan-line-byte ( byte -- )
      scan-line-length @ scan-line-capacity u< if
        scan-line-buffer scan-line-length @ + c!
        1 scan-line-length +!
      else
        drop
        true scan-line-overflow !
      then
    ;

    : consume-scan-byte ( byte -- )
      response-complete @ 0= if dup append-response-byte then
      dup $0A = if
        drop finish-scan-line
      else
        append-scan-line-byte
      then
    ;

    : consume-scan-data { data length -- }
      length 0 ?do
        data i + c@ consume-scan-byte
      loop
    ;

    : append-decimal-scan-seconds { seconds destination -- length }
      seconds 10 u< if
        seconds [char] 0 + destination c!
        1
      else
        seconds 10 / [char] 0 + destination c!
        seconds 10 mod [char] 0 + destination 1+ c!
        2
      then
    ;

    : prepare-gap-scan-command { seconds -- }
      s" AT+GAPSCAN=" { prefix prefix-length }
      prefix scan-command-build-buffer prefix-length move
      seconds scan-command-build-buffer prefix-length +
      append-decimal-scan-seconds { digits }
      scan-command-build-buffer prefix-length digits + prepare-command
    ;

    : send-scan-etx ( -- )
      scan-stop-sent @ if exit then
      $03 scan-etx-buffer c!
      scan-etx-buffer 1 write-timeout-frames
      cdc-acm-v4-write-all-unlocked
      timer::us-counter-lsb scan-stop-sent-at !
      true scan-stop-sent !
    ;

    : scan-operation-complete? ( -- complete? )
      scan-finished @ if
        scan-line-length @ 0=
        scan-line-overflow @ 0= and
        exit
      then
      response-complete @ command-result @ result-ok <> and
    ;

    : scan-deadline-reached? ( -- reached? )
      response-complete @ 0= if
        scan-started-at @ scan-command-timeout-us elapsed-us?
      else
        scan-started-at @
        scan-seconds @ 1_000_000 * scan-finish-grace-us + elapsed-us?
      then
    ;

    : service-scan-deadlines ( -- )
      scan-operation-complete? if exit then
      scan-finished @ if
        scan-finished-at @ scan-trailing-line-drain-us elapsed-us? if
          true resync-required !
          ['] x-scan-timeout ?raise
        then
        exit
      then
      scan-stop-sent @ if
        scan-stop-sent-at @ scan-stop-drain-us elapsed-us? if
          true resync-required !
          ['] x-scan-timeout ?raise
        then
        exit
      then
      scan-stop-requested @ if
        \ ETX is only meaningful after the scan command's matching E has
        \ established a successful command index.  A stop requested while
        \ C/A/R/E is still arriving remains pending until that boundary.
        response-complete @ command-result @ result-ok = and if
          true scan-aborted !
          send-scan-etx
          exit
        then
      then
      scan-deadline-reached? if
        true scan-timed-out !
        response-complete @ if
          send-scan-etx
        else
          \ Without a completed command envelope an SE cannot be correlated
          \ safely.  Preserve the bounded behavior, but require reset instead
          \ of injecting ETX into a command whose acceptance is unknown.
          true resync-required !
          ['] x-scan-timeout ?raise
        then
      then
    ;

    : do-gap-scan-transaction ( -- )
      command-buffer command-length @ 2 + write-timeout-frames
      cdc-acm-v4-write-all-unlocked
      timer::us-counter-lsb scan-started-at !

      begin scan-operation-complete? 0= while
        read-buffer read-capacity read-timeout-frames
        cdc-acm-v4-read-unlocked { received }
        received 0> if read-buffer received consume-scan-data then
        service-scan-deadlines
      repeat

      malformed-response @ if ['] x-malformed-response ?raise then
      response-overflow @ if ['] x-response-overflow ?raise then
    ;

    : execute-gap-scan { seconds -- success? }
      resync-required @ 0= averts x-resync-required
      bootstrap-complete @ averts x-scan-not-ready
      seconds valid-gap-scan-seconds? averts x-invalid-gap-scan-seconds
      seconds scan-seconds !
      clear-scan-storage
      seconds prepare-gap-scan-command
      true scan-running !
      ['] do-gap-scan-transaction
      ['] with-cdc-acm-v4-transaction try { exception }
      false scan-running !
      exception 0<> if
        exception ['] x-response-overflow <> if
          true resync-required !
        then
        exception ?raise
      then
      command-result @ result-ok =
      scan-finished @ and
      scan-aborted @ 0= and
      scan-timed-out @ 0= and
    ;

  end-module> import

  \ Carry out one finite GAP scan.  The duration is limited to 1..30 seconds.
  \ The word returns true only when the command succeeded and its matching SE
  \ arrived naturally.  A controlled stop or recovered host timeout returns
  \ false; an undrained timeout raises and latches resync-required.
  : gap-scan ( seconds -- success? )
    ['] execute-gap-scan with-command-lock
  ;

  : scan-running? ( -- running? ) scan-running @ ;

  \ Request a controlled stop from another task.  The scan owner sends one
  \ raw ETX byte and continues draining until the matching SE record.
  : request-scan-stop ( -- requested? )
    scan-running @ scan-finished @ 0= and scan-stop-sent @ 0= and if
      true scan-stop-requested !
      true
    else
      false
    then
  ;

  : scan-aborted? ( -- aborted? ) scan-aborted @ ;
  : scan-timed-out? ( -- timed-out? ) scan-timed-out @ ;
  : scan-count@ ( -- count ) scan-result-count @ ;
  : scan-dropped@ ( -- count ) scan-dropped-count @ ;
  : scan-oversize@ ( -- count ) scan-oversize-count @ ;
  : scan-unknown-count@ ( -- count ) scan-unknown-count @ ;
  : scan-diagnostic-truncated@ ( -- count )
    scan-diagnostic-truncated-count @
  ;

  : scan-result@ { index -- data length }
    index scan-result-count @ u< averts x-invalid-scan-result
    index scan-result-address
    scan-result-lengths index cells + @
  ;

  : scan-end@ ( -- data length ) scan-end-buffer scan-end-length @ ;

  : scan-last-unknown@ ( -- data length )
    scan-last-unknown-buffer scan-last-unknown-length @
  ;

  : .scan-results ( -- )
    scan-result-count @ 0 ?do
      i scan-result@ type cr
    loop
    scan-dropped-count @ ?dup if
      ." [BleuIO scan records dropped: " . ." ]" cr
    then
    scan-oversize-count @ ?dup if
      ." [BleuIO oversized scan lines: " . ." ]" cr
    then
    scan-unknown-count @ ?dup if
      ." [BleuIO unclassified lines: " . ." ]" cr
    then
    scan-diagnostic-truncated-count @ ?dup if
      ." [BleuIO diagnostic lines truncated: " . ." ]" cr
    then
    scan-aborted @ if ." [BleuIO scan stopped by request]" cr then
    scan-timed-out @ if ." [BleuIO scan exceeded its host deadline]" cr then
  ;

  : init-bleuio-scan-state ( -- )
    false scan-running !
    clear-scan-storage
    $03 scan-etx-buffer c!
  ;

  initializer init-bleuio-scan-state

end-module
