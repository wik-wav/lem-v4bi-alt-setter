# Alt Setter — pick the voicebank's alternate takes from OTO context

OpenUtau chooses a recording by appending the **Alt** number to the alias it looks
up: `d ih` becomes `d ih3`. The Lem V4Bi banks were recorded from a list with
duplicate diphone combinations, and those duplicates are not identical — each one
was sung next to different neighbours. This tool recovers that context from
`oto.ini` and sets the matching Alt number, using the same evidence the FestVox
tooling in `festvox-speech-gui` uses.

**It sets the alt expression and nothing else.** Lyrics, timing, phonemes and
every other expression are left alone, and the whole change is one undo step.

---

## Install and use

```powershell
.\build_plugin.ps1 -Install
```

Then **restart OpenUtau**. It appears in the piano roll as

```
Piano Roll → Notes → External → Alt Setter: set alts from voicebank context
```

Select some notes (or none, for the whole part) and run it. The Alt sliders in the
piano roll fill in, and `Ctrl+Z` puts everything back.

That is the whole workflow. It is a batch edit rather than a command line tool,
which is what makes it usable for real projects:

* **Each track uses its own singer's voicebank.** The plugin reads the bank from
  the track the part belongs to, so a project with `Lem_V4Bi_Civet` on one track
  and `Lem_V4Bi_Phascogale` on another gets the right takes for each, with no
  arguments to pass.
* **Nothing to keep in sync.** No file to point at, no project to close, no
  command to remember. If you change lyrics, run it again.
* **Undoable and visible.** Every value goes through OpenUtau's own expression
  commands, so it behaves like any other batch edit.
* **It will not guess when it is unsure.** If a note's phoneme list (as OpenUtau
  computed it) disagrees with the plugin's own derivation, the plugin lines the
  two readings up and places the values on OpenUtau's phones. Only when the two
  readings are too far apart to match — most phones have no counterpart — is the
  note skipped and the count reported, rather than putting a value on the wrong
  phone.

A message reports what happened, including when there was nothing to change.

### Checking it worked

Look at a note's **Alt** expression: a transition the bank recorded more than
once should show a number such as 7, and the phoneme list should show the mapped
alias. A note whose transitions all sounded best on the bank's own base recording
gets no expression, which is a normal answer rather than a failure.

---

## What it does

For every transition `A B` in the part, the tool:

1. **Recovers the recording context.** Every `oto.ini` row names a transition
   (`ay b`, `ay b1`, `ay b11`, …). Inside one recording the rows are in time
   order, so the row before `ay b` says which phone was sung before it and the
   row after says which phone followed. That is the only evidence used — WAV file
   names are never consulted.

   ```
   b_rr_ch_rr_d_rr_dh_rr_f_rr_g_rr.wav=b rrE3,…     <- before  "rr ch"
   b_rr_ch_rr_d_rr_dh_rr_f_rr_g_rr.wav=rr chE3,…    <- this take
   b_rr_ch_rr_d_rr_dh_rr_f_rr_g_rr.wav=ch rrE3,…    <- after
   ```

   So `rr ch` is known to have been recorded between `b` and `rr`.

2. **Reads the lyrics the way the bank's own phonemizer does.** An ArpasingPlus
   bank ships a complete Arpasing dictionary (`arpasing.yaml`) beside its OTO
   files, and OpenUtau phonemizes with that. The plugin reads the same file, so
   its reading of a word and OpenUtau's agree — the built-in CMU dictionary is
   only the fallback for a bank that has no dictionary of its own. Before OpenUtau
   has phonemized a part the note list is empty anyway, and the plugin still uses
   its own reading to decide.

   When the two still disagree — a word the bank's dictionary spells differently,
   a numbered pronunciation variant, a lyric written with a hint — the counts
   differ while almost every phone still lines up. The reading is then aligned to
   OpenUtau's list so the values land on the right phones, and the note is only
   refused when more than half of its phones have no counterpart, or when most of
   the pairs that did line up are merely the same *kind* of phone rather than the
   same phone. That means the two are not reading the same thing at all, and a
   value would land on the wrong phone.

3. **Scores each take against the song.** The phones around the transition —
   including the first phone of the next note — are compared with the recorded
   context:

   | Match | Points |
   | --- | --- |
   | same phone | +6 (left), +7 (right) |
   | same articulatory class | +4 |
   | different | −8 |
   | nothing recorded (`*`) | 0 |
   | light/dark `l` agrees | +20 |
   | light/dark `l` disagrees | −100 |

   The highest total wins and ties keep the bank's base take, so a take is only
   chosen when there is positive evidence for it.

4. **Handles voiced sibilants carefully.** For transitions into `z`, `zh`, `dz`,
   `jh` the choice is restricted to takes whose recorded right context is a
   voiced continuant (vowel, nasal, liquid, glide or voiced fricative). A
   sibilant spliced in front of a stop clicks, so those takes are not offered at
   all; if every take is risky, the base take is kept.

5. **Stays honest about phrase edges.** A take is rejected when its only
   advantage is an exact match against a phrase-edge pause while it makes the
   other side worse.

6. **Only offers takes that every pitch folder has.** These banks are multi-pitch
   (10 folders including head voice) and OpenUtau resolves aliases through one
   merged map, so a take present in `3_E3` but not `H3_E4` could fail to load.
   Takes missing from any folder that records the diphone are dropped before
   selection — 1 to 4 takes out of roughly 7800 per bank.

### Where the alt numbers go

An alt number belongs to the alias it numbers, and OpenUtau names a phone's alias
`<phone before> <phone>` — `ay l` is the `l` that follows `ay`. So a take is placed
on the phone its transition **ends** at: the take chosen for `ay → l` goes on the
`l`, which is what turns `ay l` into `ay l1`. Putting it on the `ay` instead would
number `- ay`, an alias recorded for a different transition entirely, and the
number would usually not exist there at all.

That phone is often in the next note, because OpenUtau's phoneme list for a note
carries the phone before it and the phone after it. The plugin works from that
list and places each value on the entry that owns the alias, at the index OpenUtau
will look the value up by.

The first phone of a part is sung out of silence, so its alias is `- <phone>` and
the take placed on it is the one chosen for silence going into it.

---

## Repository layout

```
src/AltSetterPlugin/           the plugin you install
  ContextAltBatchEdit.cs       menu entry, per-track bank, decisions, writes
src/AltSetterPluginTest/       hosts the plugin outside OpenUtau for testing
src/AltSetter/                 the shared core, plus a command line tool
  BankReader.cs                oto.ini + character.yaml -> takes with context
  Selector.cs                  the scoring and choice rules
  PhoneAlign.cs                lines a reading up with OpenUtau's phone list
  PhrasePlan.cs                phone sequence and per-phoneme choices
  Pronouncing.cs               the bank's dictionary, or CMU as a fallback
  Program.cs, Ustx.cs          the command line tool
  AltApplier.cs                writes the choices into a .ustx
tools/
  extract_cmudict.py           rebuilds the bundled CMU dictionary
  test_plugin.py               bank reading and scoring, via the UST path
  test_unit.py                 the command line tool's file writing
  check_align.py               the phone alignment, on real window entries
  check_report.py              cross-check a run's report against its project
  bank_probe.py                read-only bank exploration
build_plugin.ps1               build the plugin      <- start here
run_plugin_tests.ps1           every plugin check
build.ps1 / run_tests.ps1      the command line tool
```

The other scripts in `tools/` (`repro_diff.py`, `repro_stale.py`,
`phone_tokens.py`, `probe_ust.py`, `compare_bank.py`, `clean_altsetter.py`) are
one-off debugging helpers kept from the sessions that fixed the alignment and
placement bugs. They point at a bank in `D:\UTAU\voice` by default.

## Command line tool

`dist\AltSetter\AltSetter.exe` does the same selection and writes it into a
`.ustx` as alt expressions, for when OpenUtau is not part of the workflow (batch
jobs, scripted edits). It needs `--bank`, because a bare `.ustx` does not say
which singer a track uses, and OpenUtau must be closed so it does not overwrite
the file.

```powershell
AltSetter.exe song.ustx --bank "D:\UTAU\voice\Lem_V4Bi_Civet" --dry-run
AltSetter.exe song.ustx --bank "…" --report choices.tsv
AltSetter.exe --mode inspect --bank "…" --phones "- aa r iy"
```

The plugin and the tool share the bank reading, scoring and phone derivation
code, so they cannot disagree about a transition.

The tool rewrites **one voice part** — the first one, which is the part whose
notes it reports — and replaces the `alt` values on that part's notes with the
ones it chose. It never writes lyrics or any other expression: `atk`, `dec`,
`vel` and anything else in a note's `phoneme_expressions` block are read back and
written out unchanged, and an `alt` the new plan does not choose is removed
rather than left behind. The other parts are not touched.

## Tests

```powershell
.\run_plugin_tests.ps1     # the plugin, against all three banks
.\run_tests.ps1            # the command line tool and the shared core
```

The plugin test compiles **the same source that ships** and checks, for each bank:

* OpenUtau would discover the type — a concrete `BatchEdit` with a parameterless
  constructor and a menu name,
* the voicebank resolves from the track's singer, whether the singer points at
  the bank or at one pitch folder inside it,
* decisions land on the right notes, every index inside that note's own phoneme
  count, and the lyrics are untouched,
* a phoneme mismatch is detected and that note skipped rather than guessed,
* a bank phonemizer that reads a word differently (a different phone count) still
  gets its values placed on the right phones, and only a reading that disagrees
  about the sounds themselves is refused,
* the phoneme lists OpenUtau really hands over are replayed verbatim — entries
  such as `ay l` and `l ah`, which name the phone before as well as the phone —
  and every value has to land on the alias it is a take of, with a take number the
  bank recorded for that exact alias. A number the alias never had would be
  silence, so this is checked rather than assumed,
* two tracks with two different banks each get their own bank's takes,
* re-running reaches the same answer, and a track with no usable singer is
  refused with a message.

The command line suite additionally checks that writing a `.ustx` changes nothing
but `alt`: note count, lyrics, positions, durations, tones, pitch, vibrato and all
project settings come through a real YAML parse identical, a second run is
byte-identical, and every chosen take resolves to a real `oto.ini` alias in every
pitch folder, matching an independent Python transcription of the reference
scorer (617/617, 612/612, 611/611 for the three banks).

It also runs the tool over a project that already carries alt values and other
expressions, in both styles OpenUtau writes them (entries spanning several lines,
and flow entries such as `- {index: 0, abbr: alt, value: 1}`), and checks that the
alts are replaced exactly, the other expressions survive, every part still parses,
and the parts that were not rewritten keep what they had.

```powershell
python tools\check_report.py report.tsv song.ustx --part 3
```

compares a run's report against the project it wrote: the chosen alts, the alts
the file holds, and the picks they came from.

## Notes and limits

* The plugin reads `oto.ini`; it never edits OTO files, audio, or your project
  file.
* **It sets the values it decides and does not clear anything else.** A value left
  by an earlier run, or one you set by hand in the expression editor, stays where
  it is. If you want a clean slate, undo the previous run first.
* Building the plugin needs the .NET 10 SDK, because OpenUtau 0.1.569 targets
  .NET 10.
* Only English Arpasing-style banks are supported; the phone derivation and the
  scoring classes are built for that inventory.
* The command line tool handles one bank at a time, since a `.ustx` alone does
  not record which singer a track uses. The plugin has no such limit.
* The bundled pronunciation dictionary in `src/AltSetter/data/cmudict.txt` is
  generated by `tools/extract_cmudict.py` from the copy of CMUdict inside
  OpenUtau's own `g2p-arpabet.zip`. It is committed so the build does not need an
  OpenUtau source checkout; CMUdict is distributed under its own permissive
  licence, copyright Carnegie Mellon University.
* The test suites read the banks from `D:\UTAU\voice\Lem_V4Bi_*` and two checks
  additionally want a real project to operate on. Those banks and projects are not
  part of this repository: point `ALTSETTER_USTX_DIR` at a directory holding
  `Headlock\headlock.ustx` and `Little Ripper Boy\Little Ripper Boy_edit.ustx` to
  run those checks, or the suites skip them and say so. A local
  `fixtures.local.ps1` (not committed — see `fixtures.local.ps1.example`) is
  sourced by the test scripts to set that variable.
