"""Désassembleur TC32 (Telink) : chaque demi-mot est converti en Thumb-1 (quartet de poids fort permuté), puis décodé.

    python -I tc32dis.py image.bin <début> <fin> [--ram-code 0x6000:0x8500]

Adresses : celles de l'image (flash). Le code « RAM » (copié en RAM au démarrage et exécuté à 0x0-0x2500) est décodé avec
son adresse d'exécution pour que les appels et les littéraux tombent juste ; l'adresse d'image reste affichée.
"""
import struct
import sys

TC32_TO_THUMB = {0x0: 0x4, 0x1: 0x5, 0x2: 0x8, 0x3: 0x9, 0x4: 0x7, 0x5: 0x6, 0x6: 0xB, 0x7: 0xA,
                 0x8: 0xE, 0x9: 0xF, 0xA: 0x2, 0xB: 0x3, 0xC: 0xD, 0xD: 0xC, 0xE: 0x1, 0xF: 0x0}
THUMB_TO_TC32 = {v: k for k, v in TC32_TO_THUMB.items()}
COND = ["eq", "ne", "cs", "cc", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le", "al", "nv"]
ALU = ["and", "eor", "lsl", "lsr", "asr", "adc", "sbc", "ror", "tst", "neg", "cmp", "cmn", "orr", "mul", "bic", "mvn"]


def to_thumb(hw):
    return TC32_TO_THUMB[hw >> 12] << 12 | hw & 0xFFF


def to_tc32(hw):
    return THUMB_TO_TC32[hw >> 12] << 12 | hw & 0xFFF


def r(n):
    return {13: "sp", 14: "lr", 15: "pc"}.get(n, f"r{n}")


def rlist(bits, extra=None):
    regs = [r(i) for i in range(8) if bits >> i & 1]
    if extra:
        regs.append(extra)
    return "{" + ", ".join(regs) + "}"


class Image:
    """Une image flash complète (adresse 0 = début du fichier) et la zone de code RAM éventuelle."""

    def __init__(self, data, ram_code=None):
        self.data = data
        self.ram_code = ram_code      # (début image, fin image) exécutés à partir de 0

    def run_addr(self, a):
        if self.ram_code and self.ram_code[0] <= a < self.ram_code[1]:
            return a - self.ram_code[0]
        return a

    def image_addr(self, run):
        if self.ram_code and 0 <= run < self.ram_code[1] - self.ram_code[0]:
            return run + self.ram_code[0]
        return run

    def hw(self, a):
        return struct.unpack_from("<H", self.data, a)[0]

    def word(self, run):
        a = self.image_addr(run)
        if 0 <= a <= len(self.data) - 4:
            return struct.unpack_from("<I", self.data, a)[0]
        return None


def decode(img, a):
    """(taille, texte, infos) pour l'instruction à l'adresse d'image a. infos : 'call', 'branch', 'lit' (adresse, valeur)."""
    t = to_thumb(img.hw(a))
    pc = img.run_addr(a)
    info = {}
    top3, top5 = t >> 13, t >> 11
    if top5 == 0b00011:
        op = "sub" if t >> 9 & 1 else "add"
        rn, rd = t >> 3 & 7, t & 7
        val = f"#{t >> 6 & 7}" if t >> 10 & 1 else r(t >> 6 & 7)
        return 2, f"{op}s {r(rd)}, {r(rn)}, {val}", info
    if top3 == 0b000:
        op = ["lsls", "lsrs", "asrs"][t >> 11 & 3]
        return 2, f"{op} {r(t & 7)}, {r(t >> 3 & 7)}, #{t >> 6 & 31}", info
    if top3 == 0b001:
        op = ["movs", "cmp", "adds", "subs"][t >> 11 & 3]
        return 2, f"{op} {r(t >> 8 & 7)}, #0x{t & 0xFF:x}", info
    if t >> 10 == 0b010000:
        return 2, f"{ALU[t >> 6 & 15]}s {r(t & 7)}, {r(t >> 3 & 7)}", info
    if t >> 10 == 0b010001:
        op = t >> 8 & 3
        rd = (t & 7) | (t >> 4 & 8)
        rs = t >> 3 & 15
        if op == 3:
            return 2, f"{'blx' if t >> 7 & 1 else 'bx'} {r(rs)}", info
        return 2, f"{['add', 'cmp', 'mov'][op]} {r(rd)}, {r(rs)}", info
    if top5 == 0b01001:
        rd = t >> 8 & 7
        lit = ((pc + 4) & ~3) + (t & 0xFF) * 4
        val = img.word(lit)
        info["lit"] = (lit, val)
        return 2, f"ldr {r(rd)}, =0x{val:x}" if val is not None else f"ldr {r(rd)}, [pc, #{(t & 0xFF) * 4}]", info
    if t >> 12 == 0b0101:
        ro, rb, rd = t >> 6 & 7, t >> 3 & 7, t & 7
        if t >> 9 & 1:
            op = ["strh", "ldrsb", "ldrh", "ldrsh"][t >> 10 & 3]
        else:
            op = ["str", "strb", "ldr", "ldrb"][t >> 10 & 3]
        return 2, f"{op} {r(rd)}, [{r(rb)}, {r(ro)}]", info
    if top3 == 0b011:
        b, load = t >> 12 & 1, t >> 11 & 1
        off = (t >> 6 & 31) * (1 if b else 4)
        op = ("ldr" if load else "str") + ("b" if b else "")
        return 2, f"{op} {r(t & 7)}, [{r(t >> 3 & 7)}, #0x{off:x}]", info
    if t >> 12 == 0b1000:
        op = "ldrh" if t >> 11 & 1 else "strh"
        return 2, f"{op} {r(t & 7)}, [{r(t >> 3 & 7)}, #0x{(t >> 6 & 31) * 2:x}]", info
    if t >> 12 == 0b1001:
        op = "ldr" if t >> 11 & 1 else "str"
        return 2, f"{op} {r(t >> 8 & 7)}, [sp, #0x{(t & 0xFF) * 4:x}]", info
    if t >> 12 == 0b1010:
        src = "sp" if t >> 11 & 1 else "pc"
        return 2, f"add {r(t >> 8 & 7)}, {src}, #0x{(t & 0xFF) * 4:x}", info
    if t >> 8 == 0b10110000:
        return 2, f"{'sub' if t >> 7 & 1 else 'add'} sp, #0x{(t & 0x7F) * 4:x}", info
    if t >> 12 == 0b1011 and t >> 9 & 3 == 0b10:
        if t >> 11 & 1:
            return 2, "pop " + rlist(t & 0xFF, "pc" if t >> 8 & 1 else None), info
        return 2, "push " + rlist(t & 0xFF, "lr" if t >> 8 & 1 else None), info
    if t >> 12 == 0b1100:
        op = "ldmia" if t >> 11 & 1 else "stmia"
        return 2, f"{op} {r(t >> 8 & 7)}!, {rlist(t & 0xFF)}", info
    if t >> 12 == 0b1101:
        cond = t >> 8 & 15
        if cond == 15:
            return 2, f"swi #0x{t & 0xFF:x}", info
        off = (t & 0xFF) - (0x100 if t & 0x80 else 0)
        dest = pc + 4 + off * 2
        info["branch"] = dest
        return 2, f"b{COND[cond]} 0x{dest:x}", info
    if top5 == 0b11100:
        off = (t & 0x7FF) - (0x800 if t & 0x400 else 0)
        dest = pc + 4 + off * 2
        info["branch"] = dest
        return 2, f"b 0x{dest:x}", info
    if top5 == 0b11110 and a + 2 < len(img.data):
        t2 = to_thumb(img.hw(a + 2))
        if t2 >> 11 == 0b11111:
            hi = (t & 0x7FF) - (0x800 if t & 0x400 else 0)
            dest = pc + 4 + (hi << 12) + ((t2 & 0x7FF) << 1)
            info["call"] = dest
            return 4, f"bl 0x{dest:x}", info
    return 2, f".hw 0x{img.hw(a):04x} (thumb 0x{t:04x})", info


def disassemble(img, start, end):
    out, a = [], start
    while a < end:
        size, text, info = decode(img, a)
        out.append((a, img.run_addr(a), size, text, info))
        a += size
    return out


def show(img, start, end, file=sys.stdout):
    for a, run, size, text, info in disassemble(img, start, end):
        raw = img.data[a:a + size].hex()
        where = f"{a:06x}" + (f" ({run:05x})" if run != a else "")
        file.write(f"{where:16} {raw:9} {text}\n")


if __name__ == "__main__":
    data = open(sys.argv[1], "rb").read()
    start, end = int(sys.argv[2], 0), int(sys.argv[3], 0)
    ram = None
    if "--ram-code" in sys.argv:
        lo, hi = sys.argv[sys.argv.index("--ram-code") + 1].split(":")
        ram = (int(lo, 0), int(hi, 0))
    sys.stdout.reconfigure(encoding="utf-8")
    show(Image(data, ram), start, end)
