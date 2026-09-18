# -*- coding: utf-8 -*-
"""Two-run repro that dumps both outputs and their diff."""
import difflib
import subprocess
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
exe = str(repo / "build" / "dbg" / "AltSetter.exe")
bank = r"D:\UTAU\voice\Lem_V4Bi_Civet"

lines = [
    "name: repro", "comment: ''", 'ustx_version: "0.9"', "resolution: 480",
    "bpm: 120", "tempos:", "  - position: 0", "    bpm: 120",
    "tracks:", "- singer: Lem_V4Bi_Civet", "  track_name: main", "  track_no: 0",
    "voice_parts:", "- duration: 3840", "  name: main", "  track_no: 0",
    "  position: 0", "  notes:",
]
for i, lyric in enumerate(["the", "than", "ax", "than"]):
    lines += [
        f"      - position: {240 * i}", "        duration: 240", "        tone: 60",
        f"        lyric: {lyric}", "        pitch:", "          data:",
        "          - {x: -40, y: 0, shape: io}", "          snap_first: true",
        "        vibrato: {length: 0, period: 175, depth: 25}", "        tuning: 0",
    ]
lines += ["wave_parts: []", ""]

work = repo / "build" / "dbg"
path = work / "fresh3.ustx"
path.write_text("\n".join(lines), encoding="utf-8")

subprocess.run([exe, str(path), "--bank", bank, "--quiet"], capture_output=True)
(work / "fresh3.first.ustx").write_text(path.read_text(encoding="utf-8"), encoding="utf-8")
result = subprocess.run([exe, str(path), "--bank", bank, "--quiet"],
                        capture_output=True, text=True)
Path(work / "fresh3.second.stderr").write_text(result.stderr, encoding="utf-8")

first = (work / "fresh3.first.ustx").read_text(encoding="utf-8").splitlines()
second = path.read_text(encoding="utf-8").splitlines()
print("idempotent:", first == second)
for line in difflib.unified_diff(first, second, "first", "second", n=1, lineterm=""):
    print(line)
