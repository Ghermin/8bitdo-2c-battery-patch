"""Accès mémoire par base + décalage : suit les constantes chargées dans les registres (ldr rX, =valeur ; adds rX, #n ;
movs rX, rY) le long du code, et signale les ldr/str dont l'adresse effective tombe dans les adresses cherchées.

    python -I xref.py image.bin <début> <fin> --ram-code 0x6000:0x8500 0x843415 0x843424 ...
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python -I n'ajoute pas le dossier du script
from tc32dis import Image, disassemble  # noqa: E402

MEM = re.compile(r"^(ldr|ldrb|ldrh|str|strb|strh|ldrsb|ldrsh) (r\d), \[(r\d), #0x([0-9a-f]+)\]$")
DEST = re.compile(r"^\w+s? (r\d)\b")


def scan(img, start, end, targets):
    regs, hits = {}, []
    for a, run, size, text, info in disassemble(img, start, end):
        m = MEM.match(text)
        if m:
            op, rt, rb, off = m.group(1), m.group(2), m.group(3), int(m.group(4), 16)
            if rb in regs:
                ea = regs[rb] + off
                width = 1 if op.endswith("b") else 2 if op.endswith("h") else 4
                if any(ea <= t < ea + width for t in targets):
                    hits.append((a, run, text, ea))
            if op.startswith("ldr"):
                regs.pop(rt, None)
            continue
        if "lit" in info and info["lit"][1] is not None and text.startswith("ldr"):
            regs[text.split()[1].rstrip(",")] = info["lit"][1]
            continue
        m = re.match(r"^adds (r\d), #0x([0-9a-f]+)$", text)
        if m and m.group(1) in regs:
            regs[m.group(1)] += int(m.group(2), 16)
            continue
        m = re.match(r"^adds (r\d), (r\d), #0$", text)
        if m:
            if m.group(2) in regs:
                regs[m.group(1)] = regs[m.group(2)]
            else:
                regs.pop(m.group(1), None)
            continue
        if text.startswith(("bl ", "b ", "bx ", "pop ")) or "branch" in info:
            regs.clear()
            continue
        m = DEST.match(text)
        if m and not text.startswith(("cmp", "tst", "cmn")):
            regs.pop(m.group(1), None)
    return hits


if __name__ == "__main__":
    data = open(sys.argv[1], "rb").read()
    start, end = int(sys.argv[2], 0), int(sys.argv[3], 0)
    args = sys.argv[4:]
    ram = None
    if "--ram-code" in args:
        i = args.index("--ram-code")
        lo, hi = args[i + 1].split(":")
        ram = (int(lo, 0), int(hi, 0))
        del args[i:i + 2]
    targets = [int(t, 0) for t in args]
    sys.stdout.reconfigure(encoding="utf-8")
    for a, run, text, ea in scan(Image(data, ram), start, end, targets):
        print(f"{a:06x}" + (f" ({run:05x})" if run != a else "        ") + f"  {text:34} → 0x{ea:x}")
