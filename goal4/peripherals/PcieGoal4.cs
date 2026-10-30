using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.PCI;
 
// Goal 4 (part 1): Root Port + Endpoint + connector, all in ONE file so that
// Renode compiles them into a single assembly (no cross-file type references).
// Pattern of the connector is copied from Renode's GPIOConnector:
//   IExternal + IConnectable<IPeripheral> + AttachTo + CreateXxxConnector extension.
 
namespace Antmicro.Renode.Connectors
{
    // What the connector needs from the Endpoint side.
    public interface IPcieConfigTarget
    {
        uint ConfigRead(int reg);
        void ConfigWrite(int reg, uint value);
        uint BarRead(long offset);
        void BarWrite(long offset, uint value);
    }
 
    // What the connector needs from the Root Port side.
    public interface IPcieConnectorClient
    {
        void SetConnector(PcieConnector connector);
    }
 
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
                }
                else
                {
                    throw new RecoverableException("Peripheral must be a PCIe Root Port or a PCIe Endpoint.");
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
 
        // Only bus 0 / device 0 / function 0 is wired to the Endpoint.
        public uint ConfigRead(int dev, int fn, int reg)
        {
            lock(sync)
            {
                if(endpoint == null || dev != 0 || fn != 0)
                {
                    return 0xFFFFFFFF;
                }
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
                endpoint.ConfigWrite(reg, value);
            }
        }
 
        // BAR0 accesses (memory space) are forwarded to the Endpoint as well.
        public uint BarRead(long offset)
        {
            lock(sync)
            {
                return endpoint == null ? 0xFFFFFFFF : endpoint.BarRead(offset);
            }
        }
 
        public void BarWrite(long offset, uint value)
        {
            lock(sync)
            {
                if(endpoint != null)
                {
                    endpoint.BarWrite(offset, value);
                }
            }
        }
 
        private readonly List<IPeripheral> clients = new List<IPeripheral>();
        private IPcieConfigTarget endpoint;
        private readonly object sync = new object();
    }
}
 
namespace Antmicro.Renode.Peripherals.PCI
{
    using Antmicro.Renode.Connectors;
 
    // Root Port side (machine "rp"). Still a simplified ECAM window (Type 1 header comes later),
    // but config accesses now go through the connector instead of an internal cfg[] array.
    public class PcieRootPort : IDoubleWordPeripheral, IKnownSize, IPcieConnectorClient
    {
        public PcieRootPort(IMachine machine)
        {
            this.machine = machine;
        }
 
        public void SetConnector(PcieConnector connector)
        {
            this.connector = connector;
        }
 
        public void Reset() { }
 
        public uint ReadDoubleWord(long offset)
        {
            Decode(offset, out var bus, out var dev, out var fn, out var reg);
            if(bus != 0 || connector == null)
            {
                return 0xFFFFFFFF;
            }
            var v = connector.ConfigRead(dev, fn, reg);
            if(v != 0xFFFFFFFF)
            {
                this.Log(LogLevel.Debug, "RP CFG RD {0:X2}:{1:X2}.{2} reg 0x{3:X} -> 0x{4:X8} (via connector)", bus, dev, fn, reg, v);
            }
            return v;
        }
 
        public void WriteDoubleWord(long offset, uint value)
        {
            Decode(offset, out var bus, out var dev, out var fn, out var reg);
            if(bus != 0 || connector == null)
            {
                return;
            }
            this.Log(LogLevel.Debug, "RP CFG WR {0:X2}:{1:X2}.{2} reg 0x{3:X} <- 0x{4:X8} (via connector)", bus, dev, fn, reg, value);
            connector.ConfigWrite(dev, fn, reg, value);
        }
 
        public long Size => 0x100000;   // 1 MB ECAM window, bus 0 only
 
        private static void Decode(long offset, out int bus, out int dev, out int fn, out int reg)
        {
            reg = (int)(offset & 0xFFC);
            bus = (int)((offset >> 20) & 0xFF);
            dev = (int)((offset >> 15) & 0x1F);
            fn  = (int)((offset >> 12) & 0x7);
        }
 
        private PcieConnector connector;
        private readonly IMachine machine;
    }
 
    // BAR window on the Root Port machine: memory-space accesses to the address the firmware
    // assigned to BAR0 (0xA0000000) are forwarded through the connector to the Endpoint.
    // The address is fixed in the .repl (it matches what the firmware programs into BAR0).
    public class PcieBarWindow : IDoubleWordPeripheral, IKnownSize, IPcieConnectorClient
    {
        public PcieBarWindow(IMachine machine)
        {
            this.machine = machine;
        }
 
        public void SetConnector(PcieConnector connector)
        {
            this.connector = connector;
        }
 
        public void Reset() { }
 
        public uint ReadDoubleWord(long offset)
        {
            if(connector == null)
            {
                return 0xFFFFFFFF;
            }
            var v = connector.BarRead(offset);
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
            connector.BarWrite(offset, value);
        }
 
        public long Size => 0x1000;
 
        private PcieConnector connector;
        private readonly IMachine machine;
    }
 
    // Endpoint side (machine "ep"): Type 0 header + BAR0 (4 KB register block).
    // Config space is reached through the connector (IPcieConfigTarget).
    // The BAR0 register block is also visible on the ep machine's own bus (forwarding of BAR
    // accesses from rp is a later part).
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
            cfg[0x00 / 4] = 0x00021234;  // Device ID 0x0002 | Vendor ID 0x1234
            cfg[0x04 / 4] = 0x00100000;  // Status: capabilities list; Command = 0
            cfg[0x08 / 4] = 0x05800001;  // Class 0x05 (memory), subclass 0x80, Rev 1
            cfg[0x0C / 4] = 0x00000000;  // Header type 0
            cfg[0x10 / 4] = 0x00000000;  // BAR0
        }
 
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
                case 0x00: case 0x08: case 0x0C: // read-only
                    break;
                default:
                    cfg[reg / 4] = value;
                    break;
            }
        }
 
        // ---- BAR0 register block ----
        // 0x00: SCRATCH (read/write)   0x04: MAGIC (read-only, 0xE0E0E0E0)
        // Reached from the Root Port machine through the connector (BarRead/BarWrite),
        // and also directly on the ep machine's own bus (ReadDoubleWord/WriteDoubleWord).
        // Memory Space Enable (Command bit 1) must be set, like on real hardware.
        public uint BarRead(long offset)
        {
            if((cfg[0x04 / 4] & 0x2) == 0)
            {
                this.Log(LogLevel.Warning, "EP BAR RD ignored: Memory Space Enable is off");
                return 0xFFFFFFFF;
            }
            var v = offset == 0x04 ? 0xE0E0E0E0 : regs[(offset & 0xFFC) / 4];
            this.Log(LogLevel.Debug, "EP BAR RD off 0x{0:X} -> 0x{1:X8}", offset, v);
            return v;
        }
 
        public void BarWrite(long offset, uint value)
        {
            if((cfg[0x04 / 4] & 0x2) == 0)
            {
                this.Log(LogLevel.Warning, "EP BAR WR ignored: Memory Space Enable is off");
                return;
            }
            this.Log(LogLevel.Debug, "EP BAR WR off 0x{0:X} <- 0x{1:X8}", offset, value);
            if(offset == 0x04)
            {
                return;
            }
            regs[(offset & 0xFFC) / 4] = value;
        }
 
        public uint ReadDoubleWord(long offset)
        {
            return BarRead(offset);
        }
 
        public void WriteDoubleWord(long offset, uint value)
        {
            BarWrite(offset, value);
        }
 
        public long Size => 0x1000;
 
        private uint[] cfg;
        private uint[] regs;
        private readonly IMachine machine;
    }
}
 