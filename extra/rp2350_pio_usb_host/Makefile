TOOLCHAIN ?= arm-none-eabi-
CC := $(TOOLCHAIN)gcc
OBJCOPY := $(TOOLCHAIN)objcopy
OBJDUMP := $(TOOLCHAIN)objdump
NM := $(TOOLCHAIN)nm
SIZE := $(TOOLCHAIN)size

BUILD := build-smoke
TARGET := $(BUILD)/core1_smoke

CPUFLAGS := -mcpu=cortex-m33 -mthumb -mfloat-abi=soft
CFLAGS := $(CPUFLAGS) -std=c11 -Os -g3 -Wall -Wextra -Werror \
	-ffreestanding -fno-builtin -fno-common -ffunction-sections \
	-fdata-sections -fno-unwind-tables -fno-asynchronous-unwind-tables
LDFLAGS := $(CPUFLAGS) -nostdlib -nostartfiles \
	-Wl,-T,core1.ld,-Map,$(TARGET).map,--gc-sections,--build-id=none

.PHONY: all clean inspect

all: $(TARGET).elf $(TARGET).bin $(TARGET).list

$(BUILD):
	mkdir -p $@

$(BUILD)/startup.o: startup.S | $(BUILD)
	$(CC) $(CFLAGS) -c $< -o $@

$(BUILD)/core1_smoke.o: core1_smoke.c core1_abi.h | $(BUILD)
	$(CC) $(CFLAGS) -c $< -o $@

$(TARGET).elf: $(BUILD)/startup.o $(BUILD)/core1_smoke.o core1.ld
	$(CC) $(LDFLAGS) $(BUILD)/startup.o $(BUILD)/core1_smoke.o -lgcc -o $@
	$(SIZE) $@

$(TARGET).bin: $(TARGET).elf
	$(OBJCOPY) -O binary $< $@

$(TARGET).list: $(TARGET).elf
	$(OBJDUMP) -drwC $< > $@

inspect: all
	$(NM) -n $(TARGET).elf
	$(OBJDUMP) -h $(TARGET).elf

clean:
	rm -rf $(BUILD)
