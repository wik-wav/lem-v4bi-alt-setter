# -*- coding: utf-8 -*-
"""A note that already carries an alt the new plan does not choose.

The stale entry has to go, and any non-alt expression has to stay.
"""
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
    if i == 0:
        lines += [
            "        phoneme_expressions:",
            "        - index: 0",
            "          abbr: alt",
            "          value: 9",
            "        - index: 5",
            "          abbr: alt",
            "          value: 9",
            "        - index: 0",
            "          abbr: atk",
            "          value: 100",
        ]
lines += ["wave_parts: []", ""]

path = repo / "build" / "dbg" / "stale.ustx"
path.write_text("\n".join(lines), encoding="utf-8")
subprocess.run([exe, str(path), "--bank", bank, "--quiet"], capture_output=True)
print(path.read_text(encoding="utf-8"))
