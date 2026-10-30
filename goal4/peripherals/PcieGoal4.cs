using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.PCI;

namespace Antmicro.Renode.Connectors
{
    public interface IPcieConfigTarget
    {
        uint ConfigRead(int reg);
        void ConfigWrite(int reg, uint value);
    }
 
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
                    if(rootPort != null)
                    {
                        throw new RecoverableException("Root Port is already attached to this PCIe connector.");
                    }
                    rootPort = peripheral;
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
                if(ReferenceEquals(peripheral, rootPort)) { rootPort = null; }
                if(ReferenceEquals(peripheral, endpoint)) { endpoint = null; }
            }
        }
 
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
 
        private IPeripheral rootPort;
        private IPcieConfigTarget endpoint;
        private readonly object sync = new object();
    }
}
 
namespace Antmicro.Renode.Peripherals.PCI
{
    using Antmicro.Renode.Connectors;
 
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
 
        public long Size => 0x100000;
 
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
                case 0x10: 
                    cfg[reg / 4] = value & 0xFFFFF000;
                    break;
                case 0x04: 
                    cfg[reg / 4] = (cfg[reg / 4] & 0xFFFF0000) | (value & 0x0000FFFF);
                    break;
                case 0x00: case 0x08: case 0x0C:
                    break;
                default:
                    cfg[reg / 4] = value;
                    break;
            }
        }
        public uint ReadDoubleWord(long offset)
        {
            if(offset == 0x04)
            {
                return 0xE0E0E0E0;
            }
            return regs[(offset & 0xFFC) / 4];
        }
 
        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset == 0x04)
            {
                return;
            }
            regs[(offset & 0xFFC) / 4] = value;
        }
 
        public long Size => 0x1000;
 
        private uint[] cfg;
        private uint[] regs;
        private readonly IMachine machine;
    }
}
 