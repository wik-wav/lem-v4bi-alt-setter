# -*- coding: utf-8 -*-
"""Unit-level checks for the AltSetter plugin's UST handling.

These do not need a voicebank with alternates: they check the shapes that are
easy to get wrong, using a tiny synthetic bank with known takes.

    python tools/test_unit.py [--exe PATH]
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
EXE = REPO / "dist" / "AltSetter" / "AltSetter.exe"
DEBUG_EXE = REPO / "build" / "dbg" / "AltSetter.exe"

FAILURES = []


def fixture(*parts):
    """A real project to run the tool over, or None when none is configured.

    The projects these checks want are somebody's own songs, so they are not in
    the repository: point ALTSETTER_USTX_DIR at the directory holding them (see
    fixtures.local.ps1.example) and the checks run, otherwise they are skipped.
    """
    root = os.environ.get("ALTSETTER_USTX_DIR")
    if not root:
        return None
    path = Path(root).joinpath(*parts)
    return path if path.is_file() else None


def check(name, condition, detail=""):
    if condition:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def write(path: Path, text: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def make_bank(root: Path):
    """Two pitch folders with three takes of "ay b" whose recorded right
    contexts are z (base), r (take 1) and k (take 2).

    A take's right context is recovered from the neighbouring oto row of the
    same wav, so the wav names here are chosen to describe a chain of rows.
    """
    root.mkdir(parents=True, exist_ok=True)
    write(root / "character.yaml", """text_file_encoding: utf-8
subbanks:
- color: ""
  prefix: ""
  suffix: A3
  tone_ranges:
  - A3-B7
- color: ""
  prefix: ""
  suffix: A2
  tone_ranges:
  - C1-C3
""")
    for folder, tag, third in (("1_A3", "A3", True), ("2_A2", "A2", False)):
        rows = [
            # wav order defines the context: ay-b then b-z, so take "ay b"
            # really was recorded with z as its right context.
            f"ay_b_b_z.wav=ay b{tag},100,50,-100,40,20",
            f"ay_b_b_z.wav=b z{tag},400,50,-100,40,20",
            f"ay1_b_r_iy.wav=ay b1{tag},100,50,-100,40,20",
            f"ay1_b_r_iy.wav=b r{tag},400,50,-100,40,20",
            f"b_iy_d_ow.wav=b iy{tag},200,50,-100,40,20",
        ]
        if third:
            rows.append(f"ay2_b_k_iy.wav=ay b2{tag},100,50,-100,40,20")
            rows.append(f"ay2_b_k_iy.wav=b k{tag},400,50,-100,40,20")
        write(root / folder / "oto.ini", "\n".join(rows) + "\n")


def make_ust(path: Path, lyrics):
    lines = ["[#SETTING]", "Tempo=120", "Tracks=1", "Mode2=True",
             "[#PREV]", "Length=480", "Lyric=R", "NoteNum=60"]
    for i, lyric in enumerate(lyrics):
        lines += [f"[#{i:04d}]", "Length=480", f"Lyric={lyric}", "NoteNum=60"]
    lines += ["[#NEXT]", "Length=480", "Lyric=R", "NoteNum=60"]
    path.write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-8"))
    return lines


def read_lyrics(path: Path):
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("Lyric="):
            out.append(line[len("Lyric="):])
    return out


def run(exe, *args):
    return subprocess.run([str(exe), *map(str, args)],
                          capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


def alts_of(notes):
    """The (note, index, value) triples of every alt expression in a note list."""
    return sorted(
        (i, e["index"], e["value"])
        for i, n in enumerate(notes)
        for e in (n.get("phoneme_expressions") or []) if e.get("abbr") == "alt")


def read_report(path: Path):
    picks = []
    section = "picks"
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("# written"):
            section = "written"
            continue
        if line.startswith("#") or not line.strip() or section != "picks":
            continue
        parts = line.split("\t")
        if len(parts) != 6 or not parts[0].isdigit():
            continue
        picks.append({
            "note": int(parts[0]), "index": int(parts[1]),
            "left": parts[2], "right": parts[3], "alt": int(parts[4]),
            "context": parts[5],
        })
    return picks


def audit_ustx(ustx_path: Path, bank: Path, report: Path):
    """Cross-check the tool's choices, the project, and the voicebank.

    What must hold is that every choice the tool reports names a recording the
    bank really has, and that the project contains exactly those choices. The
    bank is the authority on the first, the project file on the second. A tool
    run rewrites one voice part, so the file is checked against that part.
    """
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import test_plugin as tp

    alias_map = tp.aliases_per_folder(bank)
    problems = []
    picks = read_report(report)
    if not picks:
        return ["the report listed no alternates"]

    # A phone's transition can reach into the next note, so when the report has
    # no right phone for a pick, the next note's first pick names it.
    def right_phone(pick):
        if pick["right"]:
            return pick["right"]
        following = [p for p in picks if p["note"] > pick["note"]]
        if not following:
            return ""
        return min(following, key=lambda p: p["note"])["left"]

    for pick in picks:
        right = right_phone(pick)
        if not right:
            problems.append(f"note {pick['note']}: alt with no following phone")
            continue
        if not tp.resolve_alias(alias_map, (pick["left"], right), pick["alt"]):
            problems.append(
                f"note {pick['note']}: {pick['left']}{pick['alt']} {right} "
                "is not in the bank")

    import yaml
    document = yaml.safe_load(ustx_path.read_text(encoding="utf-8"))
    expected = sorted((p["note"], p["index"], p["alt"]) for p in picks)
    # The tool works on one part, so find the part it rewrote and compare with
    # that one; the parts it did not touch are the caller's business.
    for part in document.get("voice_parts") or []:
        if alts_of(part.get("notes") or []) == expected:
            return problems
    in_file = alts_of((document.get("voice_parts") or [{}])[0].get("notes") or [])
    only_report = [x for x in expected if x not in in_file]
    only_file = [x for x in in_file if x not in expected]
    problems.append(
        f"{len(only_report)} choice(s) reported but not written, "
        f"{len(only_file)} written but not reported; "
        f"e.g. {only_report[:2]} vs {only_file[:2]}")

    by_note = {}
    for pick in picks:
        by_note.setdefault(pick["note"], []).append(pick["index"])
    for note, indexes in by_note.items():
        if len(indexes) != len(set(indexes)):
            problems.append(f"note {note}: the same index was chosen twice")
    return problems


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=None)
    parser.add_argument("--bank", default=None,
                        help="real bank used for the resolve check")
    args = parser.parse_args()
    exe = Path(args.exe) if args.exe else (EXE if EXE.is_file() else DEBUG_EXE)
    if not exe.is_file():
        raise SystemExit(f"plugin not built: {exe}")

    work = REPO / "build" / "unit"
    if work.exists():
        shutil.rmtree(work)
    bank = work / "bank"
    make_bank(bank)

    print(f"plugin: {exe}")
    print(f"bank  : {bank}")

    # 1. A "ay b" transition followed by "r" must pick the take recorded
    #    before "r", which is take 1. The number goes on the phone the alias ends
    #    at — "ay b1", the alias being "ay b" — not on the "ay" it starts from.
    ust = work / "hints.ust"
    make_ust(ust, ["ay [ay b]", "r [r b]"])
    result = run(exe, ust, "--bank", bank, "--quiet")
    check("hint mode exits cleanly", result.returncode == 0, result.stderr)
    lyrics = read_lyrics(ust)
    check("hint for 'ay b' got the take recorded before 'r'",
          lyrics[1].strip() == "ay [ay b1]", lyrics)

    # 2. A transition followed by "z" must keep the base take, which is the one
    #    recorded before "z".
    ust2 = work / "base.ust"
    make_ust(ust2, ["ay [ay b]", "z [z b]"])
    run(exe, ust2, "--bank", bank, "--quiet")
    check("base take stays unnumbered when its context matches",
          read_lyrics(ust2)[1].strip() == "ay [ay b]", read_lyrics(ust2))

    # 3. A note whose lyric has no hint is phonemized from the dictionary.
    ust3 = work / "words.ust"
    make_ust(ust3, ["read"])
    result = run(exe, ust3, "--bank", bank)
    check("plain English lyric produced hints", "[" in read_lyrics(ust3)[1],
          read_lyrics(ust3))

    # 4. Re-running is a no-op.
    before = ust3.read_bytes()
    run(exe, ust3, "--bank", bank, "--quiet")
    check("second run changes nothing", ust3.read_bytes() == before)

    # 5. "+" extender notes are left alone and do not shift the phone sequence.
    ust4 = work / "extender.ust"
    make_ust(ust4, ["ay [ay b]", "+", "iy [iy b]"])
    run(exe, ust4, "--bank", bank, "--quiet")
    check("extender note untouched", read_lyrics(ust4)[2] == "+", read_lyrics(ust4))

    # 6. A bank that is not a bank must fail loudly, not silently.
    empty = work / "notabank"
    empty.mkdir(parents=True, exist_ok=True)
    result = run(exe, ust4, "--bank", empty, "--quiet")
    check("missing oto.ini is reported", result.returncode != 0, result.stderr)

    # 7. The main job: writing alt expressions into a .ustx.
    ustx_src = fixture("Headlock", "headlock.ustx")
    if args.bank and Path(args.bank).is_dir():
        if ustx_src is None:
            print("  skip ustx checks (no test project; set ALTSETTER_USTX_DIR)")
        else:
            ustx = work / "project.ustx"
            shutil.copyfile(ustx_src, ustx)
            report = work / "project.report.tsv"

            result = run(exe, ustx, "--bank", args.bank, "--report", report)
            check("ustx mode exits cleanly", result.returncode == 0, result.stderr)
            text = ustx.read_text(encoding="utf-8")
            check("alt expressions were written", "abbr: alt" in text)

            # Only alt may be written, and only inside notes. The project's own
            # expression *definitions* also carry abbr:, so check the notes.
            import yaml
            before = yaml.safe_load(ustx_src.read_text(encoding="utf-8"))
            after = yaml.safe_load(text)
            # A tool run rewrites one voice part, so the checks below look at
            # the part the report describes: the first part it wrote into.
            picked = sorted((p["note"], p["index"], p["alt"])
                            for p in read_report(report))
            part_no = 0
            for i, part in enumerate(after["voice_parts"]):
                if alts_of(part["notes"]) == picked:
                    part_no = i
                    break
            notes_before = before["voice_parts"][part_no]["notes"]
            notes_after = after["voice_parts"][part_no]["notes"]
            written_abbrs = {e.get("abbr") for n in notes_after
                             for e in (n.get("phoneme_expressions") or [])}
            check("nothing but alt was written into notes",
                  written_abbrs == {"alt"}, str(written_abbrs))
            check("no note gained any other expression kind",
                  all(set(e.get("abbr") for e in (n.get("phoneme_expressions") or []))
                      <= {"alt"} for n in notes_after))
            check("note count unchanged", len(notes_before) == len(notes_after),
                  f"{len(notes_before)} -> {len(notes_after)}")
            kept = ("lyric", "position", "duration", "tone", "pitch", "vibrato",
                    "tuning")
            check("every untouched field survived",
                  all([n.get(k) for n in notes_before] == [n.get(k) for n in notes_after]
                      for k in kept),
                  "one of " + ", ".join(kept) + " differs")
            check("settings unchanged",
                  {k: v for k, v in before.items() if k != "voice_parts"} ==
                  {k: v for k, v in after.items() if k != "voice_parts"})

            expressions = [e for n in notes_after
                           for e in (n.get("phoneme_expressions") or [])]
            check("a useful number of expressions was written",
                  len(expressions) > 300, str(len(expressions)))

            # Every written alt must name a take that exists in the bank.
            bad = audit_ustx(ustx, Path(args.bank), report)
            check("every alt resolves to a real alias", not bad,
                  "; ".join(bad[:4]))

            # Running again must be a no-op.
            snapshot = ustx.read_bytes()
            run(exe, ustx, "--bank", args.bank, "--quiet")
            check("ustx mode is idempotent", ustx.read_bytes() == snapshot)

            # A dry run must not touch the file at all.
            dry = work / "dry.ustx"
            shutil.copyfile(ustx_src, dry)
            untouched = dry.read_bytes()
            result = run(exe, dry, "--bank", args.bank, "--dry-run")
            check("dry run leaves the file alone", dry.read_bytes() == untouched)
            check("dry run still reports what it would do",
                  "would be written" in result.stdout or "would be written" in result.stderr,
                  result.stdout[:200])

    # 8. A project that already carries alt values and other expressions, in the
    #    flow style OpenUtau also writes. Rewriting the alts must keep every
    #    other expression and must not leave a stale value behind.
    existing = fixture("Little Ripper Boy", "Little Ripper Boy_edit.ustx")
    if not (args.bank and Path(args.bank).is_dir() and existing):
        print("  skip existing-expressions checks (no test project)")
    else:
        ustx2 = work / "existing.ustx"
        shutil.copyfile(existing, ustx2)
        report2 = work / "existing.report.tsv"
        result = run(exe, ustx2, "--bank", args.bank, "--report", report2)
        check("a project with existing alts exits cleanly",
              result.returncode == 0, result.stderr)

        import yaml
        was = yaml.safe_load(existing.read_text(encoding="utf-8-sig"))
        now = yaml.safe_load(ustx2.read_text(encoding="utf-8"))
        reported = sorted((p["note"], p["index"], p["alt"])
                          for p in read_report(report2))
        check("every part still parses and keeps its notes",
              [len(p["notes"]) for p in was["voice_parts"]] ==
              [len(p["notes"]) for p in now["voice_parts"]],
              f"{[len(p['notes']) for p in was['voice_parts']]} -> "
              f"{[len(p['notes']) for p in now['voice_parts']]}")

        def others(parts):
            return sorted(
                (i, n, e.get("abbr"))
                for i, part in enumerate(parts)
                for n, note in enumerate(part["notes"])
                for e in (note.get("phoneme_expressions") or [])
                if e.get("abbr") != "alt")

        check("expressions other than alt were left alone",
              others(was["voice_parts"]) == others(now["voice_parts"]),
              "attack, decay or velocity entries changed")
        check("every voice kept exactly one block of alts",
              all(len(set(e["index"] for e in (n.get("phoneme_expressions") or [])
                          if e.get("abbr") == "alt")) ==
                  len([e for e in (n.get("phoneme_expressions") or [])
                       if e.get("abbr") == "alt"])
                  for part in now["voice_parts"] for n in part["notes"]),
              "a note carries two alts for the same phone")
        check("the parts left alone kept exactly what they had",
              all(alts_of(was["voice_parts"][i]["notes"]) ==
                  alts_of(now["voice_parts"][i]["notes"])
                  or alts_of(now["voice_parts"][i]["notes"]) == reported
                  for i in range(len(now["voice_parts"]))),
              "a part other than the rewritten one changed")
        bad = audit_ustx(ustx2, Path(args.bank), report2)
        check("every rewritten alt resolves to a real alias", not bad,
              "; ".join(bad[:4]))

        snapshot = ustx2.read_bytes()
        run(exe, ustx2, "--bank", args.bank, "--quiet")
        check("rewriting an already-written project is a no-op",
              ustx2.read_bytes() == snapshot)

    # 9. Handing the plugin a .ustx in UST mode must fail loudly rather than
    #    silently do nothing.
    wrong = work / "wrong.ust"
    wrong.write_text("name: not a ust\n", encoding="utf-8")
    result = run(exe, wrong, "--bank", bank, "--quiet")
    check("a non-UST file is rejected", result.returncode != 0, result.stderr)

    print()
    if FAILURES:
        print(f"{len(FAILURES)} check(s) failed: {', '.join(FAILURES)}")
        raise SystemExit(1)
    print("all unit checks passed")


if __name__ == "__main__":
    main()
