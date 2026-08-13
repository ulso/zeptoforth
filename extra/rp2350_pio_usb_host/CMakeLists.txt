cmake_minimum_required(VERSION 3.13)

set(PICO_BOARD pico2 CACHE STRING "RP2350 board used for SDK definitions")
set(PICO_PLATFORM rp2350-arm-s CACHE STRING "Build a soft-float Cortex-M33 image")
set(PICO_STDIO_UART 0 CACHE BOOL "Disable SDK UART stdio")
set(PICO_STDIO_USB 0 CACHE BOOL "Disable SDK USB stdio")

if(NOT PICO_SDK_PATH)
    set(PICO_SDK_PATH "$ENV{PICO_SDK_PATH}")
endif()
if(NOT PICO_SDK_PATH)
    message(FATAL_ERROR "Set PICO_SDK_PATH (the reference build used Pico SDK 2.2.0)")
endif()

include(${PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(zeptoforth_rp2350_pio_usb_host C ASM)

pico_sdk_init()

set(PICO_PIO_USB_DIR
    "${CMAKE_CURRENT_LIST_DIR}/Pico-PIO-USB"
    CACHE PATH "Pinned Pico-PIO-USB checkout")

add_executable(core1_pio_usb
    startup.S
    core1_pio_usb.c
    core1_support.c
    ${PICO_PIO_USB_DIR}/src/pio_usb.c
    ${PICO_PIO_USB_DIR}/src/pio_usb_host.c
    ${PICO_PIO_USB_DIR}/src/usb_crc.c
    ${PICO_SDK_PATH}/src/rp2_common/hardware_gpio/gpio.c
    ${PICO_SDK_PATH}/src/rp2_common/hardware_pio/pio.c
)

pico_generate_pio_header(core1_pio_usb ${PICO_PIO_USB_DIR}/src/usb_tx.pio)
pico_generate_pio_header(core1_pio_usb ${PICO_PIO_USB_DIR}/src/usb_rx.pio)

target_include_directories(core1_pio_usb PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}
    ${PICO_PIO_USB_DIR}/src
)

target_compile_definitions(core1_pio_usb PRIVATE
    PICO_RP2350=1
    PICO_RP2350A=1
    PICO_NO_HARDWARE=0
    PICO_PIO_USE_GPIO_BASE=0
    PICO_PIO_USB_CLK_SYS_HZ=150000000u
    PICO_TIME_DEFAULT_ALARM_POOL_DISABLED=1
)

target_compile_options(core1_pio_usb PRIVATE
    -Os
    -mcpu=cortex-m33
    -mthumb
    -mfloat-abi=soft
    -include
    ${CMAKE_CURRENT_LIST_DIR}/core1_build_config.h
    -Wall
    -Wextra
    -Werror
    -ffreestanding
    -fno-builtin
    -fno-common
    -ffunction-sections
    -fdata-sections
    -fno-unwind-tables
    -fno-asynchronous-unwind-tables
)

target_link_libraries(core1_pio_usb PRIVATE
    hardware_clocks_headers
    hardware_dma_headers
    hardware_gpio_headers
    hardware_pio_headers
    hardware_sync_headers
    hardware_timer_headers
    pico_platform_headers
    pico_stdlib_headers
)

target_link_options(core1_pio_usb PRIVATE
    -mcpu=cortex-m33
    -mthumb
    -mfloat-abi=soft
    -nostdlib
    -nostartfiles
    "LINKER:-T,${CMAKE_CURRENT_LIST_DIR}/core1.ld"
    "LINKER:--gc-sections"
    "LINKER:--wrap=pio_sm_claim"
    "LINKER:--wrap=dma_claim_mask"
    "LINKER:--wrap=pio_add_program"
    "LINKER:--wrap=pio_add_program_at_offset"
    "LINKER:--wrap=busy_wait_us"
    "LINKER:--wrap=busy_wait_ms"
    "LINKER:--build-id=none"
    "LINKER:-Map=${CMAKE_CURRENT_BINARY_DIR}/core1_pio_usb.map"
)

target_link_libraries(core1_pio_usb PRIVATE gcc)

set_property(TARGET core1_pio_usb APPEND PROPERTY LINK_DEPENDS
    ${CMAKE_CURRENT_LIST_DIR}/core1.ld)

add_custom_command(TARGET core1_pio_usb POST_BUILD
    COMMAND ${CMAKE_OBJCOPY} -O binary
            $<TARGET_FILE:core1_pio_usb>
            ${CMAKE_CURRENT_BINARY_DIR}/core1_pio_usb.bin
    VERBATIM)
