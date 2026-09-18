# -*- coding: utf-8 -*-
"""Check which phones the bank knows, to see how aliases can be split."""
from pathlib import Path

phones = set()
for f in Path(r"D:\UTAU\voice\Lem_V4Bi_Civet").glob("*/oto.ini"):
    for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
        if "=" not in line:
            continue
        alias = line.split("=", 1)[1].split(",")[0].strip()
        for part in alias.split(" "):
            phones.add(part)

print("count:", len(phones))
print("shortest:", sorted(phones, key=len)[:20])
print("longest:", sorted(phones, key=len, reverse=True)[:12])
for probe in ("p", "P", "w", "wP", "l", "lA", "ah", "ah4", "-", "ay"):
    print(f"  {probe!r} known: {probe in phones}")
