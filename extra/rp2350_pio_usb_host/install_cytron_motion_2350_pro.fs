\ Install the persistent PIO USB host and BleuIO layers on an rp2350_1core
\ system that already has setup_full_usb.fs in flash.
\
\ First generate generated/core1_payload_v4.fs with
\ generate_core1_payload.py.  Loading this file writes the complete extension
\ into the flash dictionary and updates the saved mini-dictionary.  The new
\ boot initializer takes effect after the next reset.

compile-to-ram
0 constant pio-usb-host-persistent-build

#include extra/rp2350_pio_usb_host/generated/core1_payload_v4.fs
#include extra/rp2350/sha256_accel.fs
#include extra/rp2350_pio_usb_host/enumeration_v4.fs
#include extra/rp2350_pio_usb_host/full_enumeration_v4.fs
#include extra/rp2350_pio_usb_host/cdc_acm_v4.fs
#include extra/rp2350_pio_usb_host/bleuio.fs
#include extra/rp2350_pio_usb_host/bleuio_scan.fs
#include extra/rp2350_pio_usb_host/cytron_autostart_v4.fs

\ Keep one rollback point that retains this complete extension.
compile-to-flash
cornerstone restore-pio-usb
commit-flash
compile-to-ram

mini-dict::save-flash-mini-dict
