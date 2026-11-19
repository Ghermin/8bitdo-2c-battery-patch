using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BatteryPatch
{
    /// <summary>
    /// The 8BitDo configuration channel while the firmware runs (vendor HID page FF7A or FFA0, reports 0x81 / 0x02).
    /// Only two requests are ever sent here, both read-only: identify (81 05 00 21 01) and flash read (81 05 05).
    /// The only state-changing request this tool sends on this channel is "enter update mode" (81 05 00 51 00), in Flasher.
    /// </summary>
    static class App
    {
        public const ushort Vid = 0x2DC8;
        public const int Chunk = 46;

        /// <summary>Identify reply: [0]=02 [1]=22 [2]=version x100 [6-7]=product (0x301B controller, 0x301C dongle).
        /// With the patched dongle and controller: [8]=battery %, [9]=0xBA.</summary>
        public static byte[] Identify(HidDevice dev)
        {
            var request = new byte[64];
            request[0] = 0x81; request[1] = 0x05; request[3] = 0x21; request[4] = 0x01;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                dev.Flush();
                if (!dev.Write(request)) continue;
                var b = new byte[64];
                int end = Environment.TickCount + 600;
                while (Environment.TickCount - end < 0)
                {
                    int n = dev.Read(b, Math.Max(1, end - Environment.TickCount));
                    if (n >= 10 && b[0] == 0x02 && b[1] == 0x22) return b;
                }
            }
            return null;
        }

        public static int Product(byte[] ident) => ident == null ? 0 : ident[6] | ident[7] << 8;
        public static int Version(byte[] ident) => ident == null ? 0 : ident[2];
        public static string V(int version) => (version / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Reads up to 46 bytes of flash. Never reads the register area (0x800000+): some registers clear on read.</summary>
        public static byte[] Read(HidDevice dev, int addr, int n)
        {
            if (n < 1 || n > Chunk) throw new ArgumentOutOfRangeException(nameof(n));
            if (addr < 0 || addr + n > 0x80000) throw new InvalidOperationException("flash only");
            var request = new byte[64];
            request[0] = 0x81; request[1] = 0x05; request[2] = 0x05;
            request[6] = (byte)n; request[7] = (byte)(n >> 8);
            request[14] = (byte)addr; request[15] = (byte)(addr >> 8); request[16] = (byte)(addr >> 16); request[17] = (byte)(addr >> 24);
            var b = new byte[64];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                dev.Flush();
                if (!dev.Write(request)) continue;
                int end = Environment.TickCount + 300;
                while (Environment.TickCount - end < 0)
                {
                    int got = dev.Read(b, Math.Max(1, end - Environment.TickCount));
                    if (got < 18 + n || b[0] != 0x02 || b[1] != 0x05 || b[2] != 0x00 || b[4] != 0x05) continue;
                    if ((b[6] | b[7] << 8) != n || (b[14] | b[15] << 8 | b[16] << 16 | b[17] << 24) != addr) continue;
                    return b.Skip(18).Take(n).ToArray();
                }
            }
            return null;
        }

        public static byte[] ReadRange(HidDevice dev, int addr, int length, string label = null)
        {
            var result = new byte[length];
            int lastPct = -1;
            for (int o = 0; o < length; o += Chunk)
            {
                byte[] part = Read(dev, addr + o, Math.Min(Chunk, length - o));
                if (part == null) { if (label != null) Console.WriteLine(); return null; }
                Array.Copy(part, 0, result, o, part.Length);
                if (label != null)
                {
                    int pct = (int)(100L * (o + part.Length) / length);
                    if (pct / 5 != lastPct / 5) { Console.Write($"\r  {label} {pct} %   "); lastPct = pct; }
                }
            }
            if (label != null) Console.WriteLine();
            return result;
        }

        /// <summary>Every collection of the 8BitDo configuration channel, with its identify reply.</summary>
        public static List<(HidInfo info, byte[] ident)> Find(ushort pid, ushort page)
        {
            var found = new List<(HidInfo, byte[])>();
            foreach (HidInfo h in Hid.Enumerate().Where(h => h.Vid == Vid && h.Pid == pid && h.UsagePage == page && h.InputLen >= 64 && h.OutputLen >= 64))
                using (HidDevice dev = HidDevice.Open(h))
                    if (dev != null) found.Add((h, Identify(dev)));
            return found;
        }

        public static HidInfo WaitFor(Func<HidInfo, bool> match, int ms)
        {
            int end = Environment.TickCount + ms;
            while (Environment.TickCount - end < 0)
            {
                HidInfo found = Hid.Enumerate().FirstOrDefault(match);
                if (found != null) { Thread.Sleep(500); return Hid.Enumerate().FirstOrDefault(match) ?? found; }
                Thread.Sleep(300);
            }
            return null;
        }
    }

    /// <summary>
    /// Update mode (bootloader, 2DC8:3208): 64-byte reports without ID (Windows adds ID 0).
    /// [0]=05 [1:3]=command [5:7]=length [7:9]=CRC-16/MODBUS of the data [9:13]=parameter [13:17]=address [17:]=data.
    /// Commands: 5 read, 4 erase (4 KB), 3 write (46 bytes max), 8 identity, 7 restart.
    /// Erase and write refuse any address outside [allowedStart, allowedEnd).
    /// </summary>
    sealed class Boot
    {
        public const ushort Pid = 0x3208;
        readonly HidDevice dev;
        readonly int allowedStart, allowedEnd;

        public Boot(HidDevice dev, int allowedStart, int allowedEnd) { this.dev = dev; this.allowedStart = allowedStart; this.allowedEnd = allowedEnd; }

        public static byte[] Request(int cmd, int addr = 0, int param = 0, byte[] data = null, int? datalen = null)
        {
            var r = new byte[64];
            data = data ?? new byte[0];
            r[0] = 0x05;
            r[1] = (byte)cmd; r[2] = (byte)(cmd >> 8);
            int len = datalen ?? data.Length;
            r[5] = (byte)len; r[6] = (byte)(len >> 8);
            if (data.Length > 0) { ushort crc = Crc16(data); r[7] = (byte)crc; r[8] = (byte)(crc >> 8); }
            r[9] = (byte)param; r[10] = (byte)(param >> 8); r[11] = (byte)(param >> 16); r[12] = (byte)(param >> 24);
            r[13] = (byte)addr; r[14] = (byte)(addr >> 8); r[15] = (byte)(addr >> 16); r[16] = (byte)(addr >> 24);
            Array.Copy(data, 0, r, 17, data.Length);
            return r;
        }

        static ushort Crc16(byte[] data)
        {
            int c = 0xFFFF;
            foreach (byte x in data)
            {
                c ^= x;
                for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? c >> 1 ^ 0xA001 : c >> 1;
            }
            return (ushort)c;
        }

        public bool Send(byte[] request)
        {
            var buf = new byte[65];
            Array.Copy(request, 0, buf, 1, 64);
            dev.Flush();
            return dev.Write(buf, 1000);
        }

        /// <summary>The reply repeats the command in [4] and the address in [14:18].</summary>
        byte[] Call(byte[] r, int timeoutMs = 1000, int tries = 3)
        {
            var buf = new byte[65];
            for (int t = 0; t < tries; t++)
            {
                if (!Send(r)) continue;
                int end = Environment.TickCount + timeoutMs;
                while (Environment.TickCount - end < 0)
                {
                    int n = dev.Read(buf, Math.Max(1, end - Environment.TickCount));
                    if (n < 19) continue;
                    byte[] a = buf.Skip(1).Take(64).ToArray();
                    if (a[0] == 0 && a[1] == 5 && a[2] == 0 && a[4] == r[1] && a[14] == r[13] && a[15] == r[14] && a[16] == r[15] && a[17] == r[16]) return a;
                }
            }
            return null;
        }

        public int? Identify()
        {
            byte[] a = Call(Request(8, param: 4, datalen: 4), 1000, 1);
            if (a == null || a[6] != 2 || a[7] != 0) return null;
            return a[18] | a[19] << 8;
        }

        public byte[] Read(int addr, int n)
        {
            byte[] a = Call(Request(5, addr, n, datalen: n));
            return a?.Skip(18).Take(n).ToArray();
        }

        public byte[] ReadRange(int addr, int length)
        {
            var result = new byte[length];
            for (int o = 0; o < length; o += App.Chunk)
            {
                byte[] part = Read(addr + o, Math.Min(App.Chunk, length - o));
                if (part == null) return null;
                Array.Copy(part, 0, result, o, part.Length);
            }
            return result;
        }

        void Guard(int addr, int length)
        {
            if (addr < allowedStart || length <= 0 || addr + length > allowedEnd)
                throw new InvalidOperationException($"refused: 0x{addr:x}+0x{length:x} outside 0x{allowedStart:x}-0x{allowedEnd:x}");
        }

        public bool Erase(int addr, int length)
        {
            Guard(addr, length);
            return Call(Request(4, addr, length), 15000, 1) != null;
        }

        public bool Write(int addr, int param, byte[] data)
        {
            Guard(addr, data.Length);
            if (data.Length > App.Chunk) throw new InvalidOperationException("block too long");
            return Call(Request(3, addr, param, data)) != null;
        }

        public void Restart() { Send(Request(7)); Thread.Sleep(300); }
    }
}
