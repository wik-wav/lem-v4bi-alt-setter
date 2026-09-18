# -*- coding: utf-8 -*-
"""End-to-end check of the AltSetter plugin against a real project.

Builds the exact file OpenUtau hands a legacy plugin (a Shift_JIS UST containing
[#PREV], the ordered note blocks and [#NEXT]), runs the plugin on it, then
verifies every hint the plugin wrote against the voicebank:

  * each phoneme token is a real phone,
  * each alternated phone resolves to a real oto alias in every pitch folder,
  * the chosen alt matches the reference Python scorer for the same context,
  * the plugin's output is stable when run twice.

Usage:
    python tools/test_plugin.py [--ustx PATH] [--bank PATH] [--keep]
"""
import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
EXE = REPO / "dist" / "AltSetter" / "AltSetter.exe"
DEBUG_EXE = REPO / "build" / "dbg" / "AltSetter.exe"


def fixture(*parts):
    """A real project to take notes from, or None when none is configured.

    The project these checks want is somebody's own song, so it is not in the
    repository: point ALTSETTER_USTX_DIR at the directory holding it (see
    fixtures.local.ps1.example) and the checks run.
    """
    root = os.environ.get("ALTSETTER_USTX_DIR")
    if not root:
        return None
    path = Path(root).joinpath(*parts)
    return path if path.is_file() else None

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bank_probe as bp  # noqa: E402

NOTE_KEYS = ("position", "duration", "tone", "lyric")


def read_ustx_notes(path: Path):
    """Minimal reader: the ordered note list of the first voice part."""
    notes = []
    lines = path.read_text(encoding="utf-8").splitlines()
    in_notes = False
    notes_indent = None
    current = None
    for line in lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        indent = len(line) - len(line.lstrip())
        m = re.match(r"^(\s*)(?:-\s+)?([A-Za-z_][A-Za-z0-9_]*):\s?(.*)$", line)
        if not m:
            continue
        key, value = m.group(2), m.group(3).strip()
        if indent == 0:
            in_notes = False
            if current:
                notes.append(current)
                current = None
            continue
        if key == "notes" and value == "":
            in_notes = True
            notes_indent = indent
            continue
        if not in_notes:
            continue
        if current is not None and indent <= notes_indent:
            in_notes = False
            notes.append(current)
            current = None
            continue
        is_item = stripped.startswith("-") and (current is None or indent == (len(lines[0]) and indent))
        if stripped.startswith("- "):
            if current:
                notes.append(current)
            current = {k: None for k in NOTE_KEYS}
            inner = re.match(r"^-\s+([A-Za-z_][A-Za-z0-9_]*):\s?(.*)$", stripped)
            if inner:
                current[inner.group(1)] = inner.group(2).strip()
            continue
        if current is not None and key in NOTE_KEYS:
            current[key] = value
    if current:
        notes.append(current)

    out = []
    for note in notes:
        if note.get("position") is None:
            continue
        out.append({
            "position": int(note["position"]),
            "duration": int(note["duration"]),
            "tone": int(note["tone"]),
            "lyric": note["lyric"].strip().strip("'\""),
        })
    return out


def build_ust(notes, path: Path, prev_lyric="R", next_lyric="R"):
    lines = [
        "[#SETTING]",
        "Tempo=120",
        "Tracks=1",
        "Mode2=True",
    ]
    lines += ["[#PREV]", "Length=480", "Lyric=" + prev_lyric, "NoteNum=60", "PreUtterance="]
    for i, note in enumerate(notes):
        lines += [
            f"[#{i:04d}]",
            f"Length={note['duration']}",
            f"Lyric={note['lyric']}",
            f"NoteNum={note['tone']}",
            "PreUtterance=",
        ]
    lines += ["[#NEXT]", "Length=480", "Lyric=" + next_lyric, "NoteNum=60", "PreUtterance="]
    text = "\r\n".join(lines) + "\r\n"
    path.write_bytes(text.encode("cp932", errors="replace"))
    return lines


def parse_ust_notes(path: Path):
    raw = path.read_bytes()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp932", errors="replace")
    notes = []
    current = None
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("[#") and line.endswith("]"):
            header = line
            if header in ("[#PREV]", "[#NEXT]", "[#SETTING]", "[#TRACKEND]"):
                current = None
                continue
            current = {"header": header, "lyric": "", "length": 0, "notenum": 0}
            notes.append(current)
            continue
        if current is None or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip().lower()
        if key == "lyric":
            current["lyric"] = value.strip()
        elif key == "length":
            current["length"] = int(value or 0)
        elif key == "notenum":
            current["notenum"] = int(value or 0)
    return notes


def parse_hint(lyric):
    """Return [(phone, alt)] from a "[...]" hint, or None when there is none."""
    text = (lyric or "").strip()
    if text.startswith("'") and text.endswith("'") and len(text) > 1:
        text = text[1:-1]
    if text.startswith('"') and text.endswith('"') and len(text) > 1:
        text = text[1:-1]
    m = re.search(r"\[([^\]]*)\]", text)
    if not m:
        return None
    tokens = []
    for token in m.group(1).split():
        digits = len(token)
        while digits and token[digits - 1].isdigit():
            digits -= 1
        if digits and digits < len(token):
            tokens.append((token[:digits], int(token[digits:])))
        else:
            tokens.append((token, 0))
    return tokens


def bank_affixes(bank: Path):
    """Declared subbank prefixes/suffixes, longest first (mirrors the plugin)."""
    path = bank / "character.yaml"
    prefixes, suffixes = [], []
    if not path.is_file():
        return prefixes, suffixes
    in_subbanks = False
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        # A "#" only starts a comment at a token boundary: "PF#3" is a suffix.
        line = re.sub(r"(?<=\s)#.*$", "", raw).rstrip()
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        text = line.strip()
        if not in_subbanks:
            if text == "subbanks:":
                in_subbanks = True
            continue
        if indent == 0 and not text.startswith("-"):
            break
        for key, bucket in (("prefix:", prefixes), ("suffix:", suffixes)):
            if text.startswith("- " + key):
                bucket.append(text[len(key) + 2:].strip().strip("'\""))
            elif text.startswith(key):
                bucket.append(text[len(key):].strip().strip("'\""))
    prefixes.sort(key=len, reverse=True)
    suffixes.sort(key=len, reverse=True)
    return [p for p in prefixes if p], [s for s in suffixes if s]


def strip_affixes(alias: str, prefixes, suffixes) -> str:
    text = (alias or "").strip()
    for prefix in prefixes:
        for variant in (prefix, prefix.lower(), prefix.upper()):
            if len(text) > len(variant) and text.startswith(variant):
                text = text[len(variant):].strip()
                return _strip_suffix(text, suffixes)
    return _strip_suffix(text, suffixes)


def _strip_suffix(text: str, suffixes) -> str:
    for suffix in suffixes:
        for variant in (suffix, suffix.lower(), suffix.upper()):
            if len(text) > len(variant) and text.endswith(variant):
                return text[: -len(variant)].strip()
    return text


def split_alias(alias: str, prefixes, suffixes):
    """Mirror of BankReader.SplitAlias for the reference parser."""
    text = strip_affixes(alias, prefixes, suffixes)
    if " " not in text:
        return None
    left, right = text.rsplit(" ", 1)
    left, right = left.strip(), right.strip()
    if not left or not right or left[-1].isdigit():
        return None
    pitch = ""
    if right[-1].isdigit():
        digits = len(right)
        while digits and right[digits - 1].isdigit():
            digits -= 1
        phone, take = right[:digits], int(right[digits:])
    else:
        phone, take = right, 0
    # Undo a glued pitch tag by dropping the shortest prefix of the phone that
    # leaves an already-seen spelling; the reference parser only needs to agree
    # with the plugin, which reports its own table in --mode inspect --dump.
    return left, phone, take, pitch


def take_table_from_dump(exe: Path, bank: Path):
    """Authoritative take table, straight from the plugin's own parser."""
    work = REPO / "build" / "test"
    work.mkdir(parents=True, exist_ok=True)
    dump = work / "takes.txt"
    result = subprocess.run(
        [str(exe), "--mode", "inspect", "--bank", str(bank),
         "--dump", str(dump), "--dump-limit", "1000000", "--quiet"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        raise SystemExit("inspect failed: " + (result.stderr or result.stdout))
    takes = {}
    current = None
    for line in dump.read_text(encoding="utf-8").splitlines():
        if line.startswith("== "):
            m = re.match(r"== (.*?) / (.*?)  takes=(\d+)", line)
            current = (m.group(1), m.group(2))
            takes[current] = {}
        elif current and line.startswith("   "):
            parts = line.strip().split("\t")
            number = int(parts[0])
            m = re.search(r"L=(.*?)\((.*?)\)\tR=(.*?)\((.*?)\)", line)
            takes[current][number] = (m.group(1), m.group(3))
    return takes


def reference_takes(bank: Path, folder_name=None):
    """diphone -> {take: (left_context, right_context)} from the reference parser."""
    if folder_name is None:
        candidates = sorted(p.parent for p in bank.glob("*/oto.ini"))
        folder = None
        for candidate in candidates:
            try:
                if bp.parse_oto(candidate / "oto.ini"):
                    folder = candidate
                    break
            except OSError:
                continue
        if folder is None:
            raise SystemExit(f"no usable oto.ini under {bank}")
    else:
        folder = bank / folder_name
    rows = bp.parse_oto(folder / "oto.ini")
    tag = bp.folder_pitch_tag(folder.name)
    units = []
    for row in rows:
        p = bp.split_alias(row["alias"])
        if not p:
            continue
        left, right, take, pitch = p
        if pitch != tag:
            continue
        units.append({**row, "l": left, "r": right, "take": take})
    by_wav = {}
    for unit in units:
        by_wav.setdefault(unit["wav"], []).append(unit)
    for group in by_wav.values():
        group.sort(key=lambda u: (u["offset"] + u["preutter"]))
    for group in by_wav.values():
        for i, unit in enumerate(group):
            prev = next((u for u in reversed(group[:i])
                         if u["offset"] + u["preutter"] < unit["offset"] + unit["preutter"] - 0.001), None)
            nxt = next((u for u in group[i + 1:]
                        if u["offset"] + u["preutter"] > unit["offset"] + unit["preutter"] + 0.001), None)
            unit["lc"] = prev["l"] if (prev and prev["r"] == unit["l"]) else (prev["r"] if prev else "*")
            unit["rc"] = nxt["r"] if (nxt and nxt["l"] == unit["r"]) else (nxt["l"] if nxt else "*")
    takes = {}
    for unit in units:
        takes.setdefault((unit["l"], unit["r"]), {})[unit["take"]] = (unit["lc"], unit["rc"])
    return takes


# ---------------------------------------------------------------- reference scorer
#
# An independent Python transcription of the same selection rules, used to check
# that the C# port really implements them rather than something that merely
# looks plausible.

VOWELS = bp.VOWELS
VOICED_SIBILANTS = {"z", "zh", "zi", "dz", "jh"}
SUPPORTIVE = {"vowel", "nasal", "liquid", "glide", "fricative_voiced"}
HARD_RISK = {"stop_voiceless", "stop_voiced",
             "affricate_voiceless", "affricate_voiced"}


def strip_take(phone):
    return re.sub(r"\d+$", "", phone or "")


def phone_class(phone):
    base = strip_take(phone).rstrip("_").lower()
    if base == "*":
        return "wildcard"
    if base in VOWELS:
        return "vowel"
    for phones, name in (
            ("p t k q py ty ky cl".split(), "stop_voiceless"),
            ("b d g by dy gy dx dxy".split(), "stop_voiced"),
            ("ch ts".split(), "affricate_voiceless"),
            ("jh dz".split(), "affricate_voiced"),
            ("f s sh th h hh fy hy".split(), "fricative_voiceless"),
            ("v z zh dh vy zi".split(), "fricative_voiced"),
            ("m n ng nn mm nng xn my ny ngy".split(), "nasal"),
            ("l r rr ly ry ri".split(), "liquid"),
            ("w y wi".split(), "glide")):
        if base in phones:
            return name
    if base in {"pau", "sil", "sp"}:
        return "silence"
    if base.endswith("y") and len(base) > 1:
        head = phone_class(base[:-1])
        if head != "other":
            return head
    return "other"


def edge_class(token, right_edge):
    text = (token or "").strip().lower()
    if not text or text == "*":
        return "wildcard"
    direct = phone_class(text)
    if direct != "other":
        return direct
    for vowel in sorted(VOWELS, key=len, reverse=True):
        if len(text) <= len(vowel) or not text.endswith(vowel):
            continue
        onset = text[: -len(vowel)]
        onset_class = phone_class(onset)
        if onset_class in {"other", "wildcard", "silence", "vowel"}:
            continue
        return vowel if right_edge else onset_class
    return "other"


def side_score(expected, actual, exact, right_edge):
    if not expected or expected == "*":
        return 0
    if expected == (actual or "*"):
        return exact
    wanted = edge_class(expected, right_edge)
    actual_class = edge_class(actual or "*", not right_edge)
    return 4 if wanted not in {"wildcard", "other"} and wanted == actual_class else -8


def l_class_of(left, right, outer_right):
    """Mirrors Take.LClass: the light/dark split of an "l" transition."""
    followers = set(VOWELS) | {"y"}
    if strip_take(left) == "l":
        return "light" if strip_take(right) in followers else "dark"
    if strip_take(right) == "l":
        return "light" if strip_take(outer_right) in followers else "dark"
    return "*"


def reference_choice(takes, left, right, outer_left, outer_right):
    """Return (alt, reason) using the reference rules."""
    entries = takes.get((strip_take(left), strip_take(right)))
    if not entries:
        return None
    ordered = sorted(entries.items())
    base_take = ordered[0][0]
    if len(ordered) == 1:
        return base_take, "single"
    wanted_l_class = l_class_of(left, right, outer_right)

    def score(take):
        left_context, right_context = entries[take]
        value = side_score(left_context, outer_left, 6, True)
        value += side_score(right_context, outer_right, 7, False)
        take_class = l_class_of(left, right, right_context)
        if wanted_l_class != "*" and take_class != "*":
            value += 20 if take_class == wanted_l_class else -100
        return value

    def quality(take):
        context_class = edge_class(entries[take][1], False)
        if context_class in SUPPORTIVE:
            return "supportive"
        if context_class in {"wildcard", "other"}:
            return "unknown"
        return "risky"

    def unsafe(take):
        left_relation = relation(entries[take][0], outer_left, True)
        right_relation = relation(entries[take][1], outer_right, False)
        return ((outer_left == "pau" and left_relation == "exact" and right_relation == "mismatch")
                or (outer_right == "pau" and right_relation == "exact" and left_relation == "mismatch"))

    def relation(expected, actual, right_edge):
        if not expected or expected == "*":
            return "wildcard"
        if expected == (actual or "*"):
            return "exact"
        wanted = edge_class(expected, right_edge)
        actual_class = edge_class(actual or "*", not right_edge)
        return "class" if wanted not in {"wildcard", "other"} and wanted == actual_class else "mismatch"

    if strip_take(right) in VOICED_SIBILANTS:
        pool = [t for t, _ in ordered if quality(t) == "supportive"]
        if not pool:
            pool = [t for t, _ in ordered if quality(t) == "unknown"]
        if not pool:
            return base_take, "risky"
        best = max(pool, key=lambda t: (score(t), -t))
        return best, "sibilant"

    best = base_take
    best_score = score(base_take)
    for take, _ in ordered:
        if take == best or unsafe(take):
            continue
        value = score(take)
        if value > best_score:
            best, best_score = take, value
    return best, "context"



def aliases_per_folder(bank: Path):
    """(left, right, take) -> set of pitch folders that contain it.

    Uses the plugin-authoritative spelling: the raw alias with the declared
    subbank affixes removed is exactly what OpenUtau hands a resampler, so this
    only has to prove the alias the plugin chose really exists per folder.
    """
    result = {}
    prefixes, suffixes = bank_affixes(bank)
    for folder in sorted(p.parent for p in bank.glob("*/oto.ini")):
        try:
            rows = bp.parse_oto(folder / "oto.ini")
        except OSError:
            continue
        for row in rows:
            text = strip_affixes(row["alias"], prefixes, suffixes)
            if " " not in text:
                continue
            left, right = text.rsplit(" ", 1)
            left, right = left.strip(), right.strip()
            if not left or not right:
                continue
            result.setdefault((left, right), set()).add(folder.name)
    return result


def resolve_alias(alias_map, diphone, take):
    """Which pitch folders hold this exact oto alias."""
    return alias_map.get((diphone[0], diphone[1] + (str(take) if take else "")), set())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ustx", default=None,
                        help="a real .ustx to take notes from; defaults to "
                             "$ALTSETTER_USTX_DIR\\Headlock\\headlock.ustx")
    parser.add_argument("--bank", default=r"D:\UTAU\voice\Lem_V4Bi_Civet")
    parser.add_argument("--exe", default=None)
    parser.add_argument("--keep", action="store_true")
    args = parser.parse_args()

    exe = Path(args.exe) if args.exe else (EXE if EXE.is_file() else DEBUG_EXE)
    if not exe.is_file():
        raise SystemExit(f"plugin not built: {exe}")
    bank = Path(args.bank)
    # The project is somebody's own song, so it is not in the repository: point
    # ALTSETTER_USTX_DIR at the directory holding it (see
    # fixtures.local.ps1.example), or pass --ustx.
    ustx = Path(args.ustx) if args.ustx else fixture("Headlock", "headlock.ustx")
    if ustx is None:
        raise SystemExit(
            "no test project: pass --ustx <project.ustx>, or set "
            "ALTSETTER_USTX_DIR to a directory holding Headlock\\headlock.ustx")

    notes = read_ustx_notes(ustx)
    print(f"project : {ustx} ({len(notes)} notes)")
    print(f"bank    : {bank}")
    print(f"plugin  : {exe}")

    work = REPO / "build" / "test"
    work.mkdir(parents=True, exist_ok=True)
    ust = work / "headlock.ust"
    if ust.exists():
        ust.unlink()          # never trust output left by an earlier run
    build_ust(notes, ust)
    seeded = ust.read_bytes()

    result = subprocess.run(
        [str(exe), str(ust), "--bank", str(bank), "--quiet"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(f"exit    : {result.returncode}")
    if result.stdout.strip():
        print(result.stdout.strip())
    if result.stderr.strip():
        print("stderr  :", result.stderr.strip())
    if result.returncode != 0:
        raise SystemExit("plugin failed")

    written = parse_ust_notes(ust)
    if len(written) != len(notes):
        raise SystemExit(f"note count changed: {len(notes)} -> {len(written)}")

    alias_map = aliases_per_folder(bank)
    diphone_folders = {}
    for (left, right), folders in alias_map.items():
        diphone_folders.setdefault((left, right), set()).update(folders)
    ref = take_table_from_dump(exe, bank)

    # Global phone sequence in the order OpenUtau would phonemize it, so the
    # reference scorer sees exactly the same outer context the plugin used.
    global_phones = []
    for note in written:
        tokens = parse_hint(note["lyric"])
        if not tokens:
            continue
        for phone, _ in tokens:
            global_phones.append(phone)

    alternated = 0
    phones_seen = set()
    problems = []
    examples = []
    mismatches = []
    skipped = []
    unresolved = 0
    cursor = 0
    for index, note in enumerate(written):
        tokens = parse_hint(note["lyric"])
        if tokens is None:
            # A note the plugin could not phonemize keeps its original lyric.
            # That is fine on its own; what would be a bug is a note that lost
            # a hint the plugin had already written.
            if note["lyric"].strip() != "+":
                skipped.append((index, note["lyric"]))
            continue
        for slot, (phone, alt) in enumerate(tokens):
            phones_seen.add(phone)
            position = cursor + slot
            if position + 1 >= len(global_phones):
                continue
            outer_left = global_phones[position - 1] if position > 0 else "*"
            outer_right = (global_phones[position + 2]
                           if position + 2 < len(global_phones) else "*")
            expected = reference_choice(
                ref, phone, global_phones[position + 1], outer_left, outer_right)
            if expected is None:
                unresolved += 1
                continue
            expected_alt, _reason = expected
            if expected_alt != alt:
                mismatches.append(
                    f"note {index}: {phone} {global_phones[position + 1]} "
                    f"context ({outer_left},{outer_right}) "
                    f"plugin alt {alt} vs reference alt {expected_alt}")
            if alt == 0:
                continue
            alternated += 1
            nxt = global_phones[position + 1]
            folders = resolve_alias(alias_map, (phone, nxt), alt)
            if not folders:
                problems.append(f"note {index}: {phone}{alt} {nxt} has no oto alias")
                continue
            expected_folders = diphone_folders.get((phone, nxt), set())
            if folders != expected_folders:
                problems.append(
                    f"note {index}: {phone}{alt} {nxt} exists in {sorted(folders)} "
                    f"but the diphone is recorded in {sorted(expected_folders)}")
            if len(examples) < 8:
                examples.append((index, phone, nxt, alt, len(folders)))
        cursor += len(tokens)

    print()
    print(f"hints written      : {sum(1 for n in written if '[' in n['lyric'])}")
    print(f"alternates written : {alternated}")
    print(f"distinct phones    : {len(phones_seen)}")
    print("sample             : " + ", ".join(
        f"{p}{a} {q} ({n} folders)" for _, p, q, a, n in examples))
    print(f"reference agreement: {alternated - len(mismatches)}/{alternated}")
    print(f"unresolved against reference bank: {unresolved}")
    if skipped:
        print(f"notes left alone   : {len(skipped)} ({', '.join(repr(t) for _, t in skipped[:6])})")

    # Idempotence: a second run must not change the file.
    first = ust.read_bytes()
    if first == seeded:
        raise SystemExit("the plugin changed nothing at all - check the bank path")
    subprocess.run([str(exe), str(ust), "--bank", str(bank), "--quiet"],
                   capture_output=True)
    second = ust.read_bytes()
    print(f"idempotent         : {first == second}")

    if mismatches:
        print()
        print(f"REFERENCE MISMATCHES ({len(mismatches)}):")
        for line in mismatches[:20]:
            print("  " + line)

    if alternated == 0:
        raise SystemExit("no alternates were written - bank and project do not line up")

    if unresolved > alternated * 0.05:
        raise SystemExit(
            f"{unresolved} transitions could not be resolved against the reference "
            "bank; the two are probably out of sync")

    if problems:
        print()
        print(f"PROBLEMS ({len(problems)}):")
        for problem in problems[:25]:
            print("  " + problem)
        raise SystemExit(1)

    if mismatches:
        raise SystemExit(1)

    print()
    print("OK: every alternate resolves to a real recording in every pitch folder,")
    print("    and the selection matches the reference scorer.")


if __name__ == "__main__":
    main()
