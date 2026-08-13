#include "core1_abi.h"

enum {
    CORE1_PHASE_ENTERED = 1,
    CORE1_PHASE_RUNNING = 2,
};

__attribute__((section(".shared.core1"), aligned(16), used))
core1_shared_t core1_shared;

__attribute__((noreturn)) void core1_main(void) {
    core1_shared.magic = CORE1_ABI_MAGIC;
    core1_shared.abi_version = CORE1_ABI_VERSION;
    core1_shared.image_kind = CORE1_IMAGE_SMOKE;
    core1_shared.phase = CORE1_PHASE_ENTERED;
    core1_shared.heartbeat = 0;
    core1_shared.fault = 0;
    __asm volatile("dmb" ::: "memory");

    core1_shared.phase = CORE1_PHASE_RUNNING;
    for (;;) {
        core1_shared.heartbeat++;
        __asm volatile("dmb\nsev" ::: "memory");
    }
}
