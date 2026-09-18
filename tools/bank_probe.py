# -*- coding: utf-8 -*-
"""Read-only probe of the Lem V4Bi bank's oto.ini take structure.

Answers the questions the OpenUtau alt plugin depends on:
  1. How are take numbers encoded in aliases, per pitch folder?
  2. How many takes exist per diphone, and do the counts agree across pitches?
  3. What does the recovered outer context look like for competing takes?
"""
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

PITCH_TAG = re.compile(r"([A-G](?:#|b)?-?\d+)H?\Z")
DIGITS = re.compile(r"(\d+)\Z")
VOWELS = {
    "a", "aa", "ae", "ah", "ao", "aw", "ax", "ay", "e", "eh",
    "er", "ey", "i", "ih", "iy", "o", "ow", "oy", "u", "uh", "uw",
}


def parse_oto(path: Path):
    rows = []
    text = path.read_text(encoding="utf-8", errors="replace")
    for lineno, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line or line.startswith(("#", ";")) or "=" not in line:
            continue
        wav, rest = line.split("=", 1)
        parts = rest.split(",")
        if len(parts) < 6:
            continue
        try:
            vals = [float(v) for v in parts[1:6]]
        except ValueError:
            continue
        rows.append({
            "wav": wav.strip(), "alias": parts[0].strip(),
            "offset": vals[0], "consonant": vals[1], "cutoff": vals[2],
            "preutter": vals[3], "overlap": vals[4], "line": lineno,
        })
    return rows


def split_alias(alias: str):
    """alias -> (left, right, take, pitch) or None if not a two-token diphone."""
    tag = None
    m = PITCH_TAG.search(alias)
    body = alias
    if m:
        tag = m.group(1)
        body = alias[:m.start()]
    toks = body.split()
    if len(toks) != 2:
        return None
    left, right = toks
    take = 0
    d = DIGITS.search(right)
    if d and right[:d.start()]:
        take = int(d.group(1))
        right = right[:d.start()]
    return left, right, take, tag


def folder_pitch_tag(folder_name: str) -> str:
    tag = folder_name.split("_", 1)[-1]
    return tag.replace("Cis", "C#").replace("Fis", "F#")


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else r"D:\UTAU\voice\Lem_V4Bi_Civet")
    folders = sorted(p.parent for p in root.glob("*/oto.ini"))
    print(f"bank: {root}")
    print(f"pitch folders: {[f.name for f in folders]}\n")

    per_folder = {}
    for folder in folders:
        rows = parse_oto(folder / "oto.ini")
        dips = defaultdict(dict)       # (l,r) -> {take: alias}
        nonstandard = []
        for row in rows:
            parsed = split_alias(row["alias"])
            if not parsed:
                nonstandard.append(row["alias"])
                continue
            left, right, take, tag = parsed
            if tag != folder_pitch_tag(folder.name):
                nonstandard.append(row["alias"])
                continue
            dips[(left, right)][take] = row["alias"]
        per_folder[folder.name] = dips
        takes = Counter(len(v) for v in dips.values())
        multi = sum(1 for v in dips.values() if len(v) > 1)
        print(f"{folder.name:>8}: rows={len(rows):5d} dips={len(dips):5d} "
              f"multi-take={multi:5d}  take-count histogram={dict(sorted(takes.items()))}")
        if nonstandard:
            print(f"          non-standard aliases: {len(nonstandard)} e.g. {nonstandard[:6]}")

    # Disagreement across pitch folders
    print("\n--- take-count disagreement across pitch folders ---")
    keys = set()
    for dips in per_folder.values():
        keys |= set(dips)
    missing = Counter()
    disagree = 0
    examples = []
    for key in keys:
        sets = []
        for name, dips in per_folder.items():
            sets.append(frozenset(dips.get(key, {}).keys()))
        if any(not s for s in sets):
            missing[key] = sum(1 for s in sets if not s)
            continue
        if len(set(sets)) != 1:
            disagree += 1
            if len(examples) < 10:
                examples.append((key, [sorted(s) for s in sets]))
    print(f"diphones: {len(keys)}  missing-in-some-pitch: {len(missing)}  "
          f"take-set-disagreements: {disagree}")
    for ex in examples:
        print("   ", ex)

    # Context structure of multi-take diphones
    main_folder = "3_E3" if "3_E3" in per_folder else folders[0].name
    dips = per_folder[main_folder]
    rows = parse_oto(root / main_folder / "oto.ini")
    by_wav = defaultdict(list)
    for row in rows:
        parsed = split_alias(row["alias"])
        if not parsed:
            continue
        left, right, take, tag = parsed
        if tag != folder_pitch_tag(main_folder):
            continue
        by_wav[row["wav"]].append({**row, "l": left, "r": right, "take": take})
    for units in by_wav.values():
        units.sort(key=lambda u: (u["offset"] + u["preutter"]))
    for units in by_wav.values():
        for i, unit in enumerate(units):
            prev = next((u for u in reversed(units[:i])
                         if u["offset"] + u["preutter"] < unit["offset"] + unit["preutter"] - 0.001), None)
            nxt = next((u for u in units[i + 1:]
                        if u["offset"] + u["preutter"] > unit["offset"] + unit["preutter"] + 0.001), None)
            unit["lc"] = prev["l"] if (prev and prev["r"] == unit["l"]) else (prev["r"] if prev else "*")
            unit["rc"] = nxt["r"] if (nxt and nxt["l"] == unit["r"]) else (nxt["l"] if nxt else "*")

    print(f"\n--- context of competing takes in {main_folder} ---")
    shown = 0
    for (l, r), takes in sorted(dips.items()):
        if len(takes) < 3 or shown >= 12:
            continue
        entries = [u for u in rows_units(by_wav, l, r)]
        if not entries:
            continue
        shown += 1
        print(f"  '{l} {r}' takes={sorted(takes)}")
        for u in entries:
            print(f"      take {u['take']}: left_ctx={u['lc']!r:>8} right_ctx={u['rc']!r:>8} "
                  f"wav={u['wav'][:40]}")

    # Percent of multi-take diphones where at least one context actually differs
    differing = 0
    total = 0
    for (l, r), takes in dips.items():
        if len(takes) < 2:
            continue
        entries = rows_units(by_wav, l, r)
        total += 1
        ctxs = {(u["lc"], u["rc"]) for u in entries}
        if len(ctxs) > 1:
            differing += 1
    print(f"\nmulti-take diphones: {total}, with differing outer context: {differing} "
          f"({100.0 * differing / max(1, total):.1f}%)")


def rows_units(by_wav, l, r):
    out = []
    for units in by_wav.values():
        for u in units:
            if u["l"] == l and u["r"] == r:
                out.append(u)
    return sorted(out, key=lambda u: u["take"])


if __name__ == "__main__":
    main()
