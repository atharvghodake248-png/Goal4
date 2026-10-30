#include <zephyr/kernel.h>
#include <zephyr/sys/sys_io.h>
#include <stdint.h>

#define ECAM_BASE 0x90000000UL  

static inline uintptr_t cfg_addr(uint8_t bus, uint8_t dev, uint8_t fn, uint16_t reg)
{
	return ECAM_BASE | ((uintptr_t)bus << 20) | ((uintptr_t)dev << 15) |
	       ((uintptr_t)fn << 12) | (reg & 0xFFC);
}

static uint32_t cfg_read32(uint8_t b, uint8_t d, uint8_t f, uint16_t r)
{
	return sys_read32(cfg_addr(b, d, f, r));
}

static void cfg_write32(uint8_t b, uint8_t d, uint8_t f, uint16_t r, uint32_t v)
{
	sys_write32(v, cfg_addr(b, d, f, r));
}

int main(void)
{
	printk("== Raw-MMIO PCIe enumeration (stub) ==\n");

	for (int dev = 0; dev < 4; dev++) {
		uint32_t id = cfg_read32(0, dev, 0, 0x00);
		if (id == 0xFFFFFFFF) {
			printk("00:%02x.0  no device\n", dev);
			continue;
		}
		printk("00:%02x.0  vendor=0x%04x device=0x%04x\n",
		       dev, id & 0xFFFF, id >> 16);
		printk("         class/rev=0x%08x\n", cfg_read32(0, dev, 0, 0x08));

		uint32_t orig = cfg_read32(0, dev, 0, 0x10);
		cfg_write32(0, dev, 0, 0x10, 0xFFFFFFFF);
		uint32_t mask = cfg_read32(0, dev, 0, 0x10);
		cfg_write32(0, dev, 0, 0x10, orig);

		uint32_t size = ~(mask & ~0xFUL) + 1;
		printk("         BAR0 mask=0x%08x size=0x%x bytes\n", mask, size);

		cfg_write32(0, dev, 0, 0x10, 0xA0000000);
		cfg_write32(0, dev, 0, 0x04, cfg_read32(0, dev, 0, 0x04) | 0x2);
		printk("         BAR0 now=0x%08x CMD=0x%08x\n",
		       cfg_read32(0, dev, 0, 0x10), cfg_read32(0, dev, 0, 0x04));
	}
	printk("== done ==\n");
	return 0;
}
