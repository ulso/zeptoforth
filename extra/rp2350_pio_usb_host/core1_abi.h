#pragma once

#include <stddef.h>
#include <stdint.h>

#define CORE1_ABI_MAGIC 0x43505531u /* "CPU1" */
#define CORE1_ABI_VERSION 4u
#define CORE1_ABI_BYTES 512u
#define CORE1_CONTROL_DATA_CAPACITY 256u

enum {
    CORE1_IMAGE_SMOKE = 1,
    CORE1_IMAGE_PIO_USB_FRAME = 2,
};

enum {
    CORE1_CAP_PORT_RESET = 1u << 0,
    CORE1_CAP_CONTROL_TRANSFER = 1u << 1,
    CORE1_CAP_ENDPOINT_LIFECYCLE = 1u << 2,
    CORE1_CAP_ENDPOINT_TRANSFER = 1u << 3,
};

enum {
    CORE1_PORT_DETACHED = 0,
    CORE1_PORT_ATTACHED_SUSPENDED = 1,
    CORE1_PORT_ACTIVE = 2,
    CORE1_PORT_RESETTING = 3,
    CORE1_PORT_RECOVERY_REQUIRED = 4,
};

enum {
    CORE1_COMMAND_NONE = 0,
    CORE1_COMMAND_PORT_RESET = 1,
    CORE1_COMMAND_CONTROL_TRANSFER = 2,
    CORE1_COMMAND_ENDPOINT_OPEN = 3,
    CORE1_COMMAND_ENDPOINT_CLOSE = 4,
    CORE1_COMMAND_ENDPOINT_TRANSFER = 5,
};

enum {
    CORE1_COMMAND_IDLE = 0,
    CORE1_COMMAND_BUSY = 1,
    CORE1_COMMAND_OK = 2,
    CORE1_COMMAND_PARTIAL = 3,
    CORE1_COMMAND_ERROR_INVALID = 0x80,
    CORE1_COMMAND_ERROR_STALE_EPOCH,
    CORE1_COMMAND_ERROR_NOT_CONNECTED,
    CORE1_COMMAND_ERROR_NOT_FULL_SPEED,
    CORE1_COMMAND_ERROR_PORT_STATE,
    CORE1_COMMAND_ERROR_ENDPOINT_OPEN,
    CORE1_COMMAND_ERROR_SETUP_START,
    CORE1_COMMAND_ERROR_DATA_START,
    CORE1_COMMAND_ERROR_STATUS_START,
    CORE1_COMMAND_ERROR_TRANSFER,
    CORE1_COMMAND_ERROR_STALL,
    CORE1_COMMAND_ERROR_TIMEOUT,
    CORE1_COMMAND_ERROR_DISCONNECT,
    CORE1_COMMAND_ERROR_ENDPOINT_NOT_OPEN,
    CORE1_COMMAND_ERROR_ENDPOINT_ALREADY_OPEN,
    CORE1_COMMAND_ERROR_ENDPOINT_BUSY,
};

enum {
    CORE1_COMMAND_PHASE_IDLE = 0,
    CORE1_COMMAND_PHASE_RESET = 1,
    CORE1_COMMAND_PHASE_RECOVERY = 2,
    CORE1_COMMAND_PHASE_SETUP = 3,
    CORE1_COMMAND_PHASE_DATA = 4,
    CORE1_COMMAND_PHASE_STATUS = 5,
    CORE1_COMMAND_PHASE_COMPLETE = 6,
    CORE1_COMMAND_PHASE_ENDPOINT_DATA = 7,
    CORE1_COMMAND_PHASE_ERROR = 0x80,
};

typedef struct {
    /* Core 1 telemetry.  Keep fault at offset 0x14 for startup.S. */
    volatile uint32_t magic;
    volatile uint32_t abi_version;
    volatile uint32_t image_kind;
    volatile uint32_t phase;
    volatile uint32_t heartbeat;
    volatile uint32_t fault;
    volatile uint32_t clock_hz;
    volatile uint32_t frame_count;
    volatile uint32_t line_state;
    volatile uint32_t root_event;
    volatile uint32_t root_ints;
    volatile uint32_t root_connected;
    volatile uint32_t root_full_speed;
    volatile uint32_t resource_busy;
    volatile uint32_t pio_ctrl_before;
    volatile uint32_t dma_ctrl_before;
    volatile uint32_t pio_ctrl;
    volatile uint32_t dma_ctrl;

    volatile uint32_t abi_bytes;
    volatile uint32_t capabilities;
    volatile uint32_t data_capacity;
    volatile uint32_t port_epoch;
    volatile uint32_t port_state;

    /* Single-writer request.  Core 0 writes every request field and any OUT
     * payload, executes DMB, then publishes request_seq last. */
    volatile uint32_t request_seq;
    volatile uint32_t command;
    volatile uint32_t request_epoch;
    volatile uint32_t device_address;
    volatile uint32_t ep0_mps;
    volatile uint32_t transfer_length;
    volatile uint8_t setup[8];

    /* Single-writer response.  Core 1 writes every response field and any IN
     * payload, executes DMB, then publishes completion_seq last. */
    volatile uint32_t completion_seq;
    volatile uint32_t command_status;
    volatile uint32_t command_phase;
    volatile uint32_t completion_epoch;
    volatile uint32_t actual_length;
    volatile uint32_t completion_frame;
    volatile uint32_t command_deadline_frame;
    volatile uint32_t endpoint_complete;
    volatile uint32_t endpoint_error;
    volatile uint32_t endpoint_stalled;
    volatile uint32_t failure_detail;

    /* ABI-v4 endpoint request extension.  Core 0 publishes these fields with
     * the rest of the request before request_seq.  The normalized values let
     * core 1 build a short-lived upstream endpoint descriptor without ever
     * retaining a pointer into core 0-owned memory. */
    volatile uint32_t endpoint_address;
    volatile uint32_t endpoint_attributes;
    volatile uint32_t endpoint_max_packet;
    volatile uint32_t endpoint_interval;
    volatile uint32_t transfer_timeout_frames;

    volatile uint32_t reserved[(0x100u - 0xbcu) / sizeof(uint32_t)];
    volatile uint8_t data[CORE1_CONTROL_DATA_CAPACITY];
} core1_shared_t;

_Static_assert(sizeof(core1_shared_t) == CORE1_ABI_BYTES,
               "core 1 mailbox ABI size changed");
_Static_assert(offsetof(core1_shared_t, fault) == 0x14,
               "the assembly fault-vector ABI changed");
_Static_assert(offsetof(core1_shared_t, abi_bytes) == 0x48,
               "the ABI metadata offset changed");
_Static_assert(offsetof(core1_shared_t, port_epoch) == 0x54,
               "the port epoch offset changed");
_Static_assert(offsetof(core1_shared_t, request_seq) == 0x5c,
               "the core 0 request sequence offset changed");
_Static_assert(offsetof(core1_shared_t, setup) == 0x74,
               "the setup packet offset changed");
_Static_assert(offsetof(core1_shared_t, completion_seq) == 0x7c,
               "the core 1 response sequence offset changed");
_Static_assert(offsetof(core1_shared_t, actual_length) == 0x8c,
               "the actual length offset changed");
_Static_assert(offsetof(core1_shared_t, endpoint_address) == 0xa8,
               "the endpoint address offset changed");
_Static_assert(offsetof(core1_shared_t, transfer_timeout_frames) == 0xb8,
               "the endpoint timeout offset changed");
_Static_assert(offsetof(core1_shared_t, data) == 0x100,
               "the shared data offset changed");

extern core1_shared_t core1_shared;
