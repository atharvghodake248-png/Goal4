using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.PCI;
 
// Goal 4: Root Port (Type 1) + Endpoint (Type 0, BAR, capability list, MSI) + connector.
// All classes are in ONE file so Renode compiles them into a single assembly.
// Connector pattern copied from Renode's GPIOConnector:
//   IExternal + IConnectable<IPeripheral> + AttachTo + CreateXxxConnector extension.
 
namespace Antmicro.Renode.Connectors
{
    // ---- what the connector needs from each side ----
    public interface IPcieConfigTarget                      // Endpoint side
    {
        uint ConfigRead(int reg);
        void ConfigWrite(int reg, uint value);
        uint BarRead(uint address);
        void BarWrite(uint address, uint value);
        void SetUpstream(PcieConnector connector);
    }
 
    public interface IPcieConnectorClient                   // Root Port machine side
    {
        void SetConnector(PcieConnector connector);
        void Trace(string message);
    }
 
    public interface IPcieInterruptSink { void SetInterrupt(bool level); }              // INTx line
    public interface IPcieMsiSink { void ReceiveMsi(uint address, uint data); }         // MSI memory write
    public interface IPcieBridge { bool WindowCovers(uint address); bool AddressInWindow(uint address); bool BusMasterEnabled(); }
 
    public static class PcieConnectorExtensions
    {
        public static void CreatePcieConnector(this Emulation emulation, string name)
        {
            emulation.ExternalsManager.AddExternal(new PcieConnector(), name);
        }
    }
 
    public class PcieConnector : IExternal, IConnectable<IPeripheral>
    {
        public void AttachTo(IPeripheral peripheral)
        {
            lock(sync)
            {
                if(peripheral is IPcieConnectorClient client)
                {
                    clients.Add(peripheral);
                    client.SetConnector(this);
                }
                else if(peripheral is IPcieConfigTarget target)
                {
                    if(endpoint != null)
                    {
                        throw new RecoverableException("Endpoint is already attached to this PCIe connector.");
                    }
                    endpoint = target;
                    target.SetUpstream(this);
                }
                else
                {
                    throw new RecoverableException("Peripheral must be a PCIe Root Port side peripheral or a PCIe Endpoint.");
                }
            }
        }
 
        public void DetachFrom(IPeripheral peripheral)
        {
            lock(sync)
            {
                clients.Remove(peripheral);
                if(ReferenceEquals(peripheral, endpoint)) { endpoint = null; }
            }
        }
 
        // The connector has no logger of its own; it asks the first Root Port side peripheral to print.
        public void Trace(string message)
        {
            lock(sync)
            {
                if(clients.Count > 0 && clients[0] is IPcieConnectorClient c)
                {
                    c.Trace(message);
                }
            }
        }
 
        // ---- config space: bus 1 / device 0 / function 0 is the Endpoint ----
        public uint ConfigRead(int dev, int fn, int reg)
        {
            lock(sync)
            {
                if(endpoint == null || dev != 0 || fn != 0)
                {
                    return 0xFFFFFFFF;
                }
                Trace(string.Format("[CONNECTOR] ConfigRead reg 0x{0:X} forwarded to Endpoint", reg));
                return endpoint.ConfigRead(reg);
            }
        }
 
        public void ConfigWrite(int dev, int fn, int reg, uint value)
        {
            lock(sync)
            {
                if(endpoint == null || dev != 0 || fn != 0)
                {
                    return;
                }
                Trace(string.Format("[CONNECTOR] ConfigWrite reg 0x{0:X} forwarded to Endpoint", reg));
                endpoint.ConfigWrite(reg, value);
            }
        }
 
        // ---- memory space (BAR) ----
        // The access must be inside the bridge memory window (and the bridge must have Memory Space on);
        // the Endpoint then checks it against its own BAR0.
        public uint BarRead(uint address)
        {
            lock(sync)
            {
                if(endpoint == null || !BridgeAllowsWindow(address))
                {
                    return 0xFFFFFFFF;
                }
                Trace(string.Format("[CONNECTOR] BAR read 0x{0:X8} forwarded to Endpoint", address));
                return endpoint.BarRead(address);
            }
        }
 
        public void BarWrite(uint address, uint value)
        {
            lock(sync)
            {
                if(endpoint == null || !BridgeAllowsWindow(address))
                {
                    return;
                }
                Trace(string.Format("[CONNECTOR] BAR write 0x{0:X8} forwarded to Endpoint", address));
                endpoint.BarWrite(address, value);
            }
        }
 
        // ---- interrupts: Endpoint -> Root Port side ----
        public void RaiseInterrupt(bool level)
        {
            lock(sync)
            {
                Trace(string.Format("[CONNECTOR] INTx {0} forwarded to Root Port side", level ? "assert" : "deassert"));
                foreach(var c in clients)
                {
                    if(c is IPcieInterruptSink sink)
                    {
                        sink.SetInterrupt(level);
                    }
                }
            }
        }
 
        public void SendMsi(uint address, uint data)
        {
            lock(sync)
            {
                foreach(var c in clients)
                {
                    if(c is IPcieBridge bridge && !bridge.BusMasterEnabled())
                    {
                        Trace("[CONNECTOR] MSI blocked: bridge Bus Master Enable is off");
                        return;
                    }
                }
                // An MSI is an upstream write: an address INSIDE the bridge memory window would be routed
                // downstream by a real bridge, so it can not be an upstream MSI.
                foreach(var c in clients)
                {
                    if(c is IPcieBridge bridge && bridge.AddressInWindow(address))
                    {
                        Trace(string.Format("[CONNECTOR] MSI blocked: address 0x{0:X8} is inside the bridge memory window", address));
                        return;
                    }
                }
                Trace(string.Format("[CONNECTOR] MSI write addr=0x{0:X8} data=0x{1:X} forwarded to Root Port side", address, data));
                foreach(var c in clients)
                {
                    if(c is IPcieMsiSink sink)
                    {
                        sink.ReceiveMsi(address, data);
                    }
                }
            }
        }
 
        private bool BridgeAllowsWindow(uint address)
        {
            foreach(var c in clients)
            {
                if(c is IPcieBridge bridge && !bridge.WindowCovers(address))
                {
                    Trace(string.Format("[CONNECTOR] access 0x{0:X8} blocked: outside bridge memory window or Memory Space off", address));
                    return false;
                }
            }
            return true;
        }
 
        private readonly List<IPeripheral> clients = new List<IPeripheral>();
        private IPcieConfigTarget endpoint;
        private readonly object sync = new object();
    }
}
 
namespace Antmicro.Renode.Peripherals.PCI
{
    using Antmicro.Renode.Connectors;
 
    // ===================== Root Port (machine "rp") =====================
    // Type 1 (PCI-PCI bridge) header at 00:00.0. Config accesses to the secondary bus go through the connector.
    public class PcieRootPort : IDoubleWordPeripheral, IKnownSize, IPcieConnectorClient, IPcieBridge
    {
        public PcieRootPort(IMachine machine)
        {
            this.machine = machine;
            Reset();
        }
 
        public void SetConnector(PcieConnector connector) { this.connector = connector; }
        public void Trace(string message) { this.Log(LogLevel.Debug, "{0}", message); }
 
        public void Reset()
        {
            cfg = new uint[1024];
            cfg[0x00 / 4] = 0x00011234;  // Device ID 0x0001 | Vendor ID 0x1234
            cfg[0x04 / 4] = 0x00000000;  // Command = 0, Status = 0
            cfg[0x08 / 4] = 0x06040001;  // Class 0x06 bridge, subclass 0x04 PCI-PCI, Rev 1
            cfg[0x0C / 4] = 0x00010000;  // Header type 0x01 (bridge)
        }
 
        // Bridge forwarding rules used by the connector for memory (BAR) accesses.
        public bool WindowCovers(uint address)
        {
            if((cfg[0x04 / 4] & 0x2) == 0)
            {
                return false;                                   // Memory Space Enable off
            }
            return AddressInWindow(address);
        }
 
        // Only the memory base/limit registers (no enable bits): is the address inside the bridge window?
        public bool AddressInWindow(uint address)
        {
            var v = cfg[0x20 / 4];
            uint b = (v & 0xFFF0u) << 16;                       // memory base
            uint l = (((v >> 16) & 0xFFF0u) << 16) | 0xFFFFFu;  // memory limit
            return l >= b && address >= b && address <= l;
        }
 
        public bool BusMasterEnabled() { return (cfg[0x04 / 4] & 0x4) != 0; }
 
        public uint ReadDoubleWord(long offset)
        {
            Decode(offset, out var bus, out var dev, out var fn, out var reg);
            if(bus == 0)
            {
                if(dev != 0 || fn != 0)
                {
                    return 0xFFFFFFFF;
                }
                var v = cfg[reg / 4];
                this.Log(LogLevel.Debug, "RP CFG RD 00:00.0 (bridge) reg 0x{0:X} -> 0x{1:X8}", reg, v);
                return v;
            }
            if(!IsSecondaryBus(bus) || connector == null)
            {
                return 0xFFFFFFFF;
            }
            var r = connector.ConfigRead(dev, fn, reg);
            if(r != 0xFFFFFFFF)
            {
                this.Log(LogLevel.Debug, "RP CFG RD {0:X2}:{1:X2}.{2} reg 0x{3:X} -> 0x{4:X8} (via connector)", bus, dev, fn, reg, r);
            }
            return r;
        }
 
        public void WriteDoubleWord(long offset, uint value)
        {
            Decode(offset, out var bus, out var dev, out var fn, out var reg);
            if(bus == 0)
            {
                if(dev != 0 || fn != 0)
                {
                    return;
                }
                this.Log(LogLevel.Debug, "RP CFG WR 00:00.0 (bridge) reg 0x{0:X} <- 0x{1:X8}", reg, value);
                switch(reg)
                {
                    case 0x00: case 0x08: case 0x0C: case 0x10: case 0x14: // read-only here
                        break;
                    case 0x04: // Command: lower 16 bits writable
                        cfg[reg / 4] = (cfg[reg / 4] & 0xFFFF0000) | (value & 0x0000FFFF);
                        break;
                    case 0x18: // primary / secondary / subordinate bus numbers
                        cfg[reg / 4] = value & 0x00FFFFFF;
                        break;
                    case 0x20: // memory base / limit (1 MB granularity)
                        cfg[reg / 4] = value & 0xFFF0FFF0;
                        break;
                    default:
                        cfg[reg / 4] = value;
                        break;
                }
                return;
            }
            if(!IsSecondaryBus(bus) || connector == null)
            {
                return;
            }
            this.Log(LogLevel.Debug, "RP CFG WR {0:X2}:{1:X2}.{2} reg 0x{3:X} <- 0x{4:X8} (via connector)", bus, dev, fn, reg, value);
            connector.ConfigWrite(dev, fn, reg, value);
        }
 
        public long Size => 0x1000000;  // 16 MB ECAM window = 16 buses (bus field = offset bits 27:20)
 
        private bool IsSecondaryBus(int bus)
        {
            var sec = (int)((cfg[0x18 / 4] >> 8) & 0xFF);
            return sec != 0 && bus == sec;
        }
 
        private static void Decode(long offset, out int bus, out int dev, out int fn, out int reg)
        {
            reg = (int)(offset & 0xFFC);
            bus = (int)((offset >> 20) & 0xFF);
            dev = (int)((offset >> 15) & 0x1F);
            fn  = (int)((offset >> 12) & 0x7);
        }
 
        private uint[] cfg;
        private PcieConnector connector;
        private readonly IMachine machine;
    }
 
    // ============ BAR window on the Root Port machine (memory space of the bridge) ============
    // Memory accesses to 0xA0000000.. are forwarded through the connector to the Endpoint.
    // The window address is fixed in the .repl; the connector and Endpoint still check the bridge
    // memory window, Memory Space Enable and the Endpoint's programmed BAR0 before accepting an access.
    public class PcieBarWindow : IDoubleWordPeripheral, IKnownSize, IPcieConnectorClient, IPcieInterruptSink
    {
        public PcieBarWindow(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
        }
 
        public GPIO IRQ { get; private set; }     // wired in the .repl: IRQ -> plic@20 (INTx)
 
        public void SetConnector(PcieConnector connector) { this.connector = connector; }
        public void Trace(string message) { this.Log(LogLevel.Debug, "{0}", message); }
        public void Reset() { }
 
        public void SetInterrupt(bool level)
        {
            this.Log(LogLevel.Debug, "RP INTx line -> {0}", level ? "ASSERTED" : "deasserted");
            IRQ.Set(level);
        }
 
        public uint ReadDoubleWord(long offset)
        {
            if(connector == null)
            {
                return 0xFFFFFFFF;
            }
            var v = connector.BarRead(WindowBase + (uint)offset);
            this.Log(LogLevel.Debug, "RP BAR RD off 0x{0:X} -> 0x{1:X8} (via connector)", offset, v);
            return v;
        }
 
        public void WriteDoubleWord(long offset, uint value)
        {
            if(connector == null)
            {
                return;
            }
            this.Log(LogLevel.Debug, "RP BAR WR off 0x{0:X} <- 0x{1:X8} (via connector)", offset, value);
            connector.BarWrite(WindowBase + (uint)offset, value);
        }
 
        public long Size => 0x1000;
 
        private const uint WindowBase = 0xA0000000;   // must match the .repl
        private PcieConnector connector;
        private readonly IMachine machine;
    }
 
    // ============ MSI target on the Root Port machine ============
    // An MSI is a memory write by the Endpoint to the address the firmware programmed into the MSI capability.
    // This window (0xB0000000) receives it, raises its IRQ line (-> PLIC) and holds the message data.
    // Firmware reads offset 0 to get the data and writes offset 0 to clear the interrupt.
    public class PcieMsiWindow : IDoubleWordPeripheral, IKnownSize, IPcieConnectorClient, IPcieMsiSink
    {
        public PcieMsiWindow(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
        }
 
        public GPIO IRQ { get; private set; }     // wired in the .repl: IRQ -> plic@21 (MSI)
 
        public void SetConnector(PcieConnector connector) { }
        public void Trace(string message) { this.Log(LogLevel.Debug, "{0}", message); }
        public void Reset() { lastData = 0; }
 
        public void ReceiveMsi(uint address, uint data)
        {
            if((address & 0xFFFFF000u) != WindowBase)
            {
                this.Log(LogLevel.Warning, "RP MSI write to 0x{0:X8} ignored (not the MSI window)", address);
                return;
            }
            lastData = data;
            this.Log(LogLevel.Debug, "RP MSI received addr=0x{0:X8} data=0x{1:X} -> IRQ asserted", address, data);
            IRQ.Set(true);
        }
 
        public uint ReadDoubleWord(long offset) { return lastData; }
 
        public void WriteDoubleWord(long offset, uint value)
        {
            this.Log(LogLevel.Debug, "RP MSI cleared");
            IRQ.Set(false);
        }
 
        public long Size => 0x1000;
 
        private const uint WindowBase = 0xB0000000;   // must match the .repl and the MSI address in firmware
        private uint lastData;
        private readonly IMachine machine;
    }
 
    // ===================== Endpoint (machine "ep") =====================
    // Type 0 header, BAR0 (4 KB register block), capability list with an MSI capability.
    public class PcieEndpoint : IDoubleWordPeripheral, IKnownSize, IPcieConfigTarget
    {
        public PcieEndpoint(IMachine machine)
        {
            this.machine = machine;
            Reset();
        }
 
        public void Reset()
        {
            cfg = new uint[1024];
            regs = new uint[1024];
            hostWrites = 0;
            cfg[0x00 / 4] = 0x00021234;  // Device ID 0x0002 | Vendor ID 0x1234
            cfg[0x04 / 4] = 0x00100000;  // Status: capabilities list; Command = 0
            cfg[0x08 / 4] = 0x05800001;  // Class 0x05 (memory), subclass 0x80, Rev 1
            cfg[0x0C / 4] = 0x00000000;  // Header type 0
            cfg[0x10 / 4] = 0x00000000;  // BAR0
            cfg[0x34 / 4] = 0x00000040;  // Capabilities pointer -> 0x40
            cfg[0x40 / 4] = 0x00000005;  // Cap ID 0x05 (MSI), next = 0x00, message control = 0
            // 0x44 = MSI message address, 0x48 = MSI message data
        }
 
        public void SetUpstream(PcieConnector connector) { upstream = connector; }
 
        // ---- config space (called by the connector) ----
        public uint ConfigRead(int reg)
        {
            var v = cfg[(reg & 0xFFC) / 4];
            this.Log(LogLevel.Debug, "EP CFG RD reg 0x{0:X} -> 0x{1:X8}", reg, v);
            return v;
        }
 
        public void ConfigWrite(int reg, uint value)
        {
            reg &= 0xFFC;
            this.Log(LogLevel.Debug, "EP CFG WR reg 0x{0:X} <- 0x{1:X8}", reg, value);
            switch(reg)
            {
                case 0x10: // BAR0: 4 KB 32-bit memory BAR
                    cfg[reg / 4] = value & 0xFFFFF000;
                    break;
                case 0x04: // Command: only lower 16 bits writable
                    cfg[reg / 4] = (cfg[reg / 4] & 0xFFFF0000) | (value & 0x0000FFFF);
                    break;
                case 0x00: case 0x08: case 0x0C: case 0x34: // read-only
                    break;
                case 0x40: // MSI capability header: only the MSI Enable bit (bit 16) is writable
                    cfg[reg / 4] = (cfg[reg / 4] & ~0x00010000u) | (value & 0x00010000u);
                    break;
                default:
                    cfg[reg / 4] = value;
                    break;
            }
        }
 
        // ---- memory space (called by the connector) ----
        public uint BarRead(uint address)
        {
            if(!BarHit(address))
            {
                return 0xFFFFFFFF;
            }
            var v = RegRead(address & 0xFFF);
            this.Log(LogLevel.Debug, "EP BAR RD off 0x{0:X} -> 0x{1:X8}", address & 0xFFF, v);
            return v;
        }
 
        public void BarWrite(uint address, uint value)
        {
            if(!BarHit(address))
            {
                return;
            }
            this.Log(LogLevel.Debug, "EP BAR WR off 0x{0:X} <- 0x{1:X8}", address & 0xFFF, value);
            hostWrites++;
            RegWrite(address & 0xFFF, value);
        }
 
        // Memory Space Enable (Command bit 1) must be on and the address must match the programmed BAR0.
        private bool BarHit(uint address)
        {
            if((cfg[0x04 / 4] & 0x2) == 0)
            {
                this.Log(LogLevel.Warning, "EP BAR access 0x{0:X8} ignored: Memory Space Enable is off", address);
                return false;
            }
            if((address & 0xFFFFF000u) != (cfg[0x10 / 4] & 0xFFFFF000u))
            {
                this.Log(LogLevel.Warning, "EP BAR access 0x{0:X8} ignored: does not match BAR0 0x{1:X8}", address, cfg[0x10 / 4]);
                return false;
            }
            return true;
        }
 
        // ---- BAR0 register block ----
        // 0x00: SCRATCH (read/write)   0x04: MAGIC (read-only, 0xE0E0E0E0)
        // 0x08: DOORBELL (bit 0 = 1 raises an interrupt: MSI if enabled, otherwise INTx; bit 0 = 0 clears INTx)
        // 0x0C: STATUS (read-only)  0x10: BAR0 value (read-only)  0x14: host write count (read-only)
        // 0x0C..0x14 let the firmware running on the Endpoint machine see what the Root Port side has configured.
        private uint RegRead(long offset)
        {
            switch(offset & 0xFFC)
            {
                case 0x04: return 0xE0E0E0E0;       // MAGIC
                case 0x0C: return StatusBits();      // STATUS (what the host has configured)
                case 0x10: return cfg[0x10 / 4];     // BAR0 value programmed by the host
                case 0x14: return hostWrites;        // number of BAR writes made by the host
            }
            return regs[(offset & 0xFFC) / 4];
        }
 
        // bit0 BAR0 programmed, bit1 Memory Space on, bit2 Bus Master on, bit3 MSI enabled, bit4 INTx asserted
        private uint StatusBits()
        {
            uint s = 0;
            if((cfg[0x10 / 4] & 0xFFFFF000u) != 0) { s |= 0x01; }
            if((cfg[0x04 / 4] & 0x2) != 0) { s |= 0x02; }
            if((cfg[0x04 / 4] & 0x4) != 0) { s |= 0x04; }
            if(((cfg[0x40 / 4] >> 16) & 1) != 0) { s |= 0x08; }
            if((cfg[0x04 / 4] & 0x00080000u) != 0) { s |= 0x10; }
            return s;
        }
 
        private void RegWrite(long offset, uint value)
        {
            var o = offset & 0xFFC;
            if(o == 0x04 || o == 0x0C || o == 0x10 || o == 0x14)
            {
                return;                              // read-only registers
            }
            regs[(offset & 0xFFC) / 4] = value;
            if(offset == 0x08)
            {
                Doorbell((value & 1) != 0);
            }
        }
 
        private void Doorbell(bool ring)
        {
            if(!ring)
            {
                SetIntx(false);
                return;
            }
            if(((cfg[0x40 / 4] >> 16) & 1) != 0)       // MSI enabled -> send an MSI write
            {
                if((cfg[0x04 / 4] & 0x4) == 0)
                {
                    this.Log(LogLevel.Warning, "EP MSI not sent: Bus Master Enable is off");
                    return;
                }
                var addr = cfg[0x44 / 4];
                var data = cfg[0x48 / 4] & 0xFFFF;
                this.Log(LogLevel.Debug, "EP MSI send addr=0x{0:X8} data=0x{1:X}", addr, data);
                if(upstream != null)
                {
                    upstream.SendMsi(addr, data);
                }
            }
            else
            {
                SetIntx(true);
            }
        }
 
        // Legacy INTx: Status bit 3 (Interrupt Status) mirrors the interrupt line.
        private void SetIntx(bool level)
        {
            if(level)
            {
                cfg[0x04 / 4] |= 0x00080000u;
            }
            else
            {
                cfg[0x04 / 4] &= ~0x00080000u;
            }
            this.Log(LogLevel.Debug, "EP INTx {0}", level ? "asserted" : "deasserted");
            if(upstream != null)
            {
                upstream.RaiseInterrupt(level);
            }
        }
 
        // Direct access on the ep machine's own bus (for debugging from the Renode monitor).
        public uint ReadDoubleWord(long offset) { return RegRead(offset); }
        public void WriteDoubleWord(long offset, uint value) { RegWrite(offset, value); }
 
        public long Size => 0x1000;
 
        private uint[] cfg;
        private uint[] regs;
        private PcieConnector upstream;
        private uint hostWrites;
        private readonly IMachine machine;
    }
}
 