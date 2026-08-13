#pragma once

#include <stddef.h>
#include <stdint.h>

#define CORE1_ABI_MAGIC 0x43505531u /* "CPU1" */
#define CORE1_ABI_VERSION 2u

enum {
    CORE1_IMAGE_SMOKE = 1,
    CORE1_IMAGE_PIO_USB_FRAME = 2,
};

enum {
    CORE1_COMMAND_NONE = 0,
    CORE1_COMMAND_GET_DEVICE_DESCRIPTOR_8 = 1,
};

enum {
    CORE1_COMMAND_IDLE = 0,
    CORE1_COMMAND_BUSY = 1,
    CORE1_COMMAND_OK = 2,
    CORE1_COMMAND_ERROR_INVALID = 0x80,
    CORE1_COMMAND_ERROR_NOT_CONNECTED,
    CORE1_COMMAND_ERROR_NOT_FULL_SPEED,
    CORE1_COMMAND_ERROR_ENDPOINT_OPEN,
    CORE1_COMMAND_ERROR_SETUP_START,
    CORE1_COMMAND_ERROR_DATA_START,
    CORE1_COMMAND_ERROR_STATUS_START,
    CORE1_COMMAND_ERROR_TRANSFER,
    CORE1_COMMAND_ERROR_STALL,
    CORE1_COMMAND_ERROR_TIMEOUT,
    CORE1_COMMAND_ERROR_DISCONNECT,
    CORE1_COMMAND_ERROR_DESCRIPTOR,
};

enum {
    CORE1_COMMAND_PHASE_IDLE = 0,
    CORE1_COMMAND_PHASE_RESET = 1,
    CORE1_COMMAND_PHASE_RECOVERY = 2,
    CORE1_COMMAND_PHASE_SETUP = 3,
    CORE1_COMMAND_PHASE_DATA = 4,
    CORE1_COMMAND_PHASE_STATUS = 5,
    CORE1_COMMAND_PHASE_COMPLETE = 6,
    CORE1_COMMAND_PHASE_ERROR = 0x80,
};

typedef struct {
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

    /* Single-writer mailbox.  Core 0 writes command then publishes a new
     * command_seq.  Core 1 publishes all result fields before completion_seq.
     */
    volatile uint32_t command_seq;
    volatile uint32_t command;
    volatile uint32_t completion_seq;
    volatile uint32_t command_status;
    volatile uint32_t command_phase;
    volatile uint32_t command_deadline_frame;
    volatile uint32_t descriptor_length;
    volatile uint32_t endpoint_complete;
    volatile uint32_t endpoint_error;
    volatile uint32_t endpoint_stalled;
    volatile uint8_t descriptor[16];
} core1_shared_t;

_Static_assert(sizeof(core1_shared_t) == 128,
               "core 1 mailbox ABI must remain exactly 128 bytes");
_Static_assert(offsetof(core1_shared_t, fault) == 0x14,
               "the assembly fault-vector ABI changed");
_Static_assert(offsetof(core1_shared_t, command_seq) == 0x48,
               "the core 0 request sequence offset changed");
_Static_assert(offsetof(core1_shared_t, command) == 0x4c,
               "the core 0 command offset changed");
_Static_assert(offsetof(core1_shared_t, completion_seq) == 0x50,
               "the core 1 response sequence offset changed");
_Static_assert(offsetof(core1_shared_t, command_status) == 0x54,
               "the command status offset changed");
_Static_assert(offsetof(core1_shared_t, descriptor_length) == 0x60,
               "the descriptor length offset changed");
_Static_assert(offsetof(core1_shared_t, descriptor) == 0x70,
               "the descriptor payload offset changed");

extern core1_shared_t core1_shared;
