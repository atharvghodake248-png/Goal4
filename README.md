# Goal 4: PCIe Root Port and Endpoint Simulation using Renode

## Overview
A PCIe Root Port and Endpoint simulation built with Renode and Zephyr RTOS. Two emulated AE350 machines are linked by a custom in-process connector. It demonstrates configuration-space access, BAR-based MMIO, and INTx interrupt signalling between the machines. The Root Port firmware uses hand-written MMIO only (no PCIe driver API).

## Version to review
- Branch: `goal4-charter-only` (charter scope: INTx round trip, no MSI)
- Code commit: `c405e4a` (adds machine timer and PLIC_SW to the AE350 base platform)
- MSI is not part of this branch. The MSI version (MSI window, bridge window checks, Bus Master gating) is commit `94cd4ea` on `main`.

## Architecture
1. **Root Port machine (rp):** Type 1 Root Port, BAR window and the Root Port Zephyr firmware.
2. **Endpoint machine (ep):** Type 0 Endpoint (BAR0, capability list with one vendor-specific capability) and the Endpoint Zephyr firmware.
3. **PcieConnector:** modeled on GPIOConnector. Forwards config, BAR and INTx traffic between the two machines. In this branch it does not check the bridge memory window or Bus Master Enable.

## Configuration
| Component | Address / Value |
|---|---|
| Root Port (Type 1 bridge) | `0x90000000` |
| Root Port BAR window | `0xA0000000` |
| Endpoint BAR0 | `0xA0000000`, 4 KB |
| Root Port IDs | vendor `0x1234`, device `0x0001` |
| Endpoint IDs | vendor `0x1234`, device `0x0002` |

## Repository structure
```text
goal3/
goal4/
+-- ae350_base.repl        (AE350 base + machine timer + PLIC_SW)
+-- ae350_pcie_rp.repl
+-- pcie_ep.repl
+-- run.resc
+-- peripherals/PcieGoal4.cs
+-- fw/, fw_ep/            (prebuilt zephyr.elf)
+-- fw_src/, fw_ep_src/    (firmware sources)
```

## How to run
```text
cd D:\Renode\renode
.\renode.exe D:\1_Project\goal4\run.resc
```
Then type `start` in the Renode monitor.

## Expected result
The Root Port UART window prints the Goal 4 test summary. All items PASS: Root Port (Type 1 bridge), bus numbers programmed, connector (RP -> EP), endpoint enumeration, vendor/device ID read, BAR size detection (4 KB), BAR address assignment, endpoint MMIO write/read, MMIO round trip, INTx interrupt, INTx reached CPU, interrupt handled by ISR. The run ends with `Overall Result: SUCCESS`.

## Known warnings
- `Machine Cache Control CSR (0x7ca) ... not supported`: Renode does not implement this Andes CSR.
- One `rp/plic: Unhandled read from offset 0x1080` after the INTx assert. Cause not yet identified. It does not affect the result.
- The earlier PLIC warnings came from Zephyr using the machine timer (0xE6000000) and PLIC_SW (0xE6400000), which the base Renode AE350 platform does not model. They are modeled in `ae350_base.repl`, not suppressed.

## Rebuilding the firmware (WSL)
```text
west build -b adp_xc7k/ae350 pcie_raw -p always
west build -b adp_xc7k/ae350 pcie_ep -d build_ep -p always
```