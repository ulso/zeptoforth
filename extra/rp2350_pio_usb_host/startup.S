.syntax unified
.cpu cortex-m33
.thumb

.extern __core1_bss_start
.extern __core1_bss_end
.extern __core1_shared_start
.extern __core1_shared_end
.extern __core1_stack_top
.extern core1_main
.extern core1_shared

.section .vectors, "a", %progbits
.balign 512
.global core1_vectors
core1_vectors:
    .word __core1_stack_top
    .word core1_entry + 1
    .rept 66
    .word core1_fault + 1
    .endr

.section .text.core1_entry, "ax", %progbits
.balign 4
.thumb_func
.global core1_entry
core1_entry:
    ldr r0, =__core1_bss_start
    ldr r1, =__core1_bss_end
    movs r2, #0
1:
    cmp r0, r1
    bcs 2f
    str r2, [r0], #4
    b 1b
2:
    /* The fixed mailbox is intentionally separated from private BSS. */
    ldr r0, =__core1_shared_start
    ldr r1, =__core1_shared_end
3:
    cmp r0, r1
    bcs 4f
    str r2, [r0], #4
    b 3b
4:
    dsb
    isb
    bl core1_main
    b core1_fault

.thumb_func
.global core1_fault
.global __unhandled_user_irq
.thumb_set __unhandled_user_irq, core1_fault
core1_fault:
    ldr r0, =core1_shared
    ldr r1, =0x4641554c
    str r1, [r0, #20]
    dmb
    sev
    b core1_fault
