# Research scripts

The Python scripts used to find and build the patch (comments in French). Python 3, no dependencies.

- `tc32dis.py image.bin <start> <end> [--ram-code 0x6000:0x8500]`: TC32 (Telink) disassembler.
- `xref.py image.bin <start> <end> --ram-code 0x6000:0x8500 0x843415 …`: finds loads/stores reaching given RAM addresses.
- `patch_batterie.py <official-1.09.dat> <dongle-backup.bin> <out dir>`: builds both patched images and prints the
  disassembly around each edit. The program in `src/` applies the very same edits (and checks the same SHA-256).

No firmware file is included: use the official `.dat` from 8BitDo and a backup of your own dongle.
