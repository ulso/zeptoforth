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

typedef struct {
    uint32_t sequence;
    uint32_t command;
    uint32_t epoch;
    uint32_t device_address;
    uint32_t ep0_mps;
    uint32_t length;
    uint8_t setup[8];
} command_request_t;

static volatile uint32_t endpoint_complete_latch;
static volatile uint32_t endpoint_error_latch;
static volatile uint32_t endpoint_stalled_latch;

static command_request_t request;
static uint32_t observed_request_seq;
static uint32_t command_deadline;
static uint32_t last_root_connected;
static uint32_t suspended_bad_line_samples;
static uint8_t command_phase;
static bool reset_asserted;
static bool session_ready;
static endpoint_t *command_endpoint;

static inline bool frame_deadline_reached(uint32_t now, uint32_t deadline) {
    return (int32_t)(now - deadline) >= 0;
}

static uint32_t next_epoch(uint32_t epoch) {
    epoch++;
    return epoch == 0 ? 1u : epoch;
}

static uint16_t setup_length(const uint8_t setup[8]) {
    return (uint16_t)setup[6] | ((uint16_t)setup[7] << 8);
}

static bool valid_ep0_mps(uint32_t mps) {
    return mps == 8 || mps == 16 || mps == 32 || mps == 64;
}

static void copy_bytes(uint8_t *destination, const volatile uint8_t *source,
                       uint32_t length) {
    for (uint32_t index = 0; index < length; ++index) {
        destination[index] = source[index];
    }
}

static void clear_volatile_bytes(volatile uint8_t *destination,
                                 uint32_t length) {
    for (uint32_t index = 0; index < length; ++index) {
        destination[index] = 0;
    }
}

static endpoint_t *find_command_endpoint(void) {
    for (uint32_t index = 0; index < PIO_USB_EP_POOL_CNT; ++index) {
        endpoint_t *const endpoint = PIO_USB_ENDPOINT(index);
        if (endpoint->size != 0 && endpoint->root_idx == 0 &&
            endpoint->dev_addr == request.device_address &&
            (endpoint->ep_num & 0x7fu) == 0) {
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

static bool root_has_open_endpoints(void) {
    for (uint32_t index = 0; index < PIO_USB_EP_POOL_CNT; ++index) {
        endpoint_t const *const endpoint = PIO_USB_ENDPOINT(index);
        if (endpoint->size != 0 && endpoint->root_idx == 0) {
            return true;
        }
    }
    return false;
}

static void close_all_root_endpoints(void) {
    for (uint32_t index = 0; index < PIO_USB_EP_POOL_CNT; ++index) {
        endpoint_t *const endpoint = PIO_USB_ENDPOINT(index);
        if (endpoint->size != 0 && endpoint->root_idx == 0) {
            endpoint->has_transfer = false;
            endpoint->transfer_started = false;
            endpoint->transfer_aborted = false;
            endpoint->size = 0;
        }
    }
    command_endpoint = NULL;
}

static void clear_endpoint_latches(void) {
    endpoint_complete_latch = 0;
    endpoint_error_latch = 0;
    endpoint_stalled_latch = 0;
    __asm volatile("dmb" ::: "memory");
}

static void clear_command_endpoint_latches(void) {
    uint32_t const mask = command_endpoint_mask();
    endpoint_complete_latch &= ~mask;
    endpoint_error_latch &= ~mask;
    endpoint_stalled_latch &= ~mask;
    __asm volatile("dmb" ::: "memory");
}

static void publish_command_result(uint32_t status, uint32_t phase,
                                   uint32_t now) {
    core1_shared.command_status = status;
    core1_shared.command_phase = phase;
    core1_shared.completion_epoch = core1_shared.port_epoch;
    core1_shared.completion_frame = now;
    core1_shared.command_deadline_frame = command_deadline;
    __asm volatile("dmb" ::: "memory");
    core1_shared.completion_seq = request.sequence;
    __asm volatile("dmb\nsev" ::: "memory");
}

static void set_port_recovery_required(root_port_t *root) {
    if (reset_asserted) {
        pio_usb_host_port_reset_end(0);
        reset_asserted = false;
    }
    close_all_root_endpoints();
    clear_endpoint_latches();
    root->suspended = true;
    if (session_ready) {
        core1_shared.port_epoch = next_epoch(core1_shared.port_epoch);
    }
    session_ready = false;
    core1_shared.port_state = root->connected
                                  ? CORE1_PORT_RECOVERY_REQUIRED
                                  : CORE1_PORT_DETACHED;
}

static void fail_command(root_port_t *root, uint32_t status, uint32_t now,
                         bool invalidate_session) {
    if (invalidate_session) {
        set_port_recovery_required(root);
    } else {
        close_all_root_endpoints();
        clear_endpoint_latches();
    }
    command_phase = CORE1_COMMAND_PHASE_IDLE;
    publish_command_result(status, CORE1_COMMAND_PHASE_ERROR, now);
}

static bool root_is_full_speed_idle(root_port_t *root) {
    return root->initialized && root->connected && root->is_fullspeed &&
           pio_usb_bus_get_line_state(root) == PORT_PIN_FS_IDLE;
}

static void clear_response_fields(void) {
    core1_shared.command_status = CORE1_COMMAND_BUSY;
    core1_shared.command_phase = CORE1_COMMAND_PHASE_IDLE;
    core1_shared.completion_epoch = core1_shared.port_epoch;
    core1_shared.actual_length = 0;
    core1_shared.completion_frame = 0;
    core1_shared.command_deadline_frame = 0;
    core1_shared.endpoint_complete = 0;
    core1_shared.endpoint_error = 0;
    core1_shared.endpoint_stalled = 0;
    core1_shared.failure_detail = 0;
}

static void snapshot_request(void) {
    request.sequence = core1_shared.request_seq;
    request.command = core1_shared.command;
    request.epoch = core1_shared.request_epoch;
    request.device_address = core1_shared.device_address;
    request.ep0_mps = core1_shared.ep0_mps;
    request.length = core1_shared.transfer_length;
    copy_bytes(request.setup, core1_shared.setup, sizeof(request.setup));
}

static bool validate_control_request(void) {
    return request.device_address <= 127 && valid_ep0_mps(request.ep0_mps) &&
           request.length <= CORE1_CONTROL_DATA_CAPACITY &&
           setup_length(request.setup) == request.length;
}

static void begin_port_reset(uint32_t now) {
    close_all_root_endpoints();
    clear_endpoint_latches();
    core1_shared.port_epoch = next_epoch(core1_shared.port_epoch);
    session_ready = false;
    core1_shared.port_state = CORE1_PORT_RESETTING;
    pio_usb_host_port_reset_start(0);
    reset_asserted = true;
    command_phase = CORE1_COMMAND_PHASE_RESET;
    command_deadline = now + USB_RESET_FRAMES;
    core1_shared.command_phase = command_phase;
    core1_shared.command_deadline_frame = command_deadline;
}

static void begin_control_transfer(root_port_t *root, uint32_t now) {
    uint8_t const device_address = (uint8_t)request.device_address;
    endpoint_descriptor_t endpoint = {
        .length = 7,
        .type = DESC_TYPE_ENDPOINT,
        .epaddr = 0,
        .attr = EP_ATTR_CONTROL,
        .max_size = {(uint8_t)request.ep0_mps, 0},
        .interval = 0,
    };

    if (root_has_open_endpoints() ||
        !pio_usb_host_endpoint_open(0, device_address,
                                    (const uint8_t *)&endpoint, false)) {
        fail_command(root, CORE1_COMMAND_ERROR_ENDPOINT_OPEN, now, false);
        return;
    }
    command_endpoint = find_command_endpoint();
    if (command_endpoint == NULL) {
        fail_command(root, CORE1_COMMAND_ERROR_ENDPOINT_OPEN, now, false);
        return;
    }

    clear_command_endpoint_latches();
    if (!pio_usb_host_send_setup(0, device_address, request.setup)) {
        fail_command(root, CORE1_COMMAND_ERROR_SETUP_START, now, false);
        return;
    }
    command_phase = CORE1_COMMAND_PHASE_SETUP;
    command_deadline = now + USB_TRANSFER_TIMEOUT_FRAMES;
    core1_shared.command_phase = command_phase;
    core1_shared.command_deadline_frame = command_deadline;
}

static void start_command(root_port_t *root, uint32_t now) {
    uint32_t const sequence = core1_shared.request_seq;
    if (sequence == observed_request_seq ||
        command_phase != CORE1_COMMAND_PHASE_IDLE) {
        return;
    }

    __asm volatile("dmb" ::: "memory");
    snapshot_request();
    observed_request_seq = sequence;
    command_deadline = 0;
    clear_response_fields();
    clear_endpoint_latches();
    __asm volatile("dmb" ::: "memory");

    if (request.command != CORE1_COMMAND_PORT_RESET &&
        request.command != CORE1_COMMAND_CONTROL_TRANSFER) {
        publish_command_result(CORE1_COMMAND_ERROR_INVALID,
                               CORE1_COMMAND_PHASE_ERROR, now);
        return;
    }
    if (!root->connected) {
        publish_command_result(CORE1_COMMAND_ERROR_NOT_CONNECTED,
                               CORE1_COMMAND_PHASE_ERROR, now);
        return;
    }
    if (!root->is_fullspeed) {
        publish_command_result(CORE1_COMMAND_ERROR_NOT_FULL_SPEED,
                               CORE1_COMMAND_PHASE_ERROR, now);
        return;
    }
    if (request.epoch != core1_shared.port_epoch) {
        publish_command_result(CORE1_COMMAND_ERROR_STALE_EPOCH,
                               CORE1_COMMAND_PHASE_ERROR, now);
        return;
    }
    if (!root_is_full_speed_idle(root)) {
        set_port_recovery_required(root);
        publish_command_result(CORE1_COMMAND_ERROR_DISCONNECT,
                               CORE1_COMMAND_PHASE_ERROR, now);
        return;
    }

    if (request.command == CORE1_COMMAND_PORT_RESET) {
        begin_port_reset(now);
        return;
    }

    if (!session_ready || core1_shared.port_state != CORE1_PORT_ACTIVE) {
        publish_command_result(CORE1_COMMAND_ERROR_PORT_STATE,
                               CORE1_COMMAND_PHASE_ERROR, now);
        return;
    }
    if (!validate_control_request()) {
        publish_command_result(CORE1_COMMAND_ERROR_INVALID,
                               CORE1_COMMAND_PHASE_ERROR, now);
        return;
    }
    begin_control_transfer(root, now);
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

static void start_data_or_status(root_port_t *root, uint32_t now) {
    uint8_t const device_address = (uint8_t)request.device_address;
    uint16_t const length = (uint16_t)request.length;
    bool const data_in = (request.setup[0] & 0x80u) != 0;
    uint8_t const direction = data_in ? 0x80u : 0x00u;

    clear_command_endpoint_latches();
    if (length == 0) {
        if (!pio_usb_host_endpoint_transfer(0, device_address, 0x80,
                                            NULL, 0)) {
            fail_command(root, CORE1_COMMAND_ERROR_STATUS_START, now, true);
            return;
        }
        command_phase = CORE1_COMMAND_PHASE_STATUS;
    } else {
        if (data_in) {
            clear_volatile_bytes(core1_shared.data, length);
        }
        if (!pio_usb_host_endpoint_transfer(
                0, device_address, direction,
                (uint8_t *)(uintptr_t)core1_shared.data, length)) {
            fail_command(root, CORE1_COMMAND_ERROR_DATA_START, now, true);
            return;
        }
        command_phase = CORE1_COMMAND_PHASE_DATA;
    }
    command_deadline = now + USB_TRANSFER_TIMEOUT_FRAMES;
    core1_shared.command_phase = command_phase;
    core1_shared.command_deadline_frame = command_deadline;
}

static void service_command(root_port_t *root, uint32_t now) {
    if (command_phase == CORE1_COMMAND_PHASE_IDLE) {
        start_command(root, now);
        return;
    }

    if (!root->connected) {
        fail_command(root, CORE1_COMMAND_ERROR_DISCONNECT, now, true);
        return;
    }

    if (command_phase == CORE1_COMMAND_PHASE_RESET) {
        if (!frame_deadline_reached(now, command_deadline)) {
            return;
        }
        pio_usb_host_port_reset_end(0);
        reset_asserted = false;
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
        if (!root_is_full_speed_idle(root)) {
            fail_command(root, CORE1_COMMAND_ERROR_DISCONNECT, now, true);
            return;
        }
        root->suspended = false;
        session_ready = true;
        core1_shared.port_state = CORE1_PORT_ACTIVE;
        command_phase = CORE1_COMMAND_PHASE_IDLE;
        publish_command_result(CORE1_COMMAND_OK,
                               CORE1_COMMAND_PHASE_COMPLETE, now);
        return;
    }

    uint32_t complete;
    uint32_t error;
    uint32_t stalled;
    uint32_t const endpoint_result =
        consume_endpoint_result(&complete, &error, &stalled);
    uint32_t const mask = command_endpoint_mask();
    uint32_t const unexpected = endpoint_result & ~mask;
    if (mask == 0 || unexpected != 0) {
        core1_shared.failure_detail = endpoint_result;
        fail_command(root, CORE1_COMMAND_ERROR_TRANSFER, now, true);
        return;
    }
    if ((stalled & mask) != 0) {
        core1_shared.failure_detail = stalled & mask;
        fail_command(root, CORE1_COMMAND_ERROR_STALL, now, false);
        return;
    }
    if ((error & mask) != 0) {
        core1_shared.failure_detail = error & mask;
        fail_command(root, CORE1_COMMAND_ERROR_TRANSFER, now, true);
        return;
    }

    if ((complete & mask) != 0) {
        if (command_phase == CORE1_COMMAND_PHASE_SETUP) {
            start_data_or_status(root, now);
            return;
        }

        if (command_phase == CORE1_COMMAND_PHASE_DATA) {
            bool const data_in = (request.setup[0] & 0x80u) != 0;
            core1_shared.actual_length = command_endpoint->actual_len;
            if (!data_in && core1_shared.actual_length != request.length) {
                fail_command(root, CORE1_COMMAND_ERROR_TRANSFER, now, true);
                return;
            }
            clear_command_endpoint_latches();
            uint8_t const status_direction = data_in ? 0x00u : 0x80u;
            if (!pio_usb_host_endpoint_transfer(
                    0, (uint8_t)request.device_address, status_direction, NULL,
                    0)) {
                fail_command(root, CORE1_COMMAND_ERROR_STATUS_START, now,
                             true);
                return;
            }
            command_phase = CORE1_COMMAND_PHASE_STATUS;
            command_deadline = now + USB_TRANSFER_TIMEOUT_FRAMES;
            core1_shared.command_phase = command_phase;
            core1_shared.command_deadline_frame = command_deadline;
            return;
        }

        if (command_phase == CORE1_COMMAND_PHASE_STATUS) {
            close_all_root_endpoints();
            clear_endpoint_latches();
            command_phase = CORE1_COMMAND_PHASE_IDLE;
            publish_command_result(CORE1_COMMAND_OK,
                                   CORE1_COMMAND_PHASE_COMPLETE, now);
            return;
        }

        fail_command(root, CORE1_COMMAND_ERROR_TRANSFER, now, true);
        return;
    }

    if (endpoint_result == 0 &&
        frame_deadline_reached(now, command_deadline)) {
        fail_command(root, CORE1_COMMAND_ERROR_TIMEOUT, now, true);
    }
}

/* Upstream skips its disconnect check while a root is suspended.  Reconcile
 * the physical line here so unplug/replug cannot leave connected stuck true.
 * The SE0 driven deliberately during reset must never be mistaken for an
 * unplug. */
static void reconcile_suspended_connection(root_port_t *root) {
    if (!root->initialized || !root->connected || !root->suspended ||
        reset_asserted || command_phase == CORE1_COMMAND_PHASE_RECOVERY) {
        suspended_bad_line_samples = 0;
        return;
    }

    port_pin_status_t const line_state = pio_usb_bus_get_line_state(root);
    port_pin_status_t const expected_idle =
        root->is_fullspeed ? PORT_PIN_FS_IDLE : PORT_PIN_LS_IDLE;
    if (line_state == expected_idle) {
        suspended_bad_line_samples = 0;
        return;
    }

    if (++suspended_bad_line_samples < 2) {
        return;
    }
    suspended_bad_line_samples = 0;
    root->connected = false;
    root->is_fullspeed = false;
    root->suspended = true;
    root->event = EVENT_DISCONNECT;
}

/* This strong override is a synchronous callback from pio_usb_host_frame(),
 * not an NVIC handler.  It only latches transport results for service_command. */
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

static void track_connection_epoch(root_port_t *root) {
    uint32_t const connected = root->connected ? 1u : 0u;
    if (connected == last_root_connected) {
        return;
    }
    last_root_connected = connected;
    session_ready = false;
    core1_shared.port_epoch = next_epoch(core1_shared.port_epoch);
    core1_shared.port_state = connected ? CORE1_PORT_ATTACHED_SUSPENDED
                                        : CORE1_PORT_DETACHED;
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
    core1_shared.abi_bytes = CORE1_ABI_BYTES;
    core1_shared.capabilities =
        CORE1_CAP_PORT_RESET | CORE1_CAP_CONTROL_TRANSFER;
    core1_shared.data_capacity = CORE1_CONTROL_DATA_CAPACITY;
    core1_shared.port_epoch = 1;
    core1_shared.port_state = CORE1_PORT_DETACHED;
    __asm volatile("dmb" ::: "memory");

    core1_shared.phase = CORE1_PHASE_PREFLIGHT;
    if (!resources_are_idle()) {
        core1_shared.phase = CORE1_PHASE_RESOURCE_BUSY;
        __asm volatile("dmb\nsev" ::: "memory");
        for (;;) {
            core1_shared.heartbeat++;
        }
    }

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
        reconcile_suspended_connection(root);
        track_connection_epoch(root);
        service_command(root, pio_usb_host_get_frame_number());
        core1_shared.heartbeat++;
        publish_snapshot(root);
        __asm volatile("sev" ::: "memory");
    }
}
