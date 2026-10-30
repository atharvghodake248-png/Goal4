using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.PCI
{
    
    public class PcieStubRootPort : IDoubleWordPeripheral, IKnownSize
    {
        public PcieStubRootPort(IMachine machine)
        {
            this.machine = machine;
            Reset();
        }

        public void Reset()
        {
            cfg = new uint[1024];
            cfg[0x00 / 4] = 0x56781234;  // Device ID 0x5678 | Vendor ID 0x1234
            cfg[0x04 / 4] = 0x00100000;  // Status: capabilities list; Command = 0
            cfg[0x08 / 4] = 0x02000001;  // Class 0x02 (network), Rev 1
            cfg[0x0C / 4] = 0x00000000;  // Header type 0
            cfg[0x10 / 4] = 0x00000000;  // BAR0
            // other regs = 0
        }

        public uint ReadDoubleWord(long offset)
        {
            if (!Decode(offset, out var reg))
                return 0xFFFFFFFF;       // no device -> all 1s (as real PCIe does)
            var v = cfg[reg / 4];
            this.Log(LogLevel.Debug, "CFG RD reg 0x{0:X} -> 0x{1:X8}", reg, v);
            return v;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if (!Decode(offset, out var reg)) return;
            this.Log(LogLevel.Debug, "CFG WR reg 0x{0:X} <- 0x{1:X8}", reg, value);
            switch (reg)
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
        public long Size => 0x100000;   
        private bool Decode(long offset, out int reg)
        {
            reg = (int)(offset & 0xFFC);
            var bus = (offset >> 20) & 0xFF;
            var dev = (offset >> 15) & 0x1F;
            var fn  = (offset >> 12) & 0x7;
            return bus == 0 && dev == 0 && fn == 0;
        }
        private uint[] cfg;
        private readonly IMachine machine;
    }
}