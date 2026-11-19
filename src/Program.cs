using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("8BitDo 2C battery patch")]
[assembly: AssemblyProduct("8bitdo-2c-battery-patch")]
[assembly: AssemblyVersion("1.0.0")]

namespace BatteryPatch
{
    /// <summary>English or French, from the Windows display language (or --lang en|fr).</summary>
    static class T
    {
        public static bool French = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";
        public static string Get(string en, string fr) => French ? fr : en;
    }

    static class Ui
    {
        public static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string LogFile = Path.Combine(Folder, "8bitdo-2c-battery-patch.log");

        public static void Log(string s)
        {
            try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {s}\r\n"); } catch { }
        }

        public static void Say(string s) { Console.WriteLine("  " + s); Log(s); }

        public static void Title(string s)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  " + s);
            Console.WriteLine("  " + new string('─', s.Length));
            Console.ResetColor();
            Log("=== " + s + (Flasher.DryRun ? " (dry run)" : ""));
        }

        public static bool Confirm(string what)
        {
            Console.WriteLine();
            Say(what);
            if (Flasher.DryRun) { Say(T.Get("Dry run: the device will enter update mode and be checked, nothing will be written.", "Essai : l'appareil va passer en mode mise à jour et être vérifié, rien ne sera écrit.")); return true; }
            if (Flasher.AssumeYes) return true;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("  " + T.Get("Do not unplug anything during the write. Type YES to continue: ", "Ne débranche rien pendant l'écriture. Tape OUI pour continuer : "));
            Console.ResetColor();
            string answer = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
            bool ok = answer == "YES" || answer == "OUI";
            if (!ok) Say(T.Get("Cancelled: nothing was written.", "Annulé : rien n'a été écrit."));
            return ok;
        }

        /// <summary>Polls until at least one matching device shows up (Esc or 60 s to give up).</summary>
        public static List<T2> WaitDevices<T2>(Func<List<T2>> find, string what)
        {
            List<T2> found = find();
            if (found.Count > 0) return found;
            Console.Write("  " + T.Get($"Waiting for the {what}… (Esc to cancel)", $"En attente : {what}… (Échap pour annuler)"));
            int end = Environment.TickCount + 60000;
            while (Environment.TickCount - end < 0)
            {
                if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) break;
                Thread.Sleep(700);
                found = find();
                if (found.Count > 0) { Console.WriteLine(); return found; }
            }
            Console.WriteLine();
            Say(T.Get("Nothing found.", "Rien de trouvé."));
            return null;
        }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var list = args.Select(a => a.ToLowerInvariant()).ToList();
            int lang = list.IndexOf("--lang");
            if (lang >= 0 && lang + 1 < list.Count) T.French = list[lang + 1] == "fr";
            Flasher.DryRun = list.Contains("--dry-run");
            Flasher.AssumeYes = list.Contains("--yes");
            string command = list.FirstOrDefault(a => !a.StartsWith("--") && (lang < 0 || a != list.ElementAtOrDefault(lang + 1)));

            Header();
            if (command != null)
            {
                bool ok = Run(command);
                return ok ? 0 : 1;
            }

            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("  1  " + T.Get("Status: what is connected, battery level", "État : ce qui est branché, niveau de batterie"));
                Console.WriteLine("  2  " + T.Get("Patch the controller     (USB cable)", "Patcher la manette        (câble USB)"));
                Console.WriteLine("  3  " + T.Get("Patch the dongle         (dongle alone, controller off)", "Patcher le dongle         (dongle seul, manette éteinte)"));
                Console.WriteLine("  4  " + T.Get("Restore the controller   (official firmware 1.09)", "Remettre la manette       (firmware officiel 1.09)"));
                Console.WriteLine("  5  " + T.Get("Restore the dongle       (factory firmware 1.00)", "Remettre le dongle        (firmware d'usine 1.00)"));
                Console.WriteLine("  0  " + T.Get("Quit", "Quitter"));
                Console.Write("\n  > ");
                string choice = (Console.ReadLine() ?? "0").Trim();
                string cmd = choice == "1" ? "status" : choice == "2" ? "patch-controller" : choice == "3" ? "patch-dongle"
                           : choice == "4" ? "restore-controller" : choice == "5" ? "restore-dongle" : null;
                if (cmd == null) return 0;
                Run(cmd);
            }
        }

        static void Header()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"\n  8BitDo Ultimate 2C — {T.Get("battery level over 2.4 GHz", "niveau de batterie en 2,4 GHz")}   v{Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}");
            Console.ResetColor();
            Console.WriteLine("  " + T.Get("Unofficial tool, not made or supported by 8BitDo. Use at your own risk.", "Outil non officiel, ni fait ni soutenu par 8BitDo. À tes risques."));
            if (Flasher.DryRun) Console.WriteLine("  " + T.Get("Dry run: nothing will be written.", "Mode essai : rien ne sera écrit."));
        }

        static bool Run(string command)
        {
            try
            {
                switch (command)
                {
                    case "status": return Status();
                    case "patch-controller": return Flasher.Controller(false);
                    case "patch-dongle": return Flasher.Dongle(false);
                    case "restore-controller": return Flasher.Controller(true);
                    case "restore-dongle": return Flasher.Dongle(true);
                    default:
                        Console.WriteLine("  usage: 8bitdo-2c-battery-patch [status|patch-controller|patch-dongle|restore-controller|restore-dongle] [--dry-run] [--yes] [--lang en|fr]");
                        return false;
                }
            }
            catch (Exception e)
            {
                Ui.Say(T.Get("Error: ", "Erreur : ") + e.Message);
                Ui.Log(e.ToString());
                return false;
            }
        }

        static bool Status()
        {
            Ui.Title(T.Get("Status", "État"));
            bool any = false;
            foreach (var d in App.Find(0x310A, 0xFF7A).Concat(App.Find(0x301C, 0xFFA0)))
            {
                any = true;
                int product = App.Product(d.ident);
                string version = d.ident == null ? "?" : App.V(App.Version(d.ident));
                using (HidDevice dev = HidDevice.Open(d.info))
                {
                    if (product == 0x301B)
                    {
                        byte[] m = dev == null ? null : App.Read(dev, Firmware.PadMarkerAddr, 4);
                        string state = m == null ? "?" : m.SequenceEqual(Firmware.PadMarker) ? T.Get("patched", "patchée") : T.Get("not patched", "pas patchée");
                        Ui.Say(T.Get($"Controller on the USB cable: firmware {version}, {state}.", $"Manette par câble : firmware {version}, {state}."));
                    }
                    else if (product == 0x301C && d.info.Pid == 0x301C)
                    {
                        byte[] m = dev == null ? null : App.Read(dev, Firmware.DongleMarkerAddr, 4);
                        string state = m == null ? "?" : m.SequenceEqual(Firmware.DongleMarker) ? T.Get("patched", "patché") : T.Get("not patched", "pas patché");
                        Ui.Say(T.Get($"Dongle alone (controller off or on cable): firmware {version}, {state}.", $"Dongle seul (manette éteinte ou par câble) : firmware {version}, {state}."));
                    }
                    else if (product == 0x301C)
                    {
                        Ui.Say(T.Get($"Dongle with the controller connected: firmware {version}.", $"Dongle relié à la manette : firmware {version}."));
                        if (d.ident[9] == Firmware.Magic)
                            Ui.Say(T.Get($"Battery: {d.ident[8]} %", $"Batterie : {d.ident[8]} %"));
                        else
                            Ui.Say(T.Get("No battery level: the controller or the dongle is not patched (check each one with the controller on its cable, then the dongle alone).",
                                         "Pas de niveau de batterie : la manette ou le dongle n'est pas patché (vérifie la manette par câble, puis le dongle seul)."));
                    }
                    else Ui.Say($"2DC8:{d.info.Pid:X4} « {d.info.Product} » : {T.Get("not an Ultimate 2C", "ce n'est pas une Ultimate 2C")}");
                }
            }
            if (!any) Ui.Say(T.Get("No 8BitDo Ultimate 2C or dongle found.", "Aucune 8BitDo Ultimate 2C ni dongle trouvé."));
            return any;
        }
    }
}
