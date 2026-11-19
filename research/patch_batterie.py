"""Construit les firmwares modifiés « batterie sur 2,4 GHz » de la 8BitDo Ultimate 2C et de son dongle.

    python -I patch_batterie.py <officiel-1.09.dat> <sauvegarde-dongle-1.00.bin> <dossier de sortie>

Manette 1.09 (fonction d'envoi radio 0xdd78) : chaque paquet radio passe au format 22 octets que 8BitDo réserve à son
logiciel (type 0x8e), avec l'octet 18 = niveau de batterie (RAM 0x843415, mis à jour par la tâche batterie) et
l'octet 19 = 0xBA (signature). Le reste du paquet, sa somme de contrôle et son type sont inchangés.

Dongle 1.00 :
  - réception radio (code RAM, image 0x6c28) : garde les octets 18 et 19 de tout paquet de jeu, pas seulement des 0x8e,
    dans 0x842e47 et 0x842e1c (lus ailleurs uniquement par le rapport de configuration 02 66, mode 4) ;
  - réponse d'identification 81 05 00 21 01 (0xac0e) : une petite fonction en 0xc100 (flash vierge après l'image)
    recopie ces deux octets dans la réponse, octets 7 et 8 du tampon (octets 8 et 9 côté Windows, après l'ID 0x02).

Chaque modification vérifie les octets d'origine avant de les remplacer.
"""
import hashlib
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tc32dis import THUMB_TO_TC32, Image, show  # noqa: E402

PAD_PAYLOAD_SHA = "dfd443c77f831008aa453ee2c1c9cb6871c36f8947c548e61d4387fba0a9da70"
NOP = 0x46C0          # Thumb mov r8, r8
MAGIC = 0xBA


def tc32(*thumb_halfwords):
    return b"".join(struct.pack("<H", THUMB_TO_TC32[h >> 12] << 12 | h & 0xFFF) for h in thumb_halfwords)


def bl(at, target):
    off = target - (at + 4)
    assert -0x400000 <= off < 0x400000 and off % 2 == 0
    return tc32(0xF000 | (off >> 12) & 0x7FF, 0xF800 | (off & 0xFFF) >> 1)


def edit(img, addr, old, new, what):
    cur = bytes(img[addr:addr + len(old)])
    if cur != old:
        raise SystemExit(f"0x{addr:x} ({what}) : attendu {old.hex(' ')}, trouvé {cur.hex(' ')} — rien n'est produit")
    assert len(new) == len(old)
    img[addr:addr + len(new)] = new
    return (addr, old, new, what)


def build_pad(dat):
    payload = dat[0x1C:]
    if hashlib.sha256(payload).hexdigest() != PAD_PAYLOAD_SHA:
        raise SystemExit("ce n'est pas la 1.09 officielle")
    img = bytearray(b"\xff" * 0x6000) + bytearray(payload) + bytearray(b"\xff" * (0x19000 - 0x6000 - len(payload)))
    edits = [
        edit(img, 0xDD90, bytes.fromhex("03 c0"), tc32(NOP), "longueur : toujours 22 octets (au lieu de beq 0xdd9a)"),
        edit(img, 0xDDC8, bytes.fromhex("06 c0"), tc32(NOP), "octets 18-19 : toujours remplis (au lieu de beq 0xddd8)"),
        edit(img, 0xDE14, struct.pack("<I", 0x843487), struct.pack("<I", 0x843415), "littéral : niveau de batterie au lieu de la qualité du lien"),
        edit(img, 0xDDD2, bytes.fromhex("fd 97 e9 9b"), tc32(0x2000 | MAGIC, NOP), f"octet 19 : 0x{MAGIC:X} au lieu de bl 0xb5a8"),
    ]
    return img, edits


def build_dongle(backup):
    if len(backup) != 0x20000:
        raise SystemExit("sauvegarde du dongle incomplète")
    img = bytearray(backup)
    helper_at = 0xC100
    helper = tc32(
        0x2364,           # movs r3, #0x64          (comme l'original : version dans le mot de pile)
        0x9310,           # str  r3, [sp, #0x40]
        0xAA00,           # add  r2, sp, #0          (tampon de la réponse, sur la pile de l'appelant)
        0x4B03,           # ldr  r3, =0x842e47       (octet 18 du dernier paquet : batterie)
        0x781B,           # ldrb r3, [r3, #0]
        0x71D3,           # strb r3, [r2, #7]
        0x4B02,           # ldr  r3, =0x842e1c       (octet 19 : signature)
        0x781B,           # ldrb r3, [r3, #0]
        0x7213,           # strb r3, [r2, #8]
        0x4770,           # bx   lr
    ) + struct.pack("<II", 0x842E47, 0x842E1C)
    edits = [
        edit(img, 0x6C28, bytes.fromhex("05 c1"), tc32(NOP), "réception : octets 18-19 gardés pour tout paquet (au lieu de bne 0xc36)"),
        edit(img, 0xAC0E, bytes.fromhex("64 a3 10 33"), bl(0xAC0E, helper_at), "identification : appel de la fonction en 0xc100"),
        edit(img, helper_at, b"\xff" * len(helper), helper, "fonction : batterie et signature dans la réponse"),
    ]
    return img, edits


def report(name, img, edits, ram, out):
    print(f"\n===== {name} =====")
    image = Image(bytes(img), ram)
    for addr, old, new, what in edits:
        print(f"0x{addr:05x}  {old.hex(' '):>23} → {new.hex(' '):<23}  {what}")
    return image


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    dat, dongle_backup, out = open(sys.argv[1], "rb").read(), open(sys.argv[2], "rb").read(), sys.argv[3]
    os.makedirs(out, exist_ok=True)

    pad, pad_edits = build_pad(dat)
    image = report("manette 1.09 + batterie", pad, pad_edits, (0x6000, 0x8500), out)
    print("--- fonction d'envoi radio modifiée ---")
    show(image, 0xDD78, 0xDDF0)
    print(f"--- littéral 0xde14 : 0x{struct.unpack_from('<I', pad, 0xDE14)[0]:x}")
    app = bytes(pad[0x6000:0x19000])
    open(os.path.join(out, "manette-1.09-batterie.bin"), "wb").write(app)
    print("manette : zone 0x6000-0x19000 écrite, sha256", hashlib.sha256(app).hexdigest())

    dongle, dongle_edits = build_dongle(dongle_backup)
    image = report("dongle 1.00 + batterie", dongle, dongle_edits, (0x6000, 0x8300), out)
    print("--- réception radio modifiée ---")
    show(image, 0x6C14, 0x6C3C)
    print("--- identification modifiée ---")
    show(image, 0xABF8, 0xAC3C)
    print("--- fonction ajoutée ---")
    show(image, 0xC100, 0xC114)
    print("littéraux :", [hex(x) for x in struct.unpack_from("<II", dongle, 0xC114)])
    bank = bytes(dongle[0x6000:0x10000])
    open(os.path.join(out, "dongle-1.00-batterie.bin"), "wb").write(bank)
    print("dongle : banque 0x6000-0x10000 écrite, sha256", hashlib.sha256(bank).hexdigest())
