# 8BitDo Ultimate 2C — battery level over 2.4 GHz

**English** · [Français](#français)

On Windows, the 8BitDo Ultimate 2C Wireless shows **no battery level** when it is used with its 2.4 GHz dongle: the stock
firmware never sends it to the PC. This tool patches the controller and the dongle so that the dongle reports the
**real battery percentage**, and any app can read it.

- Small patch: 9 bytes on the controller, 34 bytes on the dongle. Everything else is 8BitDo's official firmware.
- One program, a menu, no install. It backs up your devices first and can put them back to the original firmware.
- No 8BitDo firmware is shipped here: the tool downloads the official one from 8BitDo's own update server and checks it.
- Apps that read it: [Jus](https://github.com/Ghermin/jus) (taskbar battery levels), which also offers to apply this patch
  itself in one click. For other apps, see [Reading the level](#reading-the-level).

> **Unofficial.** Not made or supported by 8BitDo. Writing firmware always carries a risk; the tool is careful (see
> [Safety](#safety)), but you use it at your own risk.

## What you need

- 8BitDo **Ultimate 2C Wireless** (USB ID `2DC8:310A`, the "WUKONG" model) and its 2.4 GHz dongle.
- Windows 10 or 11.
- A USB **data** cable for the controller.
- Supported firmware: controller **1.09 or older** (it gets 1.09 + the patch), dongle **1.00** (factory). The tool checks
  and refuses anything else.

## How to use

1. Download `8bitdo-2c-battery-patch.exe` from the [latest release](../../releases/latest) and put it in a folder of its own.
2. Double-click it. A menu opens:
   ```
   1  Status: what is connected, battery level
   2  Patch the controller     (USB cable)
   3  Patch the dongle         (dongle alone, controller off)
   4  Restore the controller   (official firmware 1.09)
   5  Restore the dongle       (factory firmware 1.00)
   ```
3. **Controller**: plug it in with the USB cable, choose `2`, type `YES`. A few minutes (most of it is the backup).
4. **Dongle**: plug the dongle in, switch the controller off, choose `3`, type `YES`. A minute or two.
5. Switch the controller on (2.4 GHz mode) and choose `1`: the battery level appears.

Windows may warn about an unknown program (SmartScreen): "More info" → "Run anyway". The source is here and the exe
is built by GitHub Actions from it.

The two patches are independent: a patched controller works with a stock dongle and the other way round (you just get
no level until both are patched). If you later update with 8BitDo's official software, the patch is removed: run the
tool again.

**Backups**: the `backups` folder next to the program holds a full copy of each device's memory, taken before any
write. Keep it: it is your way back. **Do not share it**: it contains your controller's Bluetooth address, pairings and
the dongle's serial number.

Command line, for scripts: `8bitdo-2c-battery-patch.exe status | patch-controller | patch-dongle | restore-controller | restore-dongle [--dry-run] [--yes] [--lang en|fr]`.
`--dry-run` goes all the way to update mode, checks everything, and restarts the device without writing.

## Safety

- Full backup first, read twice and compared. Nothing happens if the two reads differ.
- The firmware is checked by SHA-256 before and after patching: the patch only applies to the exact firmware it was made for.
- Only the application area is erased and written. The update mode (bootloader), settings, pairings, Bluetooth address and
  calibration are never touched, and the code refuses any write outside that area.
- Everything is read back after writing. If anything fails, the device stays in update mode, which is safe: run the same
  action again to finish.
- On the normal (non-update) channel, the tool only sends two read-only requests (identify, read flash) plus the
  "enter update mode" request right before writing.

Tested on one controller (factory 1.06 → 1.09 + patch) and one dongle (factory 1.00), Windows 11. Other units should
behave the same, and the SHA-256 checks stop the tool if they do not, but please open an issue with what you saw.

## Reading the level

With both devices patched and the controller connected to the dongle, the dongle answers the identify request with the
battery level:

- HID collection `2DC8:310A`, usage page `0xFF7A`, 64-byte reports.
- Send output report `81 05 00 21 01` (padded with zeros), read input report `02 22 …`.
- Bytes `[6..7]` = `1C 30` (dongle), **byte `[8]` = battery %**, **byte `[9]` = `0xBA`** (signature: no signature, no level).
  Indexes include the report ID `0x02`.

Ask twice in a row and accept the level only if both replies are signed: with a patched dongle and a stock
controller, byte 9 carries a checksum byte that can equal `0xBA` by chance. Asking every 20–60 s is plenty. Details, packet format and
disassembly notes: [docs/how-it-works.md](docs/how-it-works.md).

## Build

`pwsh -File build.ps1` (PowerShell 7 provides the C# compiler; target .NET Framework 4.8, included in Windows) →
`out\8bitdo-2c-battery-patch.exe`. The `research` folder has the Python scripts used to find the patch (TC32
disassembler, cross-references, patch builder).

## Credits

Update-mode protocol and flashing flow from [arithon-f/8bitdo-ultimate-2c-toolkit](https://github.com/arithon-f/8bitdo-ultimate-2c-toolkit),
whose safeguards this tool follows. 8BitDo is a trademark of its owner; this project is not affiliated with it.

---

## Français

Sous Windows, la 8BitDo Ultimate 2C Wireless n'affiche **aucun niveau de batterie** quand elle passe par son dongle
2,4 GHz : le firmware d'origine ne l'envoie jamais au PC. Cet outil modifie la manette et le dongle pour que le dongle
donne le **vrai pourcentage de batterie**, lisible par n'importe quelle appli.

- Petit patch : 9 octets sur la manette, 34 sur le dongle. Tout le reste est le firmware officiel de 8BitDo.
- Un seul programme, un menu, rien à installer. Il sauvegarde d'abord tes appareils et peut les remettre d'origine.
- Aucun firmware de 8BitDo n'est fourni ici : l'outil télécharge l'officiel sur le serveur de mise à jour de 8BitDo et le vérifie.
- Appli qui l'affiche : [Jus](https://github.com/Ghermin/jus) (batteries dans la barre des tâches), qui propose aussi
  d'installer ce patch en un clic.

> **Non officiel.** Ni fait ni soutenu par 8BitDo. Écrire un firmware comporte toujours un risque ; l'outil est prudent
> (voir [Safety](#safety)), mais c'est à tes risques.

### Ce qu'il faut

- Une 8BitDo **Ultimate 2C Wireless** (ID USB `2DC8:310A`, modèle « WUKONG ») et son dongle 2,4 GHz.
- Windows 10 ou 11, un câble USB qui transmet les données.
- Firmwares pris en charge : manette **1.09 ou plus ancienne** (elle reçoit la 1.09 + le patch), dongle **1.00** (d'usine).
  Tout le reste est refusé.

### Utilisation

1. Télécharge `8bitdo-2c-battery-patch.exe` dans la [dernière version](../../releases/latest) et mets-le dans un dossier à lui.
2. Double-clique dessus : un menu s'ouvre (en français si Windows est en français).
3. **Manette** : branche-la avec le câble USB, choisis `2`, tape `OUI`. Quelques minutes (surtout la sauvegarde).
4. **Dongle** : branche le dongle, éteins la manette, choisis `3`, tape `OUI`. Une à deux minutes.
5. Rallume la manette (mode 2,4 GHz) et choisis `1` : le niveau de batterie s'affiche.

Les deux patchs sont indépendants. Une mise à jour avec le logiciel officiel de 8BitDo retire le patch : relance l'outil.

**Sauvegardes** : le dossier `backups` à côté du programme contient une copie complète de la mémoire de chaque appareil,
faite avant toute écriture. Garde-le : c'est ton retour en arrière. **Ne le partage pas** : il contient l'adresse
Bluetooth de ta manette, ses appairages et le numéro de série du dongle.

Pour remettre d'origine : choix `4` (manette) et `5` (dongle).
