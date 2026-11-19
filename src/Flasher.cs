using System;
using System.IO;
using System.Linq;

namespace BatteryPatch
{
    /// <summary>
    /// Writes a firmware zone through the update mode, following the flow of the arithon-f toolkit
    /// (u2c_customfw/ops/plan.py and ops/dongle.py): full backup first (read twice), checks against that backup once in update
    /// mode, erase, write, read back, restart. Only the application zone is ever erased or written: the update mode below it,
    /// the settings, pairings, address and calibration above it are never touched. If something fails after the erase,
    /// the device stays in update mode and running the same action again finishes the job.
    /// </summary>
    static class Flasher
    {
        const ushort PadPid = 0x310A, PadPage = 0xFF7A, PadProduct = 0x301B;
        const ushort IdlePid = 0x301C, IdlePage = 0xFFA0, DongleProduct = 0x301C;
        const int PadFlash = 0x80000, DongleFlash = 0x20000;
        const int PadMacSector = 0x76000, PadRecord = 0x3F000, DongleCal = 0x1E000, DongleRecord = 0x12000;

        public static bool DryRun, AssumeYes;

        // ================================================================ controller

        public static bool Controller(bool restore)
        {
            Ui.Title(restore ? T.Get("Restore the controller (official firmware 1.09)", "Remettre la manette d'origine (firmware officiel 1.09)")
                             : T.Get("Patch the controller", "Patcher la manette"));

            byte[] dat = Firmware.OfficialDat(Ui.Folder, Ui.Say);
            if (dat == null) { Ui.Say(T.Get("Tip: download the official 1.09 .dat yourself and put it next to this program.", "Astuce : télécharge toi-même le .dat officiel 1.09 et pose-le à côté de ce programme.")); return false; }
            byte[] official = Firmware.PadOfficialZone(dat);
            byte[] target = restore ? official : Firmware.PadPatchedZone(official);
            if (target == null) { Ui.Say(T.Get("Could not build the patched firmware: nothing done.", "Impossible de construire le firmware modifié : rien n'est fait.")); return false; }

            HidInfo stuck = Hid.Enumerate().FirstOrDefault(h => h.Vid == App.Vid && h.Pid == Boot.Pid);
            if (stuck != null)
            {
                Ui.Say(T.Get("A device is already in update mode: resuming.", "Un appareil est déjà en mode mise à jour : je reprends."));
                return PadBoot(stuck, target, null);
            }

            Ui.Say(T.Get("Connect the controller to the PC with a USB data cable.", "Branche la manette au PC avec un câble USB (qui transmet les données)."));
            var pads = Ui.WaitDevices(() => App.Find(PadPid, PadPage).Where(p => App.Product(p.ident) == PadProduct).ToList(), T.Get("controller on the cable", "manette par câble"));
            if (pads == null) return false;
            if (pads.Count != 1) { Ui.Say(T.Get("Connect only one 8BitDo controller by cable.", "Ne branche qu'une seule manette 8BitDo par câble.")); return false; }
            var pad = pads[0];
            int version = App.Version(pad.ident);
            Ui.Say(T.Get($"Controller found, firmware {App.V(version)}.", $"Manette trouvée, firmware {App.V(version)}."));
            if (version > 109)
            {
                Ui.Say(T.Get("This firmware is newer than 1.09: the patch has not been checked against it, nothing is done.",
                             "Ce firmware est plus récent que la 1.09 : le patch n'a pas été vérifié dessus, rien n'est fait."));
                return false;
            }

            byte[] backup;
            using (HidDevice dev = HidDevice.Open(pad.info))
            {
                if (dev == null) { Ui.Say(T.Get("Cannot open the controller.", "Impossible d'ouvrir la manette.")); return false; }
                byte[] marker = App.Read(dev, Firmware.PadMarkerAddr, 4);
                bool patched = marker != null && marker.SequenceEqual(Firmware.PadMarker);
                if (!restore && patched && version == 109) { Ui.Say(T.Get("This controller is already patched. Nothing to do.", "Cette manette est déjà patchée. Rien à faire.")); return true; }
                if (restore && !patched) { Ui.Say(T.Get("This controller is not patched. Nothing to do.", "Cette manette n'est pas patchée. Rien à faire.")); return true; }

                backup = Backup(dev, "controller", PadFlash, version);
                if (backup == null) return false;
            }
            if (Firmware.Sha(Slice(backup, Firmware.PadBase, Firmware.PadEnd)) == Firmware.Sha(target)) { Ui.Say(T.Get("Already up to date. Nothing to do.", "Déjà à jour. Rien à faire.")); return true; }
            if (Pending(Slice(backup, PadRecord, PadRecord + 0x24))) { Ui.Say(T.Get("An update is pending on this controller: nothing is done.", "Une mise à jour est en attente sur cette manette : rien n'est fait.")); return false; }

            if (!Ui.Confirm(restore
                    ? T.Get("The controller will get the official firmware 1.09 back. Its settings and pairings are kept.", "La manette va retrouver le firmware officiel 1.09. Ses réglages et appairages sont gardés.")
                    : T.Get("The controller will get firmware 1.09 + the battery patch. Its settings and pairings are kept.", "La manette va recevoir le firmware 1.09 + le patch batterie. Ses réglages et appairages sont gardés.")))
                return false;

            if (!EnterUpdateMode(pad.info)) return false;
            HidInfo boot = App.WaitFor(h => h.Vid == App.Vid && h.Pid == Boot.Pid, 12000);
            if (boot == null) { Ui.Say(T.Get("The controller did not enter update mode: nothing was written. Unplug it and try again.", "La manette n'est pas passée en mode mise à jour : rien n'a été écrit. Débranche-la et recommence.")); return false; }
            return PadBoot(boot, target, backup);
        }

        /// <summary>backup == null: resuming an interrupted write (only the identity is checked).</summary>
        static bool PadBoot(HidInfo bootInfo, byte[] target, byte[] backup)
        {
            if (bootInfo.OutputLen != 65 || bootInfo.InputLen != 65) { Ui.Say(T.Get("Unexpected update mode: nothing written.", "Mode mise à jour inattendu : rien n'est écrit.")); return false; }
            using (HidDevice dev = HidDevice.Open(bootInfo))
            {
                if (dev == null) { Ui.Say(T.Get("Cannot open the update mode.", "Impossible d'ouvrir le mode mise à jour.")); return false; }
                var bl = new Boot(dev, Firmware.PadBase, Firmware.PadEnd);
                int? who = bl.Identify();
                if (who != PadProduct)
                {
                    Ui.Log($"update mode identity {who:x}");
                    if (backup != null) { Ui.Say(T.Get("This is not the controller's update mode: nothing written.", "Ce n'est pas le mode mise à jour de la manette : rien n'est écrit.")); return Restart(bl, PadPid, PadPage, false); }
                    Ui.Say(T.Get("The device in update mode is not the controller (the dongle?): use the dongle action.", "L'appareil en mode mise à jour n'est pas la manette (le dongle ?) : utilise l'action du dongle."));
                    return false;
                }
                byte[] hi = bl.Read(0x20000, 16);
                if (hi == null || !hi.All(x => x == 0xFF)) { Ui.Say(T.Get("Unexpected flash content: nothing written.", "Contenu inattendu : rien n'est écrit.")); return Restart(bl, PadPid, PadPage, false); }
                if (backup != null)
                {
                    bool same = Same(bl.ReadRange(0, Firmware.PadBase), backup, 0)
                             && Same(bl.ReadRange(PadMacSector, 0x1000), backup, PadMacSector)
                             && Same(bl.ReadRange(Firmware.PadBase, Firmware.PadEnd - Firmware.PadBase), backup, Firmware.PadBase);
                    byte[] rec = bl.ReadRange(PadRecord, 0x24);
                    if (!same || rec == null || Pending(rec)) { Ui.Say(T.Get("The controller does not match its backup: nothing written.", "La manette ne correspond pas à sa sauvegarde : rien n'est écrit.")); return Restart(bl, PadPid, PadPage, false); }
                    Ui.Say(T.Get("Update mode checked against the backup.", "Mode mise à jour vérifié par rapport à la sauvegarde."));
                }
                if (DryRun) { Ui.Say(T.Get("Dry run: nothing was written.", "Essai : rien n'a été écrit.")); return Restart(bl, PadPid, PadPage, true); }

                Ui.Log("erase 0x6000-0x19000");
                if (!bl.Erase(Firmware.PadBase, Firmware.PadEnd - Firmware.PadBase)) return Stuck();
                int end = Firmware.PadBase + Firmware.PadPayloadSize;
                for (int a = Firmware.PadBase; a < Firmware.PadEnd; a += App.Chunk)
                {
                    byte[] blk = target.Skip(a - Firmware.PadBase).Take(Math.Min(App.Chunk, (a < end ? end : Firmware.PadEnd) - a)).ToArray();
                    if (a >= end && blk.All(x => x == 0xFF)) continue;
                    if (!bl.Write(a, Firmware.PadPayloadSize, blk)) return Stuck();
                    if (a < end && (a - Firmware.PadBase) % 0x1000 < App.Chunk) Console.Write($"\r  {T.Get("writing", "écriture")} {100 * (a - Firmware.PadBase) / Firmware.PadPayloadSize} %   ");
                }
                Console.WriteLine($"\r  {T.Get("writing", "écriture")} 100 %   ");
                if (!Same(bl.ReadRange(Firmware.PadBase, Firmware.PadEnd - Firmware.PadBase), target, 0)) return Stuck();
                Ui.Say(T.Get("Written and read back: OK.", "Écrit et relu : conforme."));
                return Restart(bl, PadPid, PadPage, true);
            }
        }

        // ================================================================ dongle

        public static bool Dongle(bool restore)
        {
            Ui.Title(restore ? T.Get("Restore the dongle (factory firmware 1.00)", "Remettre le dongle d'origine (firmware d'usine 1.00)")
                             : T.Get("Patch the dongle", "Patcher le dongle"));

            HidInfo stuck = Hid.Enumerate().FirstOrDefault(h => h.Vid == App.Vid && h.Pid == Boot.Pid);
            if (stuck != null)
            {
                Ui.Say(T.Get("A device is already in update mode: resuming with the latest dongle backup.", "Un appareil est déjà en mode mise à jour : je reprends avec la dernière sauvegarde du dongle."));
                byte[] last = LatestBackup("dongle", DongleFlash);
                byte[] bank = last == null ? null : Slice(last, Firmware.DongleBase, Firmware.DongleEnd);
                byte[] factory = bank == null ? null : Firmware.Sha(bank) == Firmware.DongleFactorySha ? bank : Firmware.DongleUnpatch(bank);
                if (factory == null) { Ui.Say(T.Get("No usable dongle backup in the backups folder.", "Aucune sauvegarde du dongle utilisable dans le dossier backups.")); return false; }
                return DongleBoot(stuck, restore ? factory : Firmware.DonglePatch(factory), null);
            }

            Ui.Say(T.Get("Plug in the dongle and switch the controller off (or leave the controller on its USB cable).", "Branche le dongle et éteins la manette (ou laisse la manette sur son câble USB)."));
            var dongles = Ui.WaitDevices(() => App.Find(IdlePid, IdlePage).Where(p => App.Product(p.ident) == DongleProduct).ToList(), T.Get("dongle alone", "dongle seul"));
            if (dongles == null) return false;
            if (dongles.Count != 1) { Ui.Say(T.Get("Plug in only one 8BitDo dongle.", "Ne branche qu'un seul dongle 8BitDo.")); return false; }
            var dongle = dongles[0];
            int version = App.Version(dongle.ident);
            Ui.Say(T.Get($"Dongle found, firmware {App.V(version)}.", $"Dongle trouvé, firmware {App.V(version)}."));
            if (version != 100) { Ui.Say(T.Get("Only the factory firmware 1.00 of the dongle is supported: nothing is done.", "Seul le firmware d'usine 1.00 du dongle est pris en charge : rien n'est fait.")); return false; }

            byte[] backup;
            using (HidDevice dev = HidDevice.Open(dongle.info))
            {
                if (dev == null) { Ui.Say(T.Get("Cannot open the dongle.", "Impossible d'ouvrir le dongle.")); return false; }
                byte[] marker = App.Read(dev, Firmware.DongleMarkerAddr, 4);
                bool patched = marker != null && marker.SequenceEqual(Firmware.DongleMarker);
                if (!restore && patched) { Ui.Say(T.Get("This dongle is already patched. Nothing to do.", "Ce dongle est déjà patché. Rien à faire.")); return true; }
                if (restore && !patched) { Ui.Say(T.Get("This dongle is not patched. Nothing to do.", "Ce dongle n'est pas patché. Rien à faire.")); return true; }
                backup = Backup(dev, "dongle", DongleFlash, version);
                if (backup == null) return false;
            }
            byte[] current = Slice(backup, Firmware.DongleBase, Firmware.DongleEnd);
            byte[] target = restore ? Firmware.DongleUnpatch(current) : Firmware.DonglePatch(current);
            if (target == null)
            {
                Ui.Say(T.Get("This dongle's firmware is not the one the patch was made for (factory 1.00, SHA-256 checked): nothing is done.",
                             "Le firmware de ce dongle n'est pas celui pour lequel le patch a été fait (1.00 d'usine, SHA-256 vérifié) : rien n'est fait."));
                Ui.Log("dongle bank sha256 " + Firmware.Sha(current));
                return false;
            }
            if (Staged(Slice(backup, DongleRecord, DongleRecord + 0x24))) { Ui.Say(T.Get("An update is pending on this dongle: nothing is done.", "Une mise à jour est en attente sur ce dongle : rien n'est fait.")); return false; }

            if (!Ui.Confirm(restore ? T.Get("The dongle will get its factory firmware 1.00 back.", "Le dongle va retrouver son firmware d'usine 1.00.")
                                    : T.Get("The dongle will get the battery patch. Its pairing and calibration are kept.", "Le dongle va recevoir le patch batterie. Son appairage et sa calibration sont gardés.")))
                return false;

            if (!EnterUpdateMode(dongle.info)) return false;
            HidInfo boot = App.WaitFor(h => h.Vid == App.Vid && h.Pid == Boot.Pid, 12000);
            if (boot == null) { Ui.Say(T.Get("The dongle did not enter update mode: nothing was written. Unplug it and try again.", "Le dongle n'est pas passé en mode mise à jour : rien n'a été écrit. Débranche-le et recommence.")); return false; }
            return DongleBoot(boot, target, backup);
        }

        static bool DongleBoot(HidInfo bootInfo, byte[] bank, byte[] backup)
        {
            if (bank == null || bank.Length != Firmware.DongleSize) { Ui.Say(T.Get("No valid firmware to write.", "Aucun firmware valide à écrire.")); return false; }
            if (bootInfo.OutputLen != 65 || bootInfo.InputLen != 65) { Ui.Say(T.Get("Unexpected update mode: nothing written.", "Mode mise à jour inattendu : rien n'est écrit.")); return false; }
            using (HidDevice dev = HidDevice.Open(bootInfo))
            {
                if (dev == null) { Ui.Say(T.Get("Cannot open the update mode.", "Impossible d'ouvrir le mode mise à jour.")); return false; }
                var bl = new Boot(dev, Firmware.DongleBase, Firmware.DongleEnd);
                int? who = bl.Identify();
                if (who != DongleProduct)
                {
                    Ui.Log($"update mode identity {who:x}");
                    if (backup != null) { Ui.Say(T.Get("This is not the dongle's update mode: nothing written.", "Ce n'est pas le mode mise à jour du dongle : rien n'est écrit.")); return Restart(bl, IdlePid, IdlePage, false); }
                    Ui.Say(T.Get("The device in update mode is not the dongle (the controller?): use the controller action.", "L'appareil en mode mise à jour n'est pas le dongle (la manette ?) : utilise l'action de la manette."));
                    return false;
                }
                if (backup != null)
                {
                    bool same = Same(bl.ReadRange(0, Firmware.DongleBase), backup, 0) && Same(bl.ReadRange(DongleCal, 0x40), backup, DongleCal);
                    byte[] rec = bl.ReadRange(DongleRecord, 0x24);
                    if (!same || rec == null || Staged(rec)) { Ui.Say(T.Get("The dongle does not match its backup: nothing written.", "Le dongle ne correspond pas à sa sauvegarde : rien n'est écrit.")); return Restart(bl, IdlePid, IdlePage, false); }
                    Ui.Say(T.Get("Update mode checked against the backup.", "Mode mise à jour vérifié par rapport à la sauvegarde."));
                }
                if (DryRun) { Ui.Say(T.Get("Dry run: nothing was written.", "Essai : rien n'a été écrit.")); return Restart(bl, IdlePid, IdlePage, true); }

                // Header (first 46 bytes) last: if anything stops before, the dongle stays in update mode and can be written again.
                Ui.Log("erase 0x6000-0x10000");
                if (!bl.Erase(Firmware.DongleBase, Firmware.DongleSize)) return Stuck();
                for (int a = Firmware.DongleBase + App.Chunk; a < Firmware.DongleEnd; a += App.Chunk)
                {
                    byte[] blk = bank.Skip(a - Firmware.DongleBase).Take(Math.Min(App.Chunk, Firmware.DongleEnd - a)).ToArray();
                    if (blk.All(x => x == 0xFF)) continue;
                    if (!bl.Write(a, Firmware.DongleSize, blk)) return Stuck();
                    if ((a - Firmware.DongleBase) % 0x1000 < App.Chunk) Console.Write($"\r  {T.Get("writing", "écriture")} {100 * (a - Firmware.DongleBase) / Firmware.DongleSize} %   ");
                }
                Console.WriteLine($"\r  {T.Get("writing", "écriture")} 100 %   ");
                if (!Same(bl.ReadRange(Firmware.DongleBase + App.Chunk, Firmware.DongleSize - App.Chunk), bank, App.Chunk)) return Stuck();
                if (!bl.Write(Firmware.DongleBase, Firmware.DongleSize, bank.Take(App.Chunk).ToArray())) return Stuck();
                if (!Same(bl.ReadRange(Firmware.DongleBase, Firmware.DongleSize), bank, 0)) return Stuck();
                Ui.Say(T.Get("Written and read back: OK.", "Écrit et relu : conforme."));
                return Restart(bl, IdlePid, IdlePage, true);
            }
        }

        // ================================================================ shared

        /// <summary>Full flash backup, read twice and compared, saved in backups\ next to the program.</summary>
        static byte[] Backup(HidDevice dev, string kind, int size, int version)
        {
            Ui.Say(T.Get($"Backing up the {kind} ({size / 1024} KB, read twice)…", $"Sauvegarde {(kind == "dongle" ? "du dongle" : "de la manette")} ({size / 1024} Ko, lue deux fois)…"));
            byte[] a = App.ReadRange(dev, 0, size, T.Get("read 1/2", "lecture 1/2"));
            byte[] b = a == null ? null : App.ReadRange(dev, 0, size, T.Get("read 2/2", "lecture 2/2"));
            if (a == null || b == null) { Ui.Say(T.Get("Reading stopped: nothing is done.", "Lecture interrompue : rien n'est fait.")); return null; }
            if (!a.SequenceEqual(b)) { Ui.Say(T.Get("The two reads differ: nothing is done.", "Les deux lectures diffèrent : rien n'est fait.")); return null; }
            string dir = Path.Combine(Ui.Folder, "backups");
            Directory.CreateDirectory(dir);
            string name = $"{kind}-{App.V(version)}-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
            File.WriteAllBytes(Path.Combine(dir, name), a);
            File.AppendAllText(Path.Combine(dir, "SHA256SUMS"), $"{Firmware.Sha(a)}  {name}\n");
            Ui.Say(T.Get($"Backup saved: backups\\{name} (keep it private: it holds your device's address and pairings).",
                         $"Sauvegarde enregistrée : backups\\{name} (garde-la pour toi : elle contient l'adresse et les appairages de ton appareil)."));
            return a;
        }

        static byte[] LatestBackup(string kind, int size)
        {
            string dir = Path.Combine(Ui.Folder, "backups");
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, kind + "-*.bin").OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(File.ReadAllBytes).FirstOrDefault(b => b.Length == size);
        }

        static bool EnterUpdateMode(HidInfo info)
        {
            using (HidDevice dev = HidDevice.Open(info))
            {
                if (dev == null) { Ui.Say(T.Get("Cannot open the device.", "Impossible d'ouvrir l'appareil.")); return false; }
                Ui.Say(T.Get("Entering update mode…", "Passage en mode mise à jour…"));
                var cmd = new byte[info.OutputLen];
                cmd[0] = 0x81; cmd[1] = 0x05; cmd[3] = 0x51; cmd[4] = 0x00;
                dev.Write(cmd);
                return true;
            }
        }

        static bool Restart(Boot bl, ushort pid, ushort page, bool ok)
        {
            Ui.Log("restart (command 7)");
            bl.Restart();
            HidInfo back = App.WaitFor(h => h.Vid == App.Vid && h.Pid == pid && h.UsagePage == page, 15000);
            if (back == null) { Ui.Say(T.Get("The device did not come back yet: unplug it and plug it in again.", "L'appareil n'est pas encore revenu : débranche-le et rebranche-le.")); return false; }
            using (HidDevice dev = HidDevice.Open(back))
            {
                byte[] ident = dev == null ? null : App.Identify(dev);
                if (ident != null) Ui.Say(T.Get($"Back online, firmware {App.V(App.Version(ident))}.", $"De retour, firmware {App.V(App.Version(ident))}."));
            }
            return ok;
        }

        static bool Stuck()
        {
            Console.WriteLine();
            Ui.Say(T.Get("Writing failed. The device stays in update mode, which is safe: run the same action again to finish.",
                         "L'écriture a échoué. L'appareil reste en mode mise à jour, sans danger : relance la même action pour terminer."));
            return false;
        }

        static byte[] Slice(byte[] b, int start, int end) => b.Skip(start).Take(end - start).ToArray();
        static bool Same(byte[] read, byte[] reference, int offset) => read != null && read.SequenceEqual(reference.Skip(offset).Take(read.Length));

        static bool Pending(byte[] record)
        {
            uint magic = BitConverter.ToUInt32(record, 0x18), length = BitConverter.ToUInt32(record, 0x20);
            return !(magic == 0 || magic == 0xFFFFFFFF) || !(length == 0 || length == 0xFFFFFFFF);
        }

        static bool Staged(byte[] r) => BitConverter.ToUInt32(r, 0x18) == 0xAEB14D6C && BitConverter.ToUInt32(r, 0x20) >= 1 && BitConverter.ToUInt32(r, 0x20) <= 0x3EFFF;
    }
}
