# -*- coding: utf-8 -*-
"""Build a clean copy of a project: no alt expressions, no phone aliases the
plugin rewrote.

The rescued file is re-serialized by PyYAML, so its formatting changes; what it
contains is the project with Alt Setter's own edits removed. Offset and timing
overrides are kept, since those are the user's.

    python tools/clean_altsetter.py song.ustx --bank BANK [--out CLEAN.ustx]
"""
import argparse
import re
from pathlib import Path

import yaml

parser = argparse.ArgumentParser()
parser.add_argument("project")
parser.add_argument("--bank", required=True)
parser.add_argument("--out", default=None)
args = parser.parse_args()

source = Path(args.project)
target = Path(args.out) if args.out else source.with_name(source.stem + "_clean.ustx")

# Every alias the bank records, so a phone name can be recognised as real.
aliases = set()
for oto in Path(args.bank).rglob("oto.ini"):
    for line in oto.read_text(encoding="utf-8", errors="replace").splitlines():
        if "=" not in line:
            continue
        aliases.add(line.split("=", 1)[1].split(",")[0].strip().replace(" ", ""))


def looks_compounded(name):
    """Whether a phone name is not an alias the bank records."""
    text = (name or "").replace(" ", "")
    if not text:
        return False
    return text not in aliases


document = yaml.safe_load(source.read_text(encoding="utf-8-sig"))
expressions = 0
dropped = 0
kept = 0
for part in document.get("voice_parts") or []:
    for note in part.get("notes") or []:
        if note.get("phoneme_expressions"):
            expressions += len(note["phoneme_expressions"])
            note["phoneme_expressions"] = []
        overrides = note.get("phoneme_overrides") or []
        clean = [o for o in overrides if not looks_compounded(o.get("phoneme"))]
        dropped += len(overrides) - len(clean)
        kept += len(clean)
        if "phoneme_overrides" in note or overrides:
            note["phoneme_overrides"] = clean

target.write_text(
    yaml.safe_dump(document, allow_unicode=True, sort_keys=False, width=10 ** 6),
    encoding="utf-8")
print(f"wrote {target}")
print(f"  removed {expressions} phoneme expression(s)")
print(f"  dropped {dropped} rewritten alias override(s), kept {kept}")
