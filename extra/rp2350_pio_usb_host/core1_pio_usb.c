#include "core1_abi.h"

#include <stdbool.h>
#include <stdint.h>

#include "hardware/clocks.h"
#include "hardware/dma.h"
#include "hardware/pio.h"
#include "hardware/regs/m33.h"
#include "hardware/regs/pio.h"
#include "hardware/structs/systick.h"
#include "pio_usb.h"
#include "pio_usb_ll.h"

enum {
    CORE1_PHASE_ENTERED = 1,
    CORE1_PHASE_PREFLIGHT = 2,
    CORE1_PHASE_HOST_INITIALIZED = 3,
    CORE1_PHASE_RUNNING = 4,
    CORE1_PHASE_RESOURCE_BUSY = 0x80,
};

enum {
    USB_DP_PIN = 24,
    USB_TX_PIO = 0,
    USB_TX_SM = 0,
    USB_TX_DMA = 0,
    USB_RX_PIO = 0,
    USB_RX_SM = 1,
    USB_EOP_SM = 2,
    USB_FRAME_HZ = 1000,
    USB_RESET_FRAMES = 50,
    USB_RECOVERY_FRAMES = 10,
    USB_TRANSFER_TIMEOUT_FRAMES = 100,
};

_Static_assert(PICO_PIO_USB_CLK_SYS_HZ % USB_FRAME_HZ == 0,
               "clk_sys must divide exactly into the 1 kHz frame clock");
_Static_assert(PICO_PIO_USB_CLK_SYS_HZ == 150000000u,
               "the first Cytron image is deliberately pinned to 150 MHz");

__attribute__((section(".shared.core1"), aligned(16), used))
core1_shared_t core1_shared;

static volatile uint32_t endpoint_complete_latch;
static volatile uint32_t endpoint_error_latch;
static volatile uint32_t endpoint_stalled_latch;

static uint32_t observed_command_seq;
static uint32_t active_command_seq;
static uint32_t command_deadline;
static uint8_t command_phase;
static bool reset_asserted;
static endpoint_t *command_endpoint;

static const endpoint_descriptor_t endpoint_zero_descriptor = {
    .length = 7,
    .type = DESC_TYPE_ENDPOINT,
    .epaddr = 0,
    .attr = EP_ATTR_CONTROL,
    .max_size = {8, 0},
    .interval = 0,
};

static const usb_setup_packet_t get_device_descriptor_8 = {
    .request_type = 0x80,
    .request = 0x06,
    .value_lsb = 0,
    .value_msb = DESC_TYPE_DEVICE,
    .index_lsb = 0,
    .index_msb = 0,
    .length_lsb = 8,
    .length_msb = 0,
};

static inline bool frame_deadline_reached(uint32_t now, uint32_t deadline) {
    return (int32_t)(now - deadline) >= 0;
}

static endpoint_t *find_command_endpoint(void) {
    for (uint32_t index = 0; index < PIO_USB_EP_POOL_CNT; ++index) {
        endpoint_t *const endpoint = PIO_USB_ENDPOINT(index);
        if (endpoint->size != 0 && endpoint->root_idx == 0 &&
            endpoint->dev_addr == 0 && (endpoint->ep_num & 0x7fu) == 0) {
            return endpoint;
        }
    }
    return NULL;
}

static uint32_t command_endpoint_mask(void) {
    return command_endpoint == NULL
               ? 0u
               : 1u << (uint32_t)(command_endpoint - pio_usb_ep_pool);
}

static void clear_command_endpoint_latches(void) {
    uint32_t const mask = command_endpoint_mask();
    endpoint_complete_latch &= ~mask;
    endpoint_error_latch &= ~mask;
    endpoint_stalled_latch &= ~mask;
    __asm volatile("dmb" ::: "memory");
}

static void publish_command_result(uint32_t status, uint32_t phase) {
    core1_shared.command_status = status;
    core1_shared.command_phase = phase;
    core1_shared.command_deadline_frame = command_deadline;
    __asm volatile("dmb" ::: "memory");
    core1_shared.completion_seq = active_command_seq;
    __asm volatile("dmb\nsev" ::: "memory");
}

static void stop_command_transfer(root_port_t *root) {
    if (reset_asserted) {
        pio_usb_host_port_reset_end(0);
        reset_asserted = false;
    }
    if (command_endpoint != NULL && command_endpoint->has_transfer) {
        /* service_command() runs after a complete frame transaction, so no
         * transfer is in the short transfer_started window here.  Mark the
         * queued transfer aborted directly; the upstream public abort helper
         * busy-waits for another frame and would deadlock this sole driver. */
        command_endpoint->transfer_aborted = true;
        command_endpoint->has_transfer = false;
        command_endpoint->transfer_aborted = false;
    }
    pio_usb_host_close_device(0, 0);
    command_endpoint = NULL;
    root->suspended = true;
}

static void fail_command(root_port_t *root, uint32_t status) {
    stop_command_transfer(root);
    command_phase = CORE1_COMMAND_PHASE_IDLE;
    publish_command_result(status, CORE1_COMMAND_PHASE_ERROR);
}

static bool root_is_clean_for_command(const root_port_t *root) {
    if (!root->initialized || !root->connected || !root->suspended ||
        !root->is_fullspeed ||
        pio_usb_bus_get_line_state((root_port_t *)root) != PORT_PIN_FS_IDLE) {
        return false;
    }
    for (uint32_t index = 0; index < PIO_USB_EP_POOL_CNT; ++index) {
        endpoint_t const *const endpoint = PIO_USB_ENDPOINT(index);
        if (endpoint->size != 0 && endpoint->root_idx == 0 &&
            endpoint->dev_addr == 0) {
            return false;
        }
    }
    return true;
}

static void start_command(root_port_t *root, uint32_t now) {
    uint32_t const sequence = core1_shared.command_seq;
    if (sequence == observed_command_seq ||
        command_phase != CORE1_COMMAND_PHASE_IDLE) {
        return;
    }

    __asm volatile("dmb" ::: "memory");
    uint32_t const command = core1_shared.command;
    observed_command_seq = sequence;
    active_command_seq = sequence;

    core1_shared.command_status = CORE1_COMMAND_BUSY;
    core1_shared.command_phase = CORE1_COMMAND_PHASE_IDLE;
    core1_shared.descriptor_length = 0;
    core1_shared.endpoint_complete = 0;
    core1_shared.endpoint_error = 0;
    core1_shared.endpoint_stalled = 0;
    for (uint32_t index = 0; index < sizeof(core1_shared.descriptor); ++index) {
        core1_shared.descriptor[index] = 0;
    }
    endpoint_complete_latch = 0;
    endpoint_error_latch = 0;
    endpoint_stalled_latch = 0;
    __asm volatile("dmb" ::: "memory");

    if (command != CORE1_COMMAND_GET_DEVICE_DESCRIPTOR_8) {
        publish_command_result(CORE1_COMMAND_ERROR_INVALID,
                               CORE1_COMMAND_PHASE_ERROR);
        return;
    }
    if (!root->connected) {
        publish_command_result(CORE1_COMMAND_ERROR_NOT_CONNECTED,
                               CORE1_COMMAND_PHASE_ERROR);
        return;
    }
    if (!root->is_fullspeed) {
        publish_command_result(CORE1_COMMAND_ERROR_NOT_FULL_SPEED,
                               CORE1_COMMAND_PHASE_ERROR);
        return;
    }

    if (!root_is_clean_for_command(root)) {
        publish_command_result(CORE1_COMMAND_ERROR_NOT_CONNECTED,
                               CORE1_COMMAND_PHASE_ERROR);
        return;
    }

    pio_usb_host_close_device(0, 0);
    command_endpoint = NULL;
    pio_usb_host_port_reset_start(0);
    reset_asserted = true;
    command_phase = CORE1_COMMAND_PHASE_RESET;
    command_deadline = now + USB_RESET_FRAMES;
    core1_shared.command_phase = command_phase;
    core1_shared.command_deadline_frame = command_deadline;
}

static uint32_t consume_endpoint_result(uint32_t *complete,
                                        uint32_t *error,
                                        uint32_t *stalled) {
    __asm volatile("dmb" ::: "memory");
    *complete = endpoint_complete_latch;
    *error = endpoint_error_latch;
    *stalled = endpoint_stalled_latch;
    endpoint_complete_latch = 0;
    endpoint_error_latch = 0;
    endpoint_stalled_latch = 0;
    core1_shared.endpoint_complete |= *complete;
    core1_shared.endpoint_error |= *error;
    core1_shared.endpoint_stalled |= *stalled;
    return *complete | *error | *stalled;
}

static void service_command(root_port_t *root, uint32_t now) {
    if (command_phase == CORE1_COMMAND_PHASE_IDLE) {
        start_command(root, now);
        return;
    }

    if (!root->connected) {
        fail_command(root, CORE1_COMMAND_ERROR_DISCONNECT);
        return;
    }

    if (command_phase == CORE1_COMMAND_PHASE_RESET) {
        if (!frame_deadline_reached(now, command_deadline)) {
            return;
        }
        pio_usb_host_port_reset_end(0);
        reset_asserted = false;
        /* The reset helper resumes the bus after its own 100 us guard.  Keep
         * it quiet for the USB-required post-reset recovery interval. */
        root->suspended = true;
        command_phase = CORE1_COMMAND_PHASE_RECOVERY;
        command_deadline = now + USB_RECOVERY_FRAMES;
        core1_shared.command_phase = command_phase;
        core1_shared.command_deadline_frame = command_deadline;
        return;
    }

    if (command_phase == CORE1_COMMAND_PHASE_RECOVERY) {
        if (!frame_deadline_reached(now, command_deadline)) {
            return;
        }
        if (pio_usb_bus_get_line_state(root) != PORT_PIN_FS_IDLE) {
            fail_command(root, CORE1_COMMAND_ERROR_DISCONNECT);
            return;
        }
        if (!pio_usb_host_endpoint_open(
                0, 0, (const uint8_t *)&endpoint_zero_descriptor, false)) {
            fail_command(root, CORE1_COMMAND_ERROR_ENDPOINT_OPEN);
            return;
        }
        command_endpoint = find_command_endpoint();
        if (command_endpoint == NULL) {
            fail_command(root, CORE1_COMMAND_ERROR_ENDPOINT_OPEN);
            return;
        }
        root->suspended = false;
        clear_command_endpoint_latches();
        if (!pio_usb_host_send_setup(
                0, 0, (const uint8_t *)&get_device_descriptor_8)) {
            fail_command(root, CORE1_COMMAND_ERROR_SETUP_START);
            return;
        }
        command_phase = CORE1_COMMAND_PHASE_SETUP;
        command_deadline = now + USB_TRANSFER_TIMEOUT_FRAMES;
        core1_shared.command_phase = command_phase;
        core1_shared.command_deadline_frame = command_deadline;
        return;
    }

    uint32_t complete;
    uint32_t error;
    uint32_t stalled;
    uint32_t const endpoint_result =
        consume_endpoint_result(&complete, &error, &stalled);
    if (stalled != 0) {
        fail_command(root, CORE1_COMMAND_ERROR_STALL);
        return;
    }
    if (error != 0) {
        fail_command(root, CORE1_COMMAND_ERROR_TRANSFER);
        return;
    }

    if (complete != 0) {
        uint32_t const command_endpoint_mask =
            1u << (uint32_t)(command_endpoint - pio_usb_ep_pool);
        if ((complete & command_endpoint_mask) == 0) {
            fail_command(root, CORE1_COMMAND_ERROR_TRANSFER);
            return;
        }

        if (command_phase == CORE1_COMMAND_PHASE_SETUP) {
            clear_command_endpoint_latches();
            if (!pio_usb_host_endpoint_transfer(
                    0, 0, 0x80, (uint8_t *)core1_shared.descriptor, 8)) {
                fail_command(root, CORE1_COMMAND_ERROR_DATA_START);
                return;
            }
            command_phase = CORE1_COMMAND_PHASE_DATA;
        } else if (command_phase == CORE1_COMMAND_PHASE_DATA) {
            core1_shared.descriptor_length = command_endpoint->actual_len;
            clear_command_endpoint_latches();
            if (!pio_usb_host_endpoint_transfer(0, 0, 0x00, NULL, 0)) {
                fail_command(root, CORE1_COMMAND_ERROR_STATUS_START);
                return;
            }
            command_phase = CORE1_COMMAND_PHASE_STATUS;
        } else if (command_phase == CORE1_COMMAND_PHASE_STATUS) {
            bool const descriptor_is_valid =
                core1_shared.descriptor_length == 8 &&
                core1_shared.descriptor[0] == 18 &&
                core1_shared.descriptor[1] == DESC_TYPE_DEVICE &&
                (core1_shared.descriptor[7] == 8 ||
                 core1_shared.descriptor[7] == 16 ||
                 core1_shared.descriptor[7] == 32 ||
                 core1_shared.descriptor[7] == 64);
            stop_command_transfer(root);
            command_phase = CORE1_COMMAND_PHASE_IDLE;
            publish_command_result(
                descriptor_is_valid ? CORE1_COMMAND_OK
                                    : CORE1_COMMAND_ERROR_DESCRIPTOR,
                descriptor_is_valid ? CORE1_COMMAND_PHASE_COMPLETE
                                    : CORE1_COMMAND_PHASE_ERROR);
            return;
        } else {
            fail_command(root, CORE1_COMMAND_ERROR_TRANSFER);
            return;
        }

        command_deadline = now + USB_TRANSFER_TIMEOUT_FRAMES;
        core1_shared.command_phase = command_phase;
        core1_shared.command_deadline_frame = command_deadline;
        return;
    }

    if (endpoint_result == 0 &&
        frame_deadline_reached(now, command_deadline)) {
        fail_command(root, CORE1_COMMAND_ERROR_TIMEOUT);
    }
}

/* The upstream handler drives a complete control-pipe state machine around
 * usb_device_t.  This first bounded smoke test deliberately owns just one EP0
 * transaction and latches the transport results for service_command(). */
void pio_usb_host_irq_handler(uint8_t root_id) {
    root_port_t *const root = PIO_USB_ROOT_PORT(root_id);
    uint32_t const interrupts = root->ints;

    if ((interrupts & PIO_USB_INTS_CONNECT_BITS) != 0) {
        root->event = EVENT_CONNECT;
    }
    if ((interrupts & PIO_USB_INTS_DISCONNECT_BITS) != 0) {
        root->event = EVENT_DISCONNECT;
    }
    if ((interrupts & PIO_USB_INTS_ENDPOINT_COMPLETE_BITS) != 0) {
        uint32_t const bits = root->ep_complete;
        endpoint_complete_latch |= bits;
        root->ep_complete &= ~bits;
    }
    if ((interrupts & PIO_USB_INTS_ENDPOINT_ERROR_BITS) != 0) {
        uint32_t const bits = root->ep_error;
        endpoint_error_latch |= bits;
        root->ep_error &= ~bits;
    }
    if ((interrupts & PIO_USB_INTS_ENDPOINT_STALLED_BITS) != 0) {
        uint32_t const bits = root->ep_stalled;
        endpoint_stalled_latch |= bits;
        root->ep_stalled &= ~bits;
    }
    root->ints &= ~interrupts;
    __asm volatile("dmb" ::: "memory");
}

static inline void publish_snapshot(const root_port_t *root) {
    core1_shared.frame_count = pio_usb_host_get_frame_number();
    core1_shared.line_state = pio_usb_bus_get_line_state((root_port_t *)root);
    core1_shared.root_event = (uint32_t)root->event;
    core1_shared.root_ints = root->ints;
    core1_shared.root_connected = root->connected ? 1u : 0u;
    core1_shared.root_full_speed = root->is_fullspeed ? 1u : 0u;
    core1_shared.pio_ctrl = pio0_hw->ctrl;
    core1_shared.dma_ctrl = dma_hw->ch[USB_TX_DMA].ctrl_trig;
    __asm volatile("dmb" ::: "memory");
}

static bool resources_are_idle(void) {
    uint32_t const pio_sm_mask =
        (1u << USB_TX_SM) | (1u << USB_RX_SM) | (1u << USB_EOP_SM);
    uint32_t const pio_enabled =
        (pio0_hw->ctrl & PIO_CTRL_SM_ENABLE_BITS) >> PIO_CTRL_SM_ENABLE_LSB;

    core1_shared.pio_ctrl_before = pio0_hw->ctrl;
    core1_shared.dma_ctrl_before = dma_hw->ch[USB_TX_DMA].ctrl_trig;
    core1_shared.resource_busy =
        (pio_enabled & pio_sm_mask) |
        (dma_channel_is_busy(USB_TX_DMA) ? (1u << 16) : 0u);

    return core1_shared.resource_busy == 0;
}

static void start_frame_clock(void) {
    systick_hw->csr = 0;
    systick_hw->rvr = (PICO_PIO_USB_CLK_SYS_HZ / USB_FRAME_HZ) - 1u;
    systick_hw->cvr = 0;
    systick_hw->csr = M33_SYST_CSR_CLKSOURCE_BITS | M33_SYST_CSR_ENABLE_BITS;
}

static void wait_for_next_frame(void) {
    while ((systick_hw->csr & M33_SYST_CSR_COUNTFLAG_BITS) == 0) {
        __asm volatile("nop");
    }
}

__attribute__((noreturn)) void core1_main(void) {
    core1_shared.magic = CORE1_ABI_MAGIC;
    core1_shared.abi_version = CORE1_ABI_VERSION;
    core1_shared.image_kind = CORE1_IMAGE_PIO_USB_FRAME;
    core1_shared.phase = CORE1_PHASE_ENTERED;
    core1_shared.clock_hz = PICO_PIO_USB_CLK_SYS_HZ;
    __asm volatile("dmb" ::: "memory");

    core1_shared.phase = CORE1_PHASE_PREFLIGHT;
    if (!resources_are_idle()) {
        core1_shared.phase = CORE1_PHASE_RESOURCE_BUSY;
        __asm volatile("dmb\nsev" ::: "memory");
        for (;;) {
            core1_shared.heartbeat++;
        }
    }

    /*
     * Validate the freestanding build's compile-time clock override against
     * Zeptoforth's known, hardware-verified 150 MHz configuration.  This image
     * does not run the Pico SDK clock initializer or depend on its cache.
     */
    clock_set_reported_hz(clk_sys, PICO_PIO_USB_CLK_SYS_HZ);

    pio_usb_configuration_t config = PIO_USB_DEFAULT_CONFIG;
    config.pin_dp = USB_DP_PIN;
    config.pio_tx_num = USB_TX_PIO;
    config.sm_tx = USB_TX_SM;
    config.tx_ch = USB_TX_DMA;
    config.pio_rx_num = USB_RX_PIO;
    config.sm_rx = USB_RX_SM;
    config.sm_eop = USB_EOP_SM;
    config.skip_alarm_pool = true;
    config.pinout = PIO_USB_PINOUT_DPDM;

    usb_device_t *const devices = pio_usb_host_init(&config);
    (void)devices;
    root_port_t *const root = PIO_USB_ROOT_PORT(0);

    core1_shared.phase = CORE1_PHASE_HOST_INITIALIZED;
    publish_snapshot(root);
    start_frame_clock();
    core1_shared.phase = CORE1_PHASE_RUNNING;
    core1_shared.command_status = CORE1_COMMAND_IDLE;
    core1_shared.command_phase = CORE1_COMMAND_PHASE_IDLE;

    for (;;) {
        wait_for_next_frame();
        pio_usb_host_frame();
        service_command(root, pio_usb_host_get_frame_number());
        core1_shared.heartbeat++;
        publish_snapshot(root);
        __asm volatile("sev" ::: "memory");
    }
}
