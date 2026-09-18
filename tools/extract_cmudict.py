# Extracts the CMUdict that OpenUtau ships and writes a compact word->phoneme
# table for the AltSetter plugin's no-hint fallback.
#
# Input : the dict.txt entry inside OpenUtau's g2p-arpabet.zip
# Output: src/AltSetter/data/cmudict.txt  (word<TAB>space separated ARPAbet)
#
# Only the first pronunciation of each word is kept, and stress digits are
# removed, matching OpenUtau's ArpabetG2p prep rules (lowercase +
# RemoveTailDigits).
import io
import re
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
OUT = REPO / "src" / "AltSetter" / "data" / "cmudict.txt"

# The pack lives inside an OpenUtau source checkout, not in an installed copy:
# OpenUtau.Core/G2p/Data/g2p-arpabet.zip within the source tree. There is no
# sensible default, so the path is passed on the command line.
DEFAULT_PACK = None

# OpenUtau's ArpabetG2p declares exactly these phoneme symbols.
PHONEMES = {
    "aa", "ae", "ah", "ao", "aw", "ay", "b", "ch", "d", "dh", "eh", "er",
    "ey", "f", "g", "hh", "ih", "iy", "jh", "k", "l", "m", "n", "ng", "ow",
    "oy", "p", "r", "s", "sh", "t", "th", "uh", "uw", "v", "w", "y", "z",
    "zh",
}


def strip_tail_digits(text: str) -> str:
    return re.sub(r"\d+$", "", text)


def main():
    pack = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    if pack is None or not pack.is_file():
        raise SystemExit(
            f"g2p pack not found: {pack}\n"
            "Pass the path to g2p-arpabet.zip from an OpenUtau source checkout:\n"
            "  python tools/extract_cmudict.py "
            "<checkout>\\OpenUtau.Core\\G2p\\Data\\g2p-arpabet.zip")
    with zipfile.ZipFile(pack) as zf:
        raw = zf.read("dict.txt").decode("utf-8", "replace")

    entries = {}
    skipped = 0
    for line in raw.splitlines():
        if line.startswith(";;;") or not line.strip():
            continue
        parts = line.split("  ")
        if len(parts) != 2:
            skipped += 1
            continue
        word = parts[0].strip()
        if word.endswith(")") or "(" in word:
            # Alternative pronunciation marker, e.g. READ(1)
            skipped += 1
            continue
        phones = [strip_tail_digits(p.lower()) for p in parts[1].split()]
        if not phones or any(p not in PHONEMES for p in phones):
            skipped += 1
            continue
        key = word.lower()
        if key in entries:
            skipped += 1
            continue
        entries[key] = phones

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", encoding="utf-8", newline="\n") as fh:
        for word in sorted(entries):
            fh.write(f"{word}\t{' '.join(entries[word])}\n")

    print(f"wrote {OUT} ({OUT.stat().st_size:,} bytes, {len(entries):,} words, "
          f"{skipped:,} skipped)")


if __name__ == "__main__":
    main()
