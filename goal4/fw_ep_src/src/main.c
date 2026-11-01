#include <zephyr/kernel.h>
 
/* Firmware of the ENDPOINT machine (ep): it only boots Zephyr.
 * All PCIe traffic is driven by the Root Port machine's firmware through the connector. */
int main(void)
{
	printk("== Endpoint machine (ep): Zephyr booted ==\n");
	return 0;
}
 