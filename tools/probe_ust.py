# -*- coding: utf-8 -*-
"""Ad-hoc probe: show plugin-side parsing of one UST around a lyric."""
import re
import sys
from pathlib import Path

path = Path(sys.argv[1])
raw = path.read_bytes()
try:
    text = raw.decode("utf-8")
except UnicodeDecodeError:
    text = raw.decode("cp932", errors="replace")
lines = text.splitlines()
want = sys.argv[2] if len(sys.argv) > 2 else "keepin"
for i, line in enumerate(lines):
    if want in line:
        print(f"--- line {i}: {line!r}")
        for j in range(max(0, i - 6), min(len(lines), i + 6)):
            print(f"{j:5d}: {lines[j]!r}")
        print()
