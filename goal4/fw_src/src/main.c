#include <zephyr/kernel.h>
#include <zephyr/sys/sys_io.h>
#include <zephyr/irq.h>
#include <zephyr/irq_multilevel.h>
#include <zephyr/devicetree.h>
#include <stdint.h>
 
#define ECAM_BASE 0x90000000UL   /* must match the .repl (pcieRp)  */
#define BAR0_ADDR 0xA0000000UL   /* must match the .repl (pcieBar) */
#define MSI_ADDR  0xB0000000UL   /* must match the .repl (pcieMsi) */
#define MSI_DATA  0x42
 
/* PLIC (platform file: plic @ 0xe4000000, uart1 uses source 9) */
#define PLIC_BASE 0xE4000000UL
#define PCIE_IRQ  20             /* INTx:  "IRQ -> plic@20" in the .repl */
#define MSI_IRQ   21             /* MSI:   "IRQ -> plic@21" in the .repl */
 
#define REG_SCRATCH 0x00
#define REG_MAGIC   0x04
#define REG_DOORBELL 0x08
 
/* ---------- optional real ISR for the INTx line ---------- */
#if defined(CONFIG_DYNAMIC_INTERRUPTS) && defined(CONFIG_2ND_LEVEL_INTERRUPTS)
#define ISR_AVAILABLE 1
#define UART_IRQN DT_IRQN(DT_CHOSEN(zephyr_console))
/* same encoding as the console UART (PLIC source 9), shifted to our source number */
#define PCIE_ZIRQ (UART_IRQN + (((int)PCIE_IRQ - (int)irq_from_level_2(UART_IRQN)) << CONFIG_1ST_LEVEL_INTERRUPT_BITS))
static volatile int isr_count;
 
static void pcie_isr(const void *arg)
{
	ARG_UNUSED(arg);
	isr_count++;
	irq_disable(PCIE_ZIRQ);   /* level interrupt: silence it until the device is cleared */
}
#else
#define ISR_AVAILABLE 0
#endif
 
struct results {
	int root_port, bus_nr, connector, enumerated, ids, bar_size, bar_assign;
	int mmio_write, mmio_read, round_trip, intx, intx_plic, isr, msi;
};
 
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
 
static void print_caps(uint8_t b, uint8_t d, uint8_t f)
{
	uint32_t status = cfg_read32(b, d, f, 0x04) >> 16;
 
	if (!(status & 0x10)) {
		printk("         no capability list\n");
		return;
	}
	uint32_t ptr = cfg_read32(b, d, f, 0x34) & 0xFC;
 
	while (ptr) {
		uint32_t hdr = cfg_read32(b, d, f, ptr);
 
		printk("         capability id=0x%02x at 0x%02x\n", hdr & 0xFF, ptr);
		ptr = (hdr >> 8) & 0xFC;
	}
}
 
static void plic_enable(int src)
{
	sys_write32(1, PLIC_BASE + 4 * src);                                   /* priority  */
	sys_write32(sys_read32(PLIC_BASE + 0x2000) | (1U << src), PLIC_BASE + 0x2000); /* enable */
	sys_write32(0, PLIC_BASE + 0x200000);                                  /* threshold */
}
 
/* Endpoint rings its doorbell -> legacy INTx -> connector -> PLIC source PCIE_IRQ */
static void intx_test(uint8_t dev, struct results *r)
{
	plic_enable(PCIE_IRQ);
#if ISR_AVAILABLE
	int rc = irq_connect_dynamic(PCIE_ZIRQ, 1, pcie_isr, NULL, 0);
 
	irq_enable(PCIE_ZIRQ);
	/* arch_irq_connect_dynamic() just returns the irq it was given, so rc is not a success code */
	printk("         ISR installed on irq 0x%x\n", rc);
#endif
	sys_write32(1, BAR0_ADDR + REG_DOORBELL);
 
#if ISR_AVAILABLE
	/* plain bounded loop: k_busy_wait() needs the machine timer, which Renode's AE350 model
	 * does not implement fully (its time reads return 0), so a timed wait would never end */
	for (volatile uint32_t i = 0; i < 2000000 && !isr_count; i++) {
	}
#endif
	uint32_t st1 = (cfg_read32(1, dev, 0, 0x04) >> 19) & 1;   /* Status.Interrupt */
	uint32_t claim;
	int via_isr = 0;
 
#if ISR_AVAILABLE
	via_isr = (isr_count > 0);
#endif
	int isr_runs = 0;
 
#if ISR_AVAILABLE
	isr_runs = isr_count;
#endif
	if (via_isr) {
		claim = 0;              /* Zephyr's PLIC driver claimed it before calling the ISR */
		printk("         INTx asserted: endpoint status.int=%u, ISR ran %d time(s) "
		       "(PLIC claim done by Zephyr's driver)\n", st1, isr_runs);
	} else {
		claim = sys_read32(PLIC_BASE + 0x200004);   /* poll the claim register */
		printk("         INTx asserted: endpoint status.int=%u plic_claim=%u (expected %u) via polling\n",
		       st1, claim, PCIE_IRQ);
	}
 
	sys_write32(0, BAR0_ADDR + REG_DOORBELL);                 /* clear the device */
	if (!via_isr && claim) {
		sys_write32(claim, PLIC_BASE + 0x200004);         /* complete */
	}
	uint32_t st2 = (cfg_read32(1, dev, 0, 0x04) >> 19) & 1;
 
	printk("         INTx cleared:  endpoint status.int=%u\n", st2);
	r->intx = (st1 == 1 && st2 == 0);
	r->intx_plic = via_isr ? (isr_runs >= 1) : (claim == PCIE_IRQ);
	r->isr = via_isr;
}
 
/* MSI: firmware programs the MSI capability; the doorbell makes the Endpoint write MSI_DATA to MSI_ADDR */
static void msi_test(uint8_t dev, struct results *r)
{
	plic_enable(MSI_IRQ);
	cfg_write32(1, dev, 0, 0x44, MSI_ADDR);
	cfg_write32(1, dev, 0, 0x48, MSI_DATA);
	cfg_write32(1, dev, 0, 0x40, cfg_read32(1, dev, 0, 0x40) | (1U << 16));   /* MSI enable */
	printk("         MSI cap programmed: addr=0x%08x data=0x%x ctrl=0x%08x\n",
	       cfg_read32(1, dev, 0, 0x44), cfg_read32(1, dev, 0, 0x48), cfg_read32(1, dev, 0, 0x40));
 
	sys_write32(1, BAR0_ADDR + REG_DOORBELL);
 
	uint32_t data = sys_read32(MSI_ADDR);                  /* message data held by the MSI window */
	uint32_t claim = sys_read32(PLIC_BASE + 0x200004);
 
	printk("         MSI received: data=0x%x plic_claim=%u (expected %u)\n", data, claim, MSI_IRQ);
 
	sys_write32(0, MSI_ADDR);                              /* clear the MSI window interrupt */
	if (claim) {
		sys_write32(claim, PLIC_BASE + 0x200004);
	}
	cfg_write32(1, dev, 0, 0x40, cfg_read32(1, dev, 0, 0x40) & ~(1U << 16));  /* MSI off again */
	r->msi = (data == MSI_DATA && claim == MSI_IRQ);
}
 
static const char *pf(int ok)
{
	return ok ? "PASS" : "FAIL";
}
 
int main(void)
{
	struct results r = {0};
 
	printk("== Raw-MMIO PCIe enumeration (Type 1 Root Port + Endpoint) ==\n");
 
	/* ---- Root Port: 00:00.0 (Type 1 bridge) ---- */
	uint32_t id = cfg_read32(0, 0, 0, 0x00);
 
	if (id == 0xFFFFFFFF) {
		printk("00:00.0  no root port\n");
		return 0;
	}
	uint32_t htype = (cfg_read32(0, 0, 0, 0x0C) >> 16) & 0x7F;
 
	printk("00:00.0  vendor=0x%04x device=0x%04x header_type=0x%02x\n",
	       id & 0xFFFF, id >> 16, htype);
	printk("         class/rev=0x%08x (bridge)\n", cfg_read32(0, 0, 0, 0x08));
	r.root_port = (htype == 1);
 
	cfg_write32(0, 0, 0, 0x18, 0x00010100);  /* primary 0, secondary 1, subordinate 1 */
	uint32_t busnr = cfg_read32(0, 0, 0, 0x18) & 0xFFFFFF;
 
	printk("         bus numbers pri/sec/sub = 0x%06x\n", busnr);
	r.bus_nr = (busnr == 0x010100);
 
	cfg_write32(0, 0, 0, 0x20, 0xA000A000);  /* memory base/limit: 0xA0000000..0xA00FFFFF */
	printk("         memory base/limit reg = 0x%08x\n", cfg_read32(0, 0, 0, 0x20));
 
	cfg_write32(0, 0, 0, 0x04, cfg_read32(0, 0, 0, 0x04) | 0x6);  /* MSE + bus master */
 
	/* ---- Secondary bus 1: scan for Endpoints ---- */
	for (int dev = 0; dev < 4; dev++) {
		uint32_t eid = cfg_read32(1, dev, 0, 0x00);
 
		if (eid == 0xFFFFFFFF) {
			printk("01:%02x.0  no device\n", dev);
			continue;
		}
		printk("01:%02x.0  vendor=0x%04x device=0x%04x\n", dev, eid & 0xFFFF, eid >> 16);
		printk("         class/rev=0x%08x\n", cfg_read32(1, dev, 0, 0x08));
		print_caps(1, dev, 0);
		r.connector = 1;      /* the answer came from the other machine through the connector */
		r.enumerated = 1;
		r.ids = ((eid & 0xFFFF) == 0x1234 && (eid >> 16) == 0x0002);
 
		/* BAR sizing */
		uint32_t orig = cfg_read32(1, dev, 0, 0x10);
 
		cfg_write32(1, dev, 0, 0x10, 0xFFFFFFFF);
		uint32_t mask = cfg_read32(1, dev, 0, 0x10);
 
		cfg_write32(1, dev, 0, 0x10, orig);
		uint32_t size = ~(mask & ~0xFUL) + 1;
 
		printk("         BAR0 mask=0x%08x size=0x%x bytes\n", mask, size);
		r.bar_size = (size == 0x1000);
 
		/* assign BAR + enable memory space and bus mastering */
		cfg_write32(1, dev, 0, 0x10, BAR0_ADDR);
		cfg_write32(1, dev, 0, 0x04, cfg_read32(1, dev, 0, 0x04) | 0x6);
		uint32_t bar_now = cfg_read32(1, dev, 0, 0x10);
 
		printk("         BAR0 now=0x%08x CMD=0x%08x\n", bar_now, cfg_read32(1, dev, 0, 0x04));
		r.bar_assign = (bar_now == BAR0_ADDR);
 
		/* MMIO round trip through BAR0 */
		sys_write32(0x12345678, BAR0_ADDR + REG_SCRATCH);
		uint32_t rb = sys_read32(BAR0_ADDR + REG_SCRATCH);
		uint32_t magic = sys_read32(BAR0_ADDR + REG_MAGIC);
 
		r.mmio_write = (rb == 0x12345678);
		r.mmio_read = (magic == 0xE0E0E0E0);
		r.round_trip = r.mmio_write && r.mmio_read;
		printk("         BAR0 scratch wrote=0x12345678 read=0x%08x magic=0x%08x %s\n",
		       rb, magic, r.round_trip ? "OK" : "FAIL");
 
		intx_test(dev, &r);
		msi_test(dev, &r);
	}
 
	printk("\n========== GOAL 4 TEST SUMMARY ==========\n");
	printk("Root Port (Type 1 bridge)   : %s\n", pf(r.root_port));
	printk("Bus numbers programmed      : %s\n", pf(r.bus_nr));
	printk("Connector (RP -> EP)        : %s\n", pf(r.connector));
	printk("Endpoint enumeration        : %s\n", pf(r.enumerated));
	printk("Vendor/Device ID read       : %s\n", pf(r.ids));
	printk("BAR size detection (4 KB)   : %s\n", pf(r.bar_size));
	printk("BAR address assignment      : %s\n", pf(r.bar_assign));
	printk("Endpoint MMIO write         : %s\n", pf(r.mmio_write));
	printk("Endpoint MMIO read          : %s\n", pf(r.mmio_read));
	printk("MMIO round trip             : %s\n", pf(r.round_trip));
	printk("INTx interrupt (endpoint)   : %s\n", pf(r.intx));
	printk("INTx reached CPU (ISR/claim) : %s\n", pf(r.intx_plic));
	printk("MSI (capability -> PLIC)    : %s\n", pf(r.msi));
	printk("Interrupt handled by ISR    : %s\n", r.isr ? "PASS" : "NOT TAKEN (polled instead)");
 
	int all = r.root_port && r.bus_nr && r.connector && r.enumerated && r.ids && r.bar_size &&
		  r.bar_assign && r.round_trip && r.intx && r.intx_plic && r.msi;
 
	printk("\nOverall Result: %s\n", all ? "SUCCESS" : "FAILURE");
	printk("== done ==\n");
	return 0;
}
 