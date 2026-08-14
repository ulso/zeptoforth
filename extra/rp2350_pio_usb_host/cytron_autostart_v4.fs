\ Persistent Cytron MOTION 2350 Pro boot loader for the audited ABI-v4 image.
\
\ The generated payload source must be included before this file, followed by
\ enumeration_v4.fs and the higher protocol layers.  This initializer copies
\ the payload from the flash dictionary into the carved core-1 SRAM, verifies
\ every byte, and launches it.  It deliberately does not reset the downstream
\ port, enumerate a device, open CDC, or issue a BleuIO command.

compile-to-flash

pio-pool import
pio import
dma-pool import
sha-256-accel import

0 constant pio-usb-core1-v4-autostart-idle
1 constant pio-usb-core1-v4-autostart-validating
2 constant pio-usb-core1-v4-autostart-reserving
3 constant pio-usb-core1-v4-autostart-copying
4 constant pio-usb-core1-v4-autostart-launching
5 constant pio-usb-core1-v4-autostart-running

variable pio-usb-core1-v4-autostart-status
variable pio-usb-core1-v4-autostart-exception
variable pio-usb-core1-v4-resources-reserved
32 buffer: pio-usb-core1-v4-calculated-sha256

: x-pio-usb-core1-v4-invalid-platform ( -- )
  ." the embedded PIO USB image requires rp2350_1core at 150 MHz" cr
;

: x-pio-usb-core1-v4-invalid-embedded-image ( -- )
  ." the embedded PIO USB image does not match its audited layout" cr
;

: x-pio-usb-core1-v4-already-running ( -- )
  ." core 1 is live; reset the whole target instead of invoking init" cr
;

: x-pio-usb-core1-v4-unexpected-resource ( -- )
  ." PIO0 or DMA0 was not exclusively available at boot" cr
;

: x-pio-usb-core1-v4-copy-failed ( -- )
  ." the embedded PIO USB image failed SRAM copy verification" cr
;

: pio-usb-core1-v4-embedded-valid? ( -- valid? )
  pio-usb-core1-v4-embedded-size
  $4AB0 =
  pio-usb-core1-v4-copy-end pio-usb-core1-v4-vector-table -
  $4AB0 = and
  pio-usb-core1-v4-embedded-image @
  pio-usb-core1-v4-rstack = and
  pio-usb-core1-v4-embedded-image 4 + @
  pio-usb-core1-v4-entry 1 or = and
;

: pio-usb-core1-v4-copy-equal? ( -- equal? )
  pio-usb-core1-v4-embedded-size 0 ?do
    pio-usb-core1-v4-embedded-image i + c@
    pio-usb-core1-v4-vector-table i + c@ <> if
      false unloop exit
    then
  loop
  true
;

: pio-usb-core1-v4-hash-valid? ( address -- valid? )
  pio-usb-core1-v4-embedded-size
  pio-usb-core1-v4-calculated-sha256 calc-sha-256-accel
  pio-usb-core1-v4-calculated-sha256
  pio-usb-core1-v4-expected-sha256
  32 pio-usb-v4-bytes-equal?
;

: validate-pio-usb-core1-v4-autostart ( -- )
  rp2350?
  cpu-count 1 = and
  ram-end pio-usb-core1-v4-vector-table = and
  sysclk @ 150000000 = and
  averts x-pio-usb-core1-v4-invalid-platform
  pio-usb-core1-v4-embedded-valid?
  pio-usb-core1-v4-embedded-image
  pio-usb-core1-v4-hash-valid? and
  averts x-pio-usb-core1-v4-invalid-embedded-image
;

: reserve-pio-usb-core1-v4-resources ( -- )
  32 4 allocate-pio-sms-w-piomem
  { sm0 sm1 sm2 sm3 base pio }
  sm0 bit sm1 bit or sm2 bit or sm3 bit or $0F =
  base 0= and pio PIO0 = and
  averts x-pio-usb-core1-v4-unexpected-resource
  allocate-dma 0=
  averts x-pio-usb-core1-v4-unexpected-resource
  true pio-usb-core1-v4-resources-reserved !
;

: copy-pio-usb-core1-v4-image ( -- )
  pio-usb-core1-v4-vector-table $22000 0 fill
  pio-usb-core1-v4-embedded-image
  pio-usb-core1-v4-vector-table
  pio-usb-core1-v4-embedded-size move
  dmb dsb isb
  pio-usb-core1-v4-copy-equal?
  pio-usb-core1-v4-image-present? and
  pio-usb-core1-v4-vector-table
  pio-usb-core1-v4-hash-valid? and
  averts x-pio-usb-core1-v4-copy-failed
;

: actually-init-pio-usb-core1-v4-autostart ( -- )
  pio-usb-core1-v4-autostart-validating
  pio-usb-core1-v4-autostart-status !
  pio-usb-core1-v4-alive?
  triggers x-pio-usb-core1-v4-already-running
  false pio-usb-core1-v4-launched !
  validate-pio-usb-core1-v4-autostart
  pio-usb-core1-v4-autostart-reserving
  pio-usb-core1-v4-autostart-status !
  reserve-pio-usb-core1-v4-resources
  pio-usb-core1-v4-autostart-copying
  pio-usb-core1-v4-autostart-status !
  copy-pio-usb-core1-v4-image
  pio-usb-core1-v4-autostart-launching
  pio-usb-core1-v4-autostart-status !
  launch-pio-usb-core1-v4
  pio-usb-core1-v4-autostart-running
  pio-usb-core1-v4-autostart-status !
;

: init-pio-usb-core1-v4-autostart ( -- )
  false pio-usb-core1-v4-resources-reserved !
  pio-usb-core1-v4-autostart-idle
  pio-usb-core1-v4-autostart-status !
  ['] actually-init-pio-usb-core1-v4-autostart try dup
  pio-usb-core1-v4-autostart-exception !
  drop
;

: pio-usb-core1-v4-autostart-ok? ( -- ok? )
  pio-usb-core1-v4-autostart-status @
  pio-usb-core1-v4-autostart-running =
  pio-usb-core1-v4-autostart-exception @ 0= and
  pio-usb-core1-v4-running? and
;

: pio-usb-core1-v4-autostart. ( -- )
  pio-usb-core1-v4-autostart-status @ case
    pio-usb-core1-v4-autostart-idle of
      ." core 1 PIO USB autostart did not run"
    endof
    pio-usb-core1-v4-autostart-validating of
      ." core 1 PIO USB platform/image validation failed"
    endof
    pio-usb-core1-v4-autostart-reserving of
      ." core 1 PIO USB resource reservation failed"
    endof
    pio-usb-core1-v4-autostart-copying of
      ." core 1 PIO USB payload copy verification failed"
    endof
    pio-usb-core1-v4-autostart-launching of
      ." core 1 PIO USB payload launch failed"
    endof
    pio-usb-core1-v4-autostart-running of
      ." core 1 PIO USB payload started"
    endof
    ." unknown core 1 PIO USB autostart status"
  endcase
  pio-usb-core1-v4-autostart-exception @ ?dup if
    ."  (exception " h.8 ." )"
  then
  cr
;

initializer init-pio-usb-core1-v4-autostart
