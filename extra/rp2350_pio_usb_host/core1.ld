MEMORY
{
    CORE1_RAM (rwx) : ORIGIN = 0x20060000, LENGTH = 0x22000
}

ENTRY(core1_entry)

PHDRS
{
    code PT_LOAD FLAGS(5); /* read + execute */
    data PT_LOAD FLAGS(6); /* read + write */
    shared PT_LOAD FLAGS(6); /* fixed NOLOAD core 0/core 1 mailbox */
}

SECTIONS
{
    .vectors ORIGIN(CORE1_RAM) :
    {
        . = ALIGN(512);
        __core1_vectors = .;
        KEEP(*(.vectors))
        . = ALIGN(512);
    } > CORE1_RAM :code

    .text :
    {
        __core1_image_start = .;
        *(.text*)
        *(.rodata*)
        . = ALIGN(4);
    } > CORE1_RAM :code

    .data :
    {
        __core1_data_start = .;
        *(.data*)
        . = ALIGN(4);
        __core1_data_end = .;
    } > CORE1_RAM :data

    __core1_copy_end = .;

    .bss (NOLOAD) :
    {
        __core1_bss_start = .;
        *(.bss*)
        *(COMMON)
        . = ALIGN(4);
        __core1_bss_end = .;
    } > CORE1_RAM :data

    .shared 0x20080e00 (NOLOAD) :
    {
        __core1_shared_start = .;
        KEEP(*(.shared*))
        . = ALIGN(16);
        __core1_shared_end = .;
    } > CORE1_RAM :shared

    __core1_image_end = .;
    __core1_stack_top = ORIGIN(CORE1_RAM) + LENGTH(CORE1_RAM);
    __core1_stack_limit = __core1_stack_top - 0x1000;

    ASSERT(__core1_image_end <= __core1_stack_limit,
           "core 1 image overlaps its 4 KiB stack")
    ASSERT(__core1_bss_end <= __core1_shared_start,
           "core 1 private BSS overlaps the fixed shared mailbox")
    ASSERT(__core1_shared_start == 0x20080e00,
           "core 1 shared mailbox moved")
    ASSERT(SIZEOF(.shared) == 0x200,
           "core 1 shared mailbox must remain exactly 512 bytes")
    ASSERT(__core1_shared_end == __core1_stack_limit,
           "core 1 shared mailbox must end at the stack limit")
    ASSERT((__core1_vectors & 0x1ff) == 0,
           "core 1 vector table is not 512-byte aligned")

    /DISCARD/ :
    {
        *(.comment*)
        *(.note*)
        *(.ARM.attributes*)
    }
}
