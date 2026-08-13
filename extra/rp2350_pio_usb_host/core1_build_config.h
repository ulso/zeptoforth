#pragma once

/*
 * A freestanding core 1 image never runs the Pico SDK clock initializer, so
 * its configured_freq[] cache would otherwise report zero.  Force every
 * inline/generated PIO divider calculation to the hardware-verified
 * Zeptoforth clk_sys value.  Constant folding also keeps soft-float helpers
 * out of the final image.
 */
#ifndef __ASSEMBLER__
#include "hardware/clocks.h"

#define clock_get_hz(clock) ((void)(clock), PICO_PIO_USB_CLK_SYS_HZ)
#endif
