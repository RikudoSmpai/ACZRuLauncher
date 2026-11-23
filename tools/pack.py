#!/usr/bin/env python3
"""Вшивает пакет перевода (zip acz-ru-pack/1) в хвост exe лаунчера.

    python tools/pack.py --pack dist/pack/acz-ru-pack.zip --exe dist/raw/ACZRuLauncher.exe --exe-out dist/ACZRuLauncher.exe

Zip кладётся как есть, без перепаковки: exe + zip + 8 байт длины zip (LE) + b"ACZRPAY1".
Лаунчер читает его по смещению (RuPack.TryOpen), во временную папку ничего не распаковывает.
Если в exe уже есть пакет — он срезается и заменяется.
"""
import argparse, json, os, struct, sys, zipfile

MAGIC = b"ACZRPAY1"


for _s in (sys.stdout, sys.stderr):
    _s.reconfigure(encoding="utf-8", errors="replace")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pack", required=True, help="zip acz-ru-pack/1")
    ap.add_argument("--exe", required=True)
    ap.add_argument("--exe-out", help="куда записать (по умолчанию — поверх --exe)")
    a = ap.parse_args()

    z = zipfile.ZipFile(a.pack)
    m = json.loads(z.read("manifest.json"))
    if m.get("format") != "acz-ru-pack/1":
        raise SystemExit(f"{a.pack}: format {m.get('format')!r}, ждали acz-ru-pack/1")
    missing = [f["delta"] for ar in m["archives"].values() for f in ar["files"] if f["delta"] not in z.namelist()]
    if missing:
        raise SystemExit(f"{a.pack}: в zip нет {len(missing)} дельт, например {missing[0]}")
    z.close()

    data = open(a.exe, "rb").read()
    if data[-8:] == MAGIC:
        (plen,) = struct.unpack("<q", data[-16:-8])
        data = data[: len(data) - 16 - plen]
    pack = open(a.pack, "rb").read()
    out = a.exe_out or a.exe
    with open(out, "wb") as f:
        f.write(data)
        f.write(pack)
        f.write(struct.pack("<q", len(pack)) + MAGIC)
    n = sum(len(ar["files"]) for ar in m["archives"].values())
    print(f"exe: {out} {os.path.getsize(out)} bytes; pack {m['version']} ({n} files, {len(pack)} bytes)")


if __name__ == "__main__":
    main()
