#include "core1_abi.h"

#include <stdarg.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "hardware/clocks.h"
#include "hardware/pio.h"
#include "hardware/pio_instructions.h"
#include "hardware/structs/timer.h"
#include "hardware/timer.h"
#include "pico/time.h"

#undef clock_get_hz

/*
 * The image is freestanding: it does not run the Pico SDK runtime and it must
 * never inherit libc, stdio, an alarm pool, or SDK clock-cache assumptions.
 * These deliberately small shims cover the subset referenced by the pinned
 * Pico-PIO-USB sources.  The frame loop itself is driven by core 1 SysTick.
 */

void *memset(void *destination, int value, size_t length) {
    uint8_t *out = destination;
    while (length-- != 0) {
        *out++ = (uint8_t)value;
    }
    return destination;
}

void *memcpy(void *destination, const void *source, size_t length) {
    uint8_t *out = destination;
    const uint8_t *in = source;
    while (length-- != 0) {
        *out++ = *in++;
    }
    return destination;
}

uint32_t clock_get_hz(clock_handle_t clock) {
    return clock == clk_sys ? PICO_PIO_USB_CLK_SYS_HZ : 0u;
}

void clock_set_reported_hz(clock_handle_t clock, uint hz) {
    if (clock != clk_sys || hz != PICO_PIO_USB_CLK_SYS_HZ) {
        hard_assertion_failure();
    }
}

/* Zeptoforth owns resource allocation; this image owns the fixed resources
 * only after the read-only hardware preflight in core1_main() succeeds. */
void __wrap_dma_claim_mask(uint32_t channel_mask) {
    (void)channel_mask;
}

void __wrap_pio_sm_claim(PIO pio, uint sm) {
    (void)pio;
    (void)sm;
}

static int load_pio_program(PIO pio, const pio_program_t *program,
                            uint offset) {
    for (uint i = 0; i < program->length; ++i) {
        uint16_t instruction = program->instructions[i];
        if (_pio_major_instr_bits(instruction) == pio_instr_bits_jmp) {
            instruction = (uint16_t)(instruction + offset);
        }
        pio->instr_mem[offset + i] = instruction;
    }
    return (int)offset;
}

int __wrap_pio_add_program_at_offset(PIO pio,
                                     const pio_program_t *program,
                                     uint offset) {
    return load_pio_program(pio, program, offset);
}

int __wrap_pio_add_program(PIO pio, const pio_program_t *program) {
    /* TX occupies 0..4. Upstream requests NRZI (10 words) and then edge/EOP
     * (17 words); place them at 22..31 and 5..21 respectively. */
    uint const offset = program->length == 10 ? 22u : 5u;
    return load_pio_program(pio, program, offset);
}

void busy_wait_us(uint64_t delay_us) {
    while (delay_us != 0) {
        uint32_t const chunk =
            delay_us > 0x7fffffffu ? 0x7fffffffu : (uint32_t)delay_us;
        uint32_t const start = timer0_hw->timerawl;
        while ((uint32_t)(timer0_hw->timerawl - start) < chunk) {
            __asm volatile("nop");
        }
        delay_us -= chunk;
    }
}

void __wrap_busy_wait_us(uint64_t delay_us) {
    busy_wait_us(delay_us);
}

void __wrap_busy_wait_ms(uint32_t delay_ms) {
    busy_wait_ms(delay_ms);
}

/* The external SysTick frame driver never creates an alarm pool, but the
 * pinned upstream translation unit still contains these unreachable calls. */
alarm_pool_timer_t *alarm_pool_get_default_timer(void) {
    return NULL;
}

alarm_pool_t *alarm_pool_create_on_timer(alarm_pool_timer_t *timer,
                                         uint timer_alarm_num,
                                         uint max_timers) {
    (void)timer;
    (void)timer_alarm_num;
    (void)max_timers;
    return NULL;
}

bool alarm_pool_add_repeating_timer_us(alarm_pool_t *pool,
                                       int64_t delay_us,
                                       repeating_timer_callback_t callback,
                                       void *user_data,
                                       repeating_timer_t *out) {
    (void)pool;
    (void)delay_us;
    (void)callback;
    (void)user_data;
    (void)out;
    return false;
}

bool cancel_repeating_timer(repeating_timer_t *timer) {
    (void)timer;
    return false;
}

void busy_wait_ms(uint32_t delay_ms) {
    busy_wait_us((uint64_t)delay_ms * 1000u);
}

int printf(const char *format, ...) {
    (void)format;
    return 0;
}

int puts(const char *text) {
    (void)text;
    return 0;
}

__attribute__((noreturn)) void panic(const char *format, ...) {
    (void)format;
    core1_shared.fault = 0x50414e49u; /* "PANI" */
    __asm volatile("dmb\nsev" ::: "memory");
    for (;;) {
        __asm volatile("wfe");
    }
}

__attribute__((noreturn)) void panic_unsupported(void) {
    panic("unsupported");
}

__attribute__((noreturn)) void hard_assertion_failure(void) {
    core1_shared.fault = 0x41535352u; /* "ASSR" */
    __asm volatile("dmb\nsev" ::: "memory");
    for (;;) {
        __asm volatile("wfe");
    }
}
