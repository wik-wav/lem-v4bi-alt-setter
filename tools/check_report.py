# -*- coding: utf-8 -*-
"""Compare the report's two sections against each other and the file.

    python tools/check_report.py REPORT.tsv FILE.ustx [--part N]

The report covers one voice part (the command line tool works on the first),
so the comparison is against that part's notes.
"""
import argparse
from pathlib import Path

import yaml


def sections(path):
    picks, said = [], []
    section = "picks"
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if line.startswith("# written"):
            section = "written"
            continue
        if line.startswith("#") or not line.strip():
            continue
        parts = line.split("\t")
        if section == "picks":
            if len(parts) == 6 and parts[0].isdigit():
                picks.append((int(parts[0]), int(parts[1]), int(parts[4])))
        elif len(parts) == 3 and parts[0].isdigit():
            said.append((int(parts[0]), int(parts[1]), int(parts[2])))
    return picks, said


parser = argparse.ArgumentParser()
parser.add_argument("report")
parser.add_argument("ustx")
parser.add_argument("--part", type=int, default=0)
args = parser.parse_args()

picks, said = sections(args.report)
notes = yaml.safe_load(
    Path(args.ustx).read_text(encoding="utf-8"))["voice_parts"][args.part]["notes"]
in_file = sorted((i, e["index"], e["value"])
                 for i, n in enumerate(notes)
                 for e in (n.get("phoneme_expressions") or [])
                 if e.get("abbr") == "alt")
print(f"picks  : {len(picks)}")
print(f"written: {len(said)}")
print(f"file   : {len(in_file)}")
print(f"picks == written: {sorted(picks) == sorted(said)}")
print(f"picks == file   : {sorted(picks) == sorted(in_file)}")
print(f"written == file : {sorted(said) == sorted(in_file)}")
for label, a, b in (("written not picked", said, picks),
                    ("picked not written", picks, said),
                    ("file not picked", in_file, picks)):
    diff = sorted(set(a) - set(b))
    print(f"{label}: {len(diff)} e.g. {diff[:3]}")
