using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace BatteryPatch
{
    /// <summary>
    /// The two patches, as byte edits. Each edit checks the original bytes before replacing them, and the result must
    /// match a known SHA-256, so the patch can only be applied to the exact firmware it was made for.
    /// Details and disassembly: docs/how-it-works.md.
    /// </summary>
    static class Firmware
    {
        public const byte Magic = 0xBA;

        // ------------------------------------------------------------ controller: official 1.09, zone 0x6000-0x19000
        public const int PadBase = 0x6000, PadPayloadSize = 0x12400, PadEnd = 0x19000;
        public const string PadPayloadSha = "dfd443c77f831008aa453ee2c1c9cb6871c36f8947c548e61d4387fba0a9da70";   // official 1.09 (.dat without its 0x1C header)
        public const string PadPatchedSha = "01b2f36c92799c381b6b1c7ddb7d3f4f48813d43500f8fa993bfd4586c19bfe2";   // zone 0x6000-0x19000 after the patch

        // ------------------------------------------------------------ dongle: factory 1.00, bank A 0x6000-0x10000
        public const int DongleBase = 0x6000, DongleSize = 0xA000, DongleEnd = 0x10000;
        public const string DongleFactorySha = "311768ba6c29453bf7ecc8bfb9b9f3d6810d65cd9f0992ff1d201566b441a792";
        public const string DonglePatchedSha = "e106d8b50c5541bca0e9f00d76b2db00f520a99b3ef56af15af318381772deb4";

        sealed class Edit
        {
            public int Addr; public byte[] Old, New;
            public Edit(int addr, byte[] old, byte[] @new) { Addr = addr; Old = old; New = @new; if (old.Length != @new.Length) throw new ArgumentException(); }
        }

        const ushort Nop = 0x46C0; // Thumb "mov r8, r8"

        /// <summary>Telink TC32 is Thumb-1 with the top nibble of each halfword remapped.</summary>
        static readonly int[] ThumbToTc32 = { 0xF, 0xE, 0xA, 0xB, 0x0, 0x1, 0x5, 0x4, 0x2, 0x3, 0x7, 0x6, 0xD, 0xC, 0x8, 0x9 };

        static byte[] Tc32(params int[] thumb) =>
            thumb.SelectMany(h => BitConverter.GetBytes((ushort)(ThumbToTc32[h >> 12 & 0xF] << 12 | h & 0xFFF))).ToArray();

        static byte[] Bl(int at, int target)
        {
            int off = target - (at + 4);
            return Tc32(0xF000 | (off >> 12) & 0x7FF, 0xF800 | (off & 0xFFF) >> 1);
        }

        static byte[] Hex(string s) => s.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();
        static byte[] U32(uint v) => BitConverter.GetBytes(v);

        /// <summary>Controller (radio send function at 0xdd78): every packet uses the 22-byte format, byte 18 = battery %
        /// (RAM 0x843415, kept up to date by the firmware's battery task), byte 19 = 0xBA.</summary>
        static readonly Edit[] PadEdits =
        {
            new Edit(0xDD90, Hex("03 c0"), Tc32(Nop)),                          // beq → nop : always 22 bytes
            new Edit(0xDDC8, Hex("06 c0"), Tc32(Nop)),                          // beq → nop : always fill bytes 18-19
            new Edit(0xDE14, U32(0x843487), U32(0x843415)),                     // literal: battery % instead of link quality
            new Edit(0xDDD2, Hex("fd 97 e9 9b"), Tc32(0x2000 | Magic, Nop)),     // bl 0xb5a8 → movs r0, #0xBA
        };

        /// <summary>Dongle: keep bytes 18-19 of every game packet, and copy them into the identify reply (bytes 8-9 on the PC side).</summary>
        static readonly Edit[] DongleEdits = BuildDongleEdits();

        static Edit[] BuildDongleEdits()
        {
            const int helperAt = 0xC100;
            byte[] helper = Tc32(
                0x2364,   // movs r3, #0x64       (as the original: version word on the stack)
                0x9310,   // str  r3, [sp, #0x40]
                0xAA00,   // add  r2, sp, #0      (reply buffer, on the caller's stack)
                0x4B03,   // ldr  r3, =0x842e47   (byte 18 of the last packet: battery)
                0x781B,   // ldrb r3, [r3]
                0x71D3,   // strb r3, [r2, #7]
                0x4B02,   // ldr  r3, =0x842e1c   (byte 19: signature)
                0x781B,   // ldrb r3, [r3]
                0x7213,   // strb r3, [r2, #8]
                0x4770    // bx   lr
            ).Concat(U32(0x842E47)).Concat(U32(0x842E1C)).ToArray();
            return new[]
            {
                new Edit(0x6C28, Hex("05 c1"), Tc32(Nop)),                       // bne → nop : keep bytes 18-19 of every packet
                new Edit(0xAC0E, Hex("64 a3 10 33"), Bl(0xAC0E, helperAt)),       // identify reply: call the helper
                new Edit(helperAt, Enumerable.Repeat((byte)0xFF, helper.Length).ToArray(), helper),
            };
        }

        /// <summary>Bytes at 0xDDD2 (controller) and 0xAC0E (dongle) once patched: a quick way to tell a patched device.</summary>
        public static byte[] PadMarker => PadEdits[3].New;
        public static int PadMarkerAddr => PadEdits[3].Addr;
        public static byte[] DongleMarker => DongleEdits[1].New;
        public static int DongleMarkerAddr => DongleEdits[1].Addr;

        static byte[] Apply(byte[] image, int baseAddr, Edit[] edits, bool undo)
        {
            byte[] img = (byte[])image.Clone();
            foreach (Edit e in edits)
            {
                byte[] from = undo ? e.New : e.Old, to = undo ? e.Old : e.New;
                int o = e.Addr - baseAddr;
                if (!img.Skip(o).Take(from.Length).SequenceEqual(from)) return null;
                Array.Copy(to, 0, img, o, to.Length);
            }
            return img;
        }

        public static string Sha(byte[] b) { using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

        /// <summary>Official 1.09 zone 0x6000-0x19000 (payload then 0xFF).</summary>
        public static byte[] PadOfficialZone(byte[] dat)
        {
            if (dat == null || dat.Length < 0x1C) return null;
            byte[] payload = dat.Skip(0x1C).ToArray();
            if (payload.Length != PadPayloadSize || Sha(payload) != PadPayloadSha) return null;
            if (dat[0] != 0x6D || BitConverter.ToInt32(dat, 4) != PadBase || BitConverter.ToInt32(dat, 8) != PadPayloadSize || BitConverter.ToUInt16(dat, 12) != 0x301B) return null;
            byte[] zone = Enumerable.Repeat((byte)0xFF, PadEnd - PadBase).ToArray();
            Array.Copy(payload, zone, PadPayloadSize);
            return zone;
        }

        public static byte[] PadPatchedZone(byte[] officialZone)
        {
            byte[] z = Apply(officialZone, PadBase, PadEdits, false);
            return z != null && Sha(z) == PadPatchedSha ? z : null;
        }

        public static byte[] DonglePatch(byte[] factoryBank)
        {
            if (Sha(factoryBank) != DongleFactorySha) return null;
            byte[] b = Apply(factoryBank, DongleBase, DongleEdits, false);
            return b != null && Sha(b) == DonglePatchedSha ? b : null;
        }

        public static byte[] DongleUnpatch(byte[] patchedBank)
        {
            if (Sha(patchedBank) != DonglePatchedSha) return null;
            byte[] b = Apply(patchedBank, DongleBase, DongleEdits, true);
            return b != null && Sha(b) == DongleFactorySha ? b : null;
        }

        // ------------------------------------------------------------ official firmware, from 8BitDo's own update server

        const string Server = "http://dl.8bitdo.com:8080";   // the server 8BitDo's Upgrade Tool uses; the file is checked by SHA-256
        const string TypeUltimate2C = "75";

        /// <summary>Official 1.09 .dat: a .dat file next to the program if there is one, otherwise downloaded from 8BitDo.</summary>
        public static byte[] OfficialDat(string folder, Action<string> say)
        {
            foreach (string f in Directory.GetFiles(folder, "*.dat"))
            {
                byte[] d = File.ReadAllBytes(f);
                if (PadOfficialZone(d) != null) { say(T.Get($"Official firmware 1.09 found: {Path.GetFileName(f)}", $"Firmware officiel 1.09 trouvé : {Path.GetFileName(f)}")); return d; }
            }
            say(T.Get("Downloading the official firmware 1.09 from 8BitDo's server…", "Téléchargement du firmware officiel 1.09 sur le serveur de 8BitDo…"));
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(Server + "/firmware/select");
                req.Method = "POST"; req.Timeout = 15000; req.ContentLength = 0;
                req.Headers.Add("Beta", "1"); req.Headers.Add("Type", TypeUltimate2C);
                string json;
                using (var r = req.GetResponse()) using (var s = new StreamReader(r.GetResponseStream())) json = s.ReadToEnd();
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                string path = null;
                if (root != null && root.TryGetValue("list", out object list) && list is IEnumerable items)
                    foreach (object o in items)
                        if (o is Dictionary<string, object> e && e.TryGetValue("version", out object v) && e.TryGetValue("filePathName", out object p)
                            && Math.Abs(Convert.ToDouble(v, CultureInfo.InvariantCulture) - 1.09) < 0.001)
                            path = p as string;
                if (path == null) { say(T.Get("8BitDo's server no longer lists version 1.09.", "Le serveur de 8BitDo ne propose plus la version 1.09.")); return null; }
                using (var wc = new WebClient())
                {
                    byte[] dat = wc.DownloadData(Server + path);
                    if (PadOfficialZone(dat) == null) { say(T.Get("The downloaded file is not the expected official 1.09: not used.", "Le fichier téléchargé n'est pas la 1.09 officielle attendue : il n'est pas utilisé.")); return null; }
                    say(T.Get("Downloaded and verified (SHA-256).", "Téléchargé et vérifié (SHA-256)."));
                    return dat;
                }
            }
            catch (Exception ex)
            {
                say(T.Get("Download failed: ", "Échec du téléchargement : ") + ex.Message);
                return null;
            }
        }
    }
}
