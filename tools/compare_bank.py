# -*- coding: utf-8 -*-
"""Compare the plugin's parsed take table with the reference Python parser.

Usage:
    python tools/compare_bank.py <dump.txt> [bank] [folder]
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bank_probe as bp  # noqa: E402


def read_dump(path):
    takes = {}
    current = None
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if line.startswith("== "):
            m = re.match(r"== (.*?) / (.*?)  takes=(\d+)", line)
            current = (m.group(1), m.group(2))
            takes[current] = []
        elif current and line.startswith("   "):
            parts = line.strip().split("\t")
            takes[current].append(int(parts[0]))
    return takes


def reference(folder):
    rows = bp.parse_oto(folder / "oto.ini")
    dips = defaultdict(set)
    tag = bp.folder_pitch_tag(folder.name)
    for row in rows:
        p = bp.split_alias(row["alias"])
        if not p:
            continue
        left, right, take, pitch = p
        if pitch != tag:
            continue
        dips[(left, right)].add(take)
    return dips


def main():
    dump = read_dump(sys.argv[1])
    bank = Path(sys.argv[2] if len(sys.argv) > 2 else r"D:\UTAU\voice\Lem_V4Bi_Civet")
    folder_name = sys.argv[3] if len(sys.argv) > 3 else "1_A2"
    ref = reference(bank / folder_name)

    print(f"dump keys: {len(dump)}   reference keys: {len(ref)}")
    print(f"dump multi-take: {sum(1 for v in dump.values() if len(v) > 1)}   "
          f"reference multi-take: {sum(1 for v in ref.values() if len(v) > 1)}")

    missing = [k for k in ref if k not in dump]
    extra = [k for k in dump if k not in ref]
    print(f"keys missing from dump: {len(missing)}  extra: {len(extra)}")
    for k in missing[:10]:
        print("   missing", k, sorted(ref[k]))
    for k in extra[:10]:
        print("   extra  ", k, sorted(dump[k]))

    disagree = []
    for k in dump:
        if k not in ref:
            continue
        expected = set(ref[k])
        if expected - set(dump[k]):
            disagree.append((k, sorted(dump[k]), sorted(ref[k]), "phases:"))
    print(f"keys where the plugin is missing a take: {len(disagree)}")
    for k, d, r, tag in disagree[:20]:
        print("   ", k, "plugin", d, "reference", r, tag)


if __name__ == "__main__":
    main()
