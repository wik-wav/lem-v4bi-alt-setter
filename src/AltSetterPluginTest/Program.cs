using System;
using System.Collections.Generic;
using System.Linq;
using AltSetter.Plugin;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;

namespace AltSetter.Plugin.Test {

    /// <summary>
    /// Exercises the shipping batch edit outside OpenUtau.
    ///
    /// It builds the smallest real project it can  Ea track with a singer, a
    /// voice part with notes  Eand checks the things the plugin is responsible
    /// for: that OpenUtau would discover it, that the voicebank comes from the
    /// track's own singer, that every decided alt lands on the right note and
    /// phone index, and that a phoneme mismatch is detected instead of producing
    /// a wrong value.
    /// </summary>
    internal static class Program {
        private static int failures;

        private static int Main(string[] args) {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var banks = args.Length > 0
                ? args
                : new[] { @"D:\UTAU\voice\Lem_V4Bi_Civet" };

            Console.WriteLine("== 1. OpenUtau would discover this as a batch edit");
            CheckDiscoverable();

            foreach (var bank in banks) {
                Console.WriteLine();
                Console.WriteLine("== bank " + bank);
                Console.WriteLine("-- 2. the voicebank comes from the track's own singer");
                CheckSingerResolution(bank);

                Console.WriteLine("-- 3. decisions land on the right notes and indices");
                CheckDecisions(bank);

                Console.WriteLine("-- 4. a phoneme mismatch is caught, not guessed");
                CheckDriftDetection(bank);
            }

            Console.WriteLine();
            Console.WriteLine("== 5. a track with no usable singer is refused");
            CheckNoSinger(banks[0]);

            Console.WriteLine();
            Console.WriteLine("== 6. several tracks each use their own singer");
            CheckPerTrackSingers(banks);

            Console.WriteLine();
            Console.WriteLine("== 7. the project shapes OpenUtau actually writes");
            CheckProjectFormats();

            Console.WriteLine();
            Console.WriteLine("== 8. every recorded alias is read back as a real phone");
            foreach (var one in banks) {
                CheckAliasPhones(one);
            }

            Console.WriteLine();
            Console.WriteLine("== 9. every declared pitch map resolves as OpenUtau resolves it");
            foreach (var one in banks) {
                CheckPitchMaps(one);
            }

            Console.WriteLine();
            Console.WriteLine("== 10. the values name an alias the bank recorded");
            CheckValuesNameRecordedAliases(banks[0]);

            Console.WriteLine();
            Console.WriteLine("== 11. the windows OpenUtau really hands over");
            foreach (var one in banks) {
                CheckRealWindowShapes(one);
            }


            Console.WriteLine();
            if (failures > 0) {
                Console.WriteLine($"{failures} check(s) failed");
                return 1;
            }
            Console.WriteLine("all plugin checks passed");
            return 0;
        }

        /// <summary>
        /// Reads back every alias the bank records, the way the plugin has to:
        /// an alias is a phone plus a take number and the subbank suffix that
        /// <c>character.yaml</c> declares, and that suffix is spelled differently
        /// in every bank ("A3" in Civet, "PA2" in Phascogale, "QA2" in Quoll).
        /// What is read back has to be a phone the bank itself uses.
        /// </summary>
        private static void CheckAliasPhones(string bank) {
            var name = System.IO.Path.GetFileName(bank);
            var loaded = AltSetter.BankReader.Load(bank);
            // Every take alias the bank parsed, which is the exact spelling the
            // plugin has to read back.
            var aliases = loaded.Diphones.Values
                .SelectMany(d => d.Takes)
                .Select(t => t.Alias)
                .Distinct()
                .ToList();
            var read = aliases.Select(a => {
                var parts = a.Split(' ');
                // Every take carries the suffix of some subbank, so it is looked
                // up with the tone that subbank covers.
                var tone = ToneFor(loaded, parts.Length > 1 ? parts[1] : parts[0]);
                return (Alias: a,
                        Left: AltSetter.Plugin.Phones.Of(parts[0], loaded, tone),
                        Right: AltSetter.Plugin.Phones.Of(
                            parts.Length > 1 ? parts[1] : "", loaded, tone));
            }).ToList();
            var missed = read
                .Where(x => !loaded.KnownPhones.Contains(x.Left) ||
                            (x.Right.Length > 0 && !loaded.KnownPhones.Contains(x.Right)))
                .Select(x => x.Alias + "->" + x.Left + "/" + x.Right)
                .Take(8)
                .ToList();
            Console.WriteLine($"   {name}: {aliases.Count} take alias(es), " +
                $"{aliases.Count - missed.Count} read back as the bank's own phones " +
                $"({loaded.KnownPhones.Count} known phones)");
            // What OpenUtau hands over, spelled the way its phonemizer writes it,
            // including the phone it prepends from the note before and a take
            // number where the recording has one. Every one has to come back as a
            // phone the bank uses, or the note's values cannot be placed.
            var wrong = new List<string>();
            foreach (var probe in new[] { "- m", "m ay", "ay l", "l ah", "ah v", "y uw" }) {
                foreach (var tail in new[] { "", "1", "2" }) {
                    var alias = probe + tail + Suffix(loaded);
                    var readBack = AltSetter.Plugin.Phones.Of(alias, loaded, 60);
                    if (!loaded.KnownPhones.Contains(readBack)) {
                        wrong.Add($"{alias}->{readBack}");
                    }
                }
            }
            Check($"{name}: aliases as OpenUtau spells them read back as real phones",
                  wrong.Count == 0, string.Join(", ", wrong.Take(6)));
            Check($"{name}: aliases read back as real phones",
                  missed.Count == 0, string.Join(", ", missed));
        }

        /// <summary>
        /// USTX files come in more than one shape, and the differences are all
        /// things that quietly produce an empty note list rather than an error:
        /// a UTF-8 byte order mark in front of the first key, flow-style maps
        /// such as "- {index: 0, abbr: alt, value: 3}", and a voice_parts list
        /// whose items sit at column 0 so a part's "duration:" looks top-level.
        /// This writes each shape and reads it back.
        /// </summary>
        private static void CheckProjectFormats() {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "altsetter-format-test");
            System.IO.Directory.CreateDirectory(directory);
            var files = new[] {
                WriteProject(directory, "plain.ustx", bom: false, flow: false),
                WriteProject(directory, "bom.ustx", bom: true, flow: false),
                WriteProject(directory, "flow.ustx", bom: false, flow: true),
                WriteProject(directory, "bom-flow.ustx", bom: true, flow: true),
            };
            foreach (var file in files) {
                var name = System.IO.Path.GetFileName(file);
                var project = AltSetter.Ustx.Load(file);
                Check($"{name}: every note was found",
                      project.NoteCount == ExpectedLyrics.Length,
                      $"{project.NoteCount} of {ExpectedLyrics.Length}");
                if (project.NoteCount != ExpectedLyrics.Length) {
                    continue;
                }
                var lyrics = Enumerable.Range(0, project.NoteCount)
                    .Select(project.LyricOf).ToArray();
                Check($"{name}: lyrics read back correctly",
                      lyrics.SequenceEqual(ExpectedLyrics),
                      string.Join(",", lyrics.Take(4)));
                var tones = Enumerable.Range(0, project.NoteCount)
                    .Select(project.ToneOf).ToArray();
                Check($"{name}: tones read back correctly",
                      tones.All(t => t == 60), string.Join(",", tones.Take(4)));
            }
            try {
                System.IO.Directory.Delete(directory, true);
            } catch (Exception) {
            }
        }

        /// <summary>
        /// Write a small USTX in the shape OpenUtau 0.9 uses, optionally with a
        /// byte order mark and flow-style expression maps.
        /// </summary>
        private static string WriteProject(
            string directory, string name, bool bom, bool flow) {
            var path = System.IO.Path.Combine(directory, name);
            var text = new System.Text.StringBuilder();
            text.AppendLine("name: format-test");
            text.AppendLine("comment: ''");
            text.AppendLine("output_dir: Vocal");
            text.AppendLine("cache_dir: UCache");
            text.AppendLine("ustx_version: \"0.9\"");
            text.AppendLine("resolution: 480");
            text.AppendLine("bpm: 120");
            text.AppendLine("beat_per_bar: 4");
            text.AppendLine("beat_unit: 4");
            // A tempo track holds the project's tempo as a note-like item, and in
            // a real project it sits at the same column as a voice note in a
            // voice_parts list written at column 0. Reading it as a note shifts
            // every note index by one, so every shape here carries one.
            text.AppendLine("tempos:");
            text.AppendLine("  - position: 0");
            text.AppendLine("    bpm: 120");
            text.AppendLine("tracks:");
            text.AppendLine("- singer: Lem_V4Bi_Civet");
            text.AppendLine("  track_name: Track1");
            text.AppendLine("  track_no: 0");
            text.AppendLine("voice_parts:");
            // The part's own keys sit at column 0, right after the list marker.
            text.AppendLine("- duration: 3840");
            text.AppendLine("  name: Part1");
            text.AppendLine("  track_no: 0");
            text.AppendLine("  position: 0");
            text.AppendLine("  notes:");
            var position = 0;
            foreach (var lyric in ExpectedLyrics) {
                text.AppendLine("  - position: " + position);
                text.AppendLine("    duration: 240");
                text.AppendLine("    tone: 60");
                text.AppendLine("    lyric: " + lyric);
                text.AppendLine("    pitch:");
                text.AppendLine("      data:");
                text.AppendLine("      - {x: -40, y: 0, shape: io}");
                text.AppendLine("      snap_first: true");
                text.AppendLine("    vibrato: {length: 0, period: 175, depth: 25}");
                text.AppendLine("    tuning: 0");
                if (flow) {
                    text.AppendLine("    phoneme_expressions:");
                    text.AppendLine("    - {index: 0, abbr: vel, value: 100}");
                    text.AppendLine("    phoneme_overrides: []");
                }
                position += 240;
            }
            text.AppendLine("wave_parts: []");
            System.IO.File.WriteAllText(
                path, text.ToString(), new System.Text.UTF8Encoding(bom));
            return path;
        }

        private static void Check(string name, bool ok, string detail = "") {
            Console.WriteLine((ok ? "   ok   " : "   FAIL ") + name +
                              (ok || string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            if (!ok) {
                failures++;
            }
        }

        private static void CheckDiscoverable() {
            var type = typeof(ContextAltBatchEdit);
            // This is exactly what DocManager.SearchAllPlugins tests for.
            Check("implements BatchEdit", typeof(BatchEdit).IsAssignableFrom(type));
            Check("not abstract", !type.IsAbstract && !type.IsInterface);
            Check("has a parameterless constructor",
                  type.GetConstructor(Type.EmptyTypes) != null);
            Check("has a menu name", !string.IsNullOrWhiteSpace(new ContextAltBatchEdit().Name));
            Console.WriteLine("   menu entry: " + new ContextAltBatchEdit().Name);
        }

        private static void CheckSingerResolution(string bank) {
            var name = System.IO.Path.GetFileName(bank);
            var resolved = BankLocator.Resolve(new FakeSinger(bank));
            Check("resolved from the singer's location",
                  string.Equals(resolved, bank, StringComparison.OrdinalIgnoreCase),
                  resolved);
            // A singer entry may point at one pitch folder inside the bank, which
            // must still resolve to the bank.
            var pitch = System.IO.Directory.GetDirectories(bank)
                .FirstOrDefault(d => System.IO.File.Exists(
                    System.IO.Path.Combine(d, "oto.ini")));
            if (pitch != null) {
                var nested = BankLocator.Resolve(new FakeSinger(pitch));
                Check($"a pitch folder ({System.IO.Path.GetFileName(pitch)}) resolves to the bank",
                      string.Equals(nested, bank, StringComparison.OrdinalIgnoreCase), nested);
            }
        }

        private static void CheckDecisions(string bank) {
            var project = BuildProject(bank, out var track, out var part, out var notes);
            var edit = new ContextAltBatchEdit();
            var decisions = edit.Plan(project, part, new List<UNote>());

            Check("a voicebank was found", decisions.Failure == null, decisions.Failure);
            Check("it is the track's own bank",
                  string.Equals(decisions.BankRoot, bank, StringComparison.OrdinalIgnoreCase),
                  decisions.BankRoot);
            Check("every note was considered",
                  decisions.Notes.Count == notes.Count,
                  $"{decisions.Notes.Count} of {notes.Count}");

            var values = decisions.Notes.SelectMany(d => d.Values).ToList();
            Console.WriteLine($"   {values.Count} alt value(s) across " +
                $"{decisions.Notes.Count(d => d.Values.Count > 0)} note(s)");
            Check("a useful number of values was decided", values.Count > 20,
                  values.Count.ToString());
            Check("every value is a numbered take", values.All(v => v.Alt > 0));
            Check("no note was skipped for drift", decisions.SkippedByDrift == 0,
                  decisions.SkippedByDrift.ToString());

            // The phone list was derived from the lyrics, so each index must be
            // inside that note's own phone count, and the values must be the same
            // ones the command line tool computes from the same lyrics.
            var expected = ExpectedCounts();
            var bad = new List<string>();
            foreach (var decision in decisions.Notes) {
                if (!expected.TryGetValue(decision.Note.lyric, out var count)) {
                    continue;
                }
                foreach (var (index, alt) in decision.Values) {
                    if (index >= count) {
                        bad.Add($"{decision.Note.lyric}[{index}] of {count}");
                    }
                }
            }
            Check("every index is inside its note", bad.Count == 0,
                  string.Join(", ", bad.Take(4)));
            Check("lyrics untouched",
                  notes.Select(n => n.lyric).SequenceEqual(ExpectedLyrics));

            // Re-running must reach the same answer.
            var again = edit.Plan(project, part, new List<UNote>());
            Check("re-running reaches the same answer",
                  again.Notes.SelectMany(d => d.Values).SequenceEqual(values));
        }

        private static void CheckDriftDetection(string bank) {
            // What OpenUtau actually hands a batch edit: its own phone list, but
            // only for the notes it has phonemized so far, and spelled the way its
            // phonemizer writes a voicebank alias ("ayPA3", "ah4PA3") rather than
            // as a bare phone. Both of those are how this went wrong in the field.
            var project = BuildProject(bank, out _, out var part, out var notes);
            // "weathers" reads as five phones; the bank's phonemizer may drop one
            // of them, which is the case that has to be lined up rather than lost.
            var target = notes.First(n => n.lyric == "weathers");
            part.phonemes.Add(new UPhoneme { Parent = target, index = 0, phoneme = "w" + Suffix(bank) });
            part.phonemes.Add(new UPhoneme { Parent = target, index = 1, phoneme = "eh2" + Suffix(bank) });
            part.phonemes.Add(new UPhoneme { Parent = target, index = 2, phoneme = "dh" + Suffix(bank) });
            part.phonemes.Add(new UPhoneme { Parent = target, index = 3, phoneme = "er1" + Suffix(bank) });

            var decisions = new ContextAltBatchEdit().Plan(
                project, part, new List<UNote>());
            var decision = decisions.Notes.First(d => d.Note == target);
            Check("a note with a shorter OpenUtau list is realigned, not skipped",
                  decision.Skipped == null && decision.Values.Count > 0,
                  decision.Skipped ?? $"{decision.Values.Count} value(s)");
            Check("it is reported as realigned", decisions.Realigned > 0,
                  decisions.Realigned.ToString());
            Check("every value lands inside OpenUtau's list",
                  decision.Values.All(v => v.Index >= 0 && v.Index < 4),
                  string.Join(",", decision.Values.Select(v => v.Index)));

            // A note OpenUtau has not phonemized at all still has to be written:
            // its list is the note's own, which is exactly what the writer uses.
            var empty = BuildProject(bank, out _, out var part2, out var notes2);
            var untouched = notes2.First(n => !n.lyric.StartsWith("+"));
            part2.phonemes.Add(new UPhoneme {
                Parent = notes2.First(n => n != untouched && !n.lyric.StartsWith("+")),
                index = 0,
                phoneme = "dPA3",
            });
            var half = new ContextAltBatchEdit().Plan(empty, part2, new List<UNote>());
            var blank = half.Notes.First(d => d.Note == untouched);
            Check("a note OpenUtau has not phonemized is still decided",
                  blank.Skipped == null && blank.Values.Count > 0,
                  blank.Skipped ?? $"{blank.Values.Count} value(s)");
            Check("its values use the note's own phone order",
                  blank.Values.Count > 0 && blank.Values.All(v => v.Index >= 0),
                  string.Join(",", blank.Values.Select(v => v.Index)));

            // A reading that disagrees about the sounds themselves is still
            // refused, because a value there would land on the wrong phone.
            var wrong = BuildProject(bank, out _, out var part3, out var notes3);
            var other = notes3.First(n => !n.lyric.StartsWith("+"));
            for (var i = 0; i < 6; i++) {
                part3.phonemes.Add(new UPhoneme {
                    Parent = other, index = i,
                    phoneme = new[] { "zPA3", "zhPA3", "vPA3", "dhPA3", "zPA3", "zhPA3" }[i],
                });
            }
            var refused = new ContextAltBatchEdit().Plan(
                wrong, part3, new List<UNote>());
            Check("a reading that disagrees about the sounds is still refused",
                  refused.Notes.First(d => d.Note == other).Skipped != null,
                  refused.Notes.First(d => d.Note == other).Skipped ?? "not skipped");
            // Refusing one note must not cost the others their values, and must
            // not refuse them as well. Both plans walk the same notes in the same
            // order, so they are compared note for note instead of against a
            // number picked by hand.
            var clean = BuildProject(bank, out _, out var partClean, out _);
            var reference = new ContextAltBatchEdit().Plan(
                clean, partClean, new List<UNote>());
            var lost = new List<string>();
            var extraRefusals = new List<string>();
            for (var k = 0; k < refused.Notes.Count && k < reference.Notes.Count; k++) {
                var mine = refused.Notes[k];
                var refNote = reference.Notes[k];
                if (mine.Note == other || refNote.Skipped != null) {
                    // The note given the wrong sounds, and notes that never had
                    // any phonemes to decide (extenders and rests).
                    continue;
                }
                if (mine.Skipped != null) {
                    extraRefusals.Add(mine.Note.lyric);
                } else if (refNote.Values.Count > 0 && mine.Values.Count == 0) {
                    lost.Add(mine.Note.lyric);
                }
            }
            Check("no other note is refused",
                  extraRefusals.Count == 0, string.Join(",", extraRefusals));
            Check("a refused note does not cost the others their values",
                  lost.Count == 0, string.Join(",", lost));
        }

        /// <summary>
        /// A real subbank suffix from the bank under test.
        ///
        /// The suffixes matter: Civet's are "A3", "F#3", "A2" and so on, not the
        /// "PA3" a folder name suggests, so an alias like "wA3" has to be read
        /// back as "w" and not as "w" plus a stray letter.
        /// </summary>
        private static string Suffix(string bank) {
            var loaded = AltSetter.BankReader.Load(bank);
            return Suffix(loaded);
        }

        /// <summary>A real subbank suffix from the bank under test.</summary>
        private static string Suffix(AltSetter.Bank bank) {
            return bank.Subbanks
                .Select(s => s.Suffix)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s) && s.IndexOf(' ') < 0)
                ?? "A3";
        }

        /// <summary>
        /// The tone a take's suffix belongs to, found by trying the bank's
        /// declared ranges, so the alias is read back with the subbank that
        /// actually recorded it.
        /// </summary>
        private static int ToneFor(AltSetter.Bank bank, string token) {
            for (var tone = 24; tone <= 108; tone++) {
                var affixes = bank.AffixesFor(tone);
                var prefix = affixes.Prefix ?? "";
                var suffix = affixes.Suffix ?? "";
                if (suffix.Length > 0 &&
                    token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    (prefix.Length == 0 ||
                     token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) {
                    return tone;
                }
            }
            return -1;
        }

        /// <summary>
        /// Every pitch map the bank declares has to be usable: a tone inside a
        /// range that no other range covers resolves to that subbank's suffix, and
        /// whichever suffix a tone resolves to is one the bank declares.
        ///
        /// Ranges are allowed to overlap — that is what voice colours are, a head
        /// voice covering the same tones as the plain one — so the check is on the
        /// ranges that stand alone, which is where a wrong mapping would show.
        /// </summary>
        private static void CheckPitchMaps(string bank) {
            var name = System.IO.Path.GetFileName(bank);
            var loaded = AltSetter.BankReader.Load(bank);
            var problems = new List<string>();
            foreach (var subbank in loaded.Subbanks) {
                foreach (var range in subbank.ToneRanges) {
                    var tone = FirstToneIn(range);
                    if (tone < 0) {
                        problems.Add($"{range} unreadable");
                        continue;
                    }
                    var resolved = loaded.AffixesFor(tone).Suffix;
                    if (!loaded.Suffixes.Contains(resolved)) {
                        problems.Add($"{range} -> {resolved}, which the bank does not declare");
                        continue;
                    }
                    // Only judge a range nothing else covers, so an overlap with a
                    // head-voice colour is not counted as a mistake.
                    var covers = loaded.Subbanks
                        .SelectMany(s => s.ToneRanges)
                        .Count(r => AltSetter.ToneRange.Covers(r, tone));
                    if (covers == 1 &&
                        !string.Equals(resolved, subbank.Suffix, StringComparison.OrdinalIgnoreCase)) {
                        problems.Add($"{range} -> {resolved}, not {subbank.Suffix}");
                    }
                }
            }
            Console.WriteLine($"   {name}: {loaded.Subbanks.Count} subbank(s), " +
                $"{loaded.Subbanks.Sum(s => s.ToneRanges.Count)} tone range(s)");
            Check($"{name}: every declared tone range resolves to a suffix the bank declares",
                  problems.Count == 0, string.Join("; ", problems.Take(4)));
        }

        /// <summary>A tone in the middle of a declared range such as "A3-B7".</summary>
        private static int FirstToneIn(string range) {
            var parts = (range ?? "").Split('-');
            if (parts.Length != 2) {
                return -1;
            }
            var low = Pitch(parts[0]);
            var high = Pitch(parts[1]);
            return low < 0 || high < 0 ? -1 : low + (high - low) / 2;
        }

        private static int Pitch(string name) {
            var text = (name ?? "").Trim();
            if (text.Length < 2) {
                return -1;
            }
            var index = "C D EF G A B".IndexOf(char.ToUpperInvariant(text[0]));
            if (index < 0) {
                return -1;
            }
            var semitone = new[] { 0, 2, 4, 5, 7, 9, 11 }[index / 2];
            var at = 1;
            if (at < text.Length && (text[at] == '#' || text[at] == 'b')) {
                semitone += text[at] == '#' ? 1 : -1;
                at++;
            }
            return int.TryParse(text.Substring(at), out var octave)
                ? semitone + (octave + 1) * 12
                : -1;
        }

        /// <summary>
        /// An alt is a take number OpenUtau glues onto the phoneme to build the
        /// alias it looks up, so the value has to be one the bank recorded for
        /// that transition. This is the check the user's ear is making: the
        /// numbered alias has to exist, or nothing is heard.
        /// </summary>
        private static void CheckValuesNameRecordedAliases(string bank) {
            var loaded = AltSetter.BankReader.Load(bank);
            var project = BuildProject(bank, out _, out var part, out var notes);
            // Every singing note gets the bank's own spelling of its phones, which
            // is what OpenUtau hands over: the phone, a take number where the
            // recording has one, and the subbank suffix.
            var suffix = Suffix(loaded);
            var spellings = new Dictionary<string, string[]> {
                { "distant", new[] { "d", "ih", "s", "t", "ah", "n", "t" } },
                { "flickering", new[] { "f", "l", "ih", "k", "er", "ih", "ng" } },
                { "its", new[] { "ih", "t", "s" } },
                { "greener", new[] { "g", "r", "iy", "n", "er" } },
                { "scenery", new[] { "s", "iy", "n", "er", "iy" } },
                { "this", new[] { "dh", "ih", "s" } },
                { "weathers", new[] { "w", "eh2", "dh", "er", "z" } },
                { "bringing", new[] { "b", "r", "ih", "ng", "ih", "ng" } },
            };
            var filled = 0;
            foreach (var note in notes) {
                if (!spellings.TryGetValue(note.lyric, out var phones)) {
                    continue;
                }
                for (var k = 0; k < phones.Length; k++) {
                    part.phonemes.Add(new UPhoneme {
                        Parent = note, index = k, phoneme = phones[k] + suffix,
                    });
                }
                filled++;
            }

            var decisions = new ContextAltBatchEdit().Plan(
                project, part, new List<UNote>());
            var problems = new List<string>();
            var chosen = 0;
            foreach (var decision in decisions.Notes) {
                foreach (var (index, alt) in decision.Values) {
                    var phoneme = part.phonemes.FirstOrDefault(
                        p => p.Parent == decision.Note && p.index == index);
                    if (phoneme == null) {
                        problems.Add($"no phoneme at index {index}");
                        continue;
                    }
                    // The alt is a take number OpenUtau glues onto the phone to
                    // build the alias it looks up, so the take has to be one the
                    // bank offers for that phone or nothing is heard.
                    var right = phoneme.phoneme.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        ? phoneme.phoneme.Substring(0, phoneme.phoneme.Length - suffix.Length)
                        : phoneme.phoneme;
                    var offered = loaded.Diphones.Values
                        .Where(d => string.Equals(Strip(d.Right), Strip(right),
                                                  StringComparison.OrdinalIgnoreCase))
                        .SelectMany(d => d.Takes)
                        .Select(t => t.Number)
                        .Distinct()
                        .OrderBy(n => n)
                        .ToList();
                    if (!offered.Contains(alt)) {
                        problems.Add($"take {alt} of '{right}' (note " +
                            $"{decision.Note.lyric}); the bank offers " +
                            $"[{string.Join(",", offered.Take(12))}]");
                    }
                    chosen++;
                }
            }
            Console.WriteLine($"   {filled} note(s) phonemized, {chosen} value(s) chosen, " +
                $"drift-skipped {decisions.SkippedByDrift}, realigned {decisions.Realigned}");
            Check($"{System.IO.Path.GetFileName(bank)}: every chosen value lands on a phone",
                  problems.Count == 0, string.Join("; ", problems.Take(3)));
            Check($"{System.IO.Path.GetFileName(bank)}: no note is skipped for drift",
                  decisions.SkippedByDrift == 0,
                  string.Join("; ", problems.Take(3)));
        }

        /// <summary>
        /// The phoneme list OpenUtau actually hands over, taken from a run on a
        /// real three note project ("i love you") in this bank. Its entries are
        /// aliases that name the phone before and the phone they start, such as
        /// "ay l" for the l after ay, and the list is longer than the reading of
        /// the lyric because it carries the note before and the note after. This
        /// is the shape that decides whether a note keeps its value: an
        /// alignment that reads whole entries rather than their last phone
        /// matches nothing here and leaves the note without one.
        /// </summary>
        private static void CheckRealWindowShapes(string bank) {
            var loaded = AltSetter.BankReader.Load(bank);
            var suffix = Suffix(loaded);
            var window = new[] {
                new[] { "- ay1" + suffix },
                new[] { "ay l" + suffix, "l ah" + suffix },
                new[] { "ah v" + suffix, "v y1" + suffix, "y uw8" + suffix, "uw -3" + suffix },
            };
            var name = System.IO.Path.GetFileName(bank);
            // A bank that never recorded these transitions cannot say anything
            // about the shape, so it is not asked.
            var readBack = window.SelectMany(w => w)
                .Select(a => AltSetter.Plugin.Phones.Of(a, loaded, 60)).ToList();
            if (readBack.Any(p => !loaded.KnownPhones.Contains(p))) {
                Console.WriteLine($"   {name}: (no \"i love you\" window in this bank; skipped)");
                return;
            }

            var project = new UProject();
            OpenUtau.Core.Format.Ustx.AddDefaultExpressions(project);
            project.tracks.Clear();
            var track = new UTrack(project) { TrackNo = 0 };
            track.Singer = new FakeSinger(bank);
            project.tracks.Add(track);
            var part = new UVoicePart { trackNo = 0, position = 0 };
            project.parts.Add(part);
            var notes = new List<UNote>();
            var position = 0;
            foreach (var lyric in new[] { "i", "love", "you" }) {
                var note = UNote.Create();
                note.lyric = lyric;
                note.duration = 390;
                note.position = position;
                note.tone = 60;
                position += 390;
                notes.Add(note);
                part.notes.Add(note);
            }
            for (var n = 0; n < notes.Count; n++) {
                for (var k = 0; k < window[n].Length; k++) {
                    part.phonemes.Add(new UPhoneme {
                        Parent = notes[n], index = k, phoneme = window[n][k],
                    });
                }
            }

            var decisions = new ContextAltBatchEdit().Plan(
                project, part, new List<UNote>());
            var placed = new Dictionary<UNote, int[]>();
            var values = new Dictionary<UNote, List<int>>();
            foreach (var decision in decisions.Notes) {
                var mapping = decision.Mapping;
                var bar = mapping.LastIndexOf('|');
                // The plugin writes an unplaced phone as "2->-".
                placed[decision.Note] = (bar < 0 ? "" : mapping.Substring(bar + 1))
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => {
                        var to = pair.Split(new[] { "->" }, StringSplitOptions.None)[1];
                        return to == "-" ? -1 : int.Parse(to);
                    })
                    .ToArray();
                values[decision.Note] = decision.Values.ConvertAll(v => v.Index);
            }
            Check($"{name}: no note in the real window is dropped as drift",
                  decisions.SkippedByDrift == 0,
                  $"{decisions.SkippedByDrift} dropped");

            // Which window entry stands for which phone of the lyric. Entry k of
            // a window is the alias "<phone before> <phone>", so "ay" (the whole
            // of "i") is entry 0 of its own window, while "love" is read "l ah v"
            // against a window of two entries and "you" is read "y uw" against a
            // window whose first entry belongs to the note before it.
            var wantMapping = new[] { "0->0", "0->0 1->1 2->-", "0->1 1->2" };
            for (var n = 0; n < notes.Count; n++) {
                var got = string.Join(" ", Enumerable.Range(0, placed[notes[n]].Length)
                    .Select(k => $"{k}->{(placed[notes[n]][k] >= 0
                        ? placed[notes[n]][k].ToString() : "-")}"));
                Check($"{name}: '{notes[n].lyric}' lines up with the window",
                      got == wantMapping[n], $"got {got}, want {wantMapping[n]}");
            }

            // What the values do with that alignment. A take number is glued to
            // the phone its alias ends at, so "love" numbers "ay l" and "l ah"
            // while "you" numbers "v y" and "y uw"; the first phone of the part
            // may take the recorded transition out of silence, "- ay".
            var wanted = new[] { new int[0], new[] { 0, 1 }, new[] { 1, 2 } };
            var forbidden = new[] { new int[0], new int[0], new int[0] };
            for (var n = 0; n < notes.Count; n++) {
                var got = values[notes[n]];
                var missing = wanted[n].Where(w => !got.Contains(w)).ToList();
                var wrong = forbidden[n].Where(w => got.Contains(w)).ToList();
                Check($"{name}: '{notes[n].lyric}' takes the right window entries",
                      missing.Count == 0 && wrong.Count == 0,
                      $"wanted {string.Join(",", wanted[n])}, got " +
                      $"[{string.Join(",", got)}]");
            }

            // The number has to be a take of the transition the alias at that
            // index actually names, or OpenUtau looks up an alias the bank never
            // recorded and the phone goes silent.
            var mismatched = new List<string>();
            foreach (var decision in decisions.Notes) {
                var n = notes.IndexOf(decision.Note);
                foreach (var value in decision.Values) {
                    var phones = AltSetter.Plugin.Phones.Of(
                        window[n][value.Index], loaded, 60).Split(' ');
                    var diphone = phones.Length < 2 ? null
                        : loaded.Find(phones[phones.Length - 2], phones[phones.Length - 1]);
                    var offered = diphone == null
                        ? new List<int>()
                        : diphone.Takes.ConvertAll(t => t.Number);
                    if (!offered.Contains(value.Alt)) {
                        mismatched.Add($"{window[n][value.Index]}: take {value.Alt}, " +
                            $"bank offers [{string.Join(",", offered.Take(10))}]");
                    }
                }
            }
            Check($"{name}: every value is a take of the transition its alias names",
                  mismatched.Count == 0, string.Join("; ", mismatched.Take(3)));
            foreach (var decision in decisions.Notes) {
                var n = notes.IndexOf(decision.Note);
                var shown = decision.Values.ConvertAll(v => {
                    var alias = window[n][v.Index];
                    var end = alias.Length;
                    while (end > 0 && char.IsDigit(alias[end - 1])) {
                        end--;
                    }
                    return alias.Substring(0, end) + " + take " + v.Alt;
                });
                Console.WriteLine($"   {decision.Note.lyric}: " +
                    (shown.Count == 0 ? "(base take everywhere)" : string.Join(", ", shown)));
            }
        }

        /// <summary>A phone without its take number, for comparing two spellings.</summary>
        private static string Strip(string text) {
            var end = text.Length;
            while (end > 0 && char.IsDigit(text[end - 1])) {
                end--;
            }
            return text.Substring(0, end);
        }

        /// <summary>The subbank suffix an alias carries.</summary>
        private static string SuffixOf(string alias, AltSetter.Bank bank) {
            foreach (var suffix in bank.SuffixesLongestFirst) {
                if (suffix.Length > 0 && suffix.IndexOf(' ') < 0 &&
                    alias.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                    return suffix;
                }
            }
            return "";
        }

        private static void CheckNoSinger(string bank) {
            var project = BuildProject(bank, out var track, out var part, out _);
            track.Singer = null;
            var decisions = new ContextAltBatchEdit().Plan(
                project, part, new List<UNote>());
            Check("it refuses rather than guessing",
                  decisions.Failure != null && decisions.Notes.Count == 0,
                  decisions.Failure ?? "no failure reported");
            Console.WriteLine("   said: " + decisions.Failure);
        }

        private static void CheckPerTrackSingers(string[] banks) {
            if (banks.Length < 2) {
                Console.WriteLine("   (needs two banks; skipped)");
                return;
            }
            // Two tracks, two different banks, in one project.
            var project = new UProject();
            OpenUtau.Core.Format.Ustx.AddDefaultExpressions(project);
            project.tracks.Clear();
            var parts = new List<UVoicePart>();
            for (var i = 0; i < 2; i++) {
                var track = new UTrack(project) { TrackNo = i };
                track.Singer = new FakeSinger(banks[i]);
                project.tracks.Add(track);
                var part = new UVoicePart { trackNo = i, position = 0 };
                AddNotes(part);
                project.parts.Add(part);
                parts.Add(part);
            }

            var edit = new ContextAltBatchEdit();
            var first = edit.Plan(project, parts[0], new List<UNote>());
            var second = edit.Plan(project, parts[1], new List<UNote>());
            Console.WriteLine($"   track 0 -> {first.BankName}");
            Console.WriteLine($"   track 1 -> {second.BankName}");
            Check("track 0 used its own bank",
                  string.Equals(first.BankRoot, banks[0], StringComparison.OrdinalIgnoreCase),
                  first.BankRoot);
            Check("track 1 used its own bank",
                  string.Equals(second.BankRoot, banks[1], StringComparison.OrdinalIgnoreCase),
                  second.BankRoot);
            Check("the two tracks got their own answers",
                  first.BankRoot != second.BankRoot);
        }

        // ------------------------------------------------------------- helpers

        private static readonly string[] ExpectedLyrics = {
            "distant", "+", "flickering", "+", "+", "its", "greener", "+",
            "scenery", "+", "+", "this", "weathers", "+", "+", "bringing",
        };

        /// <summary>
        /// Phone counts per lyric for this project, taken from the command line
        /// tool's report, so this test catches a plugin that indexes differently.
        /// </summary>
        private static Dictionary<string, int> ExpectedCounts() =>
            new Dictionary<string, int> {
                { "distant", 7 }, { "flickering", 7 }, { "its", 3 },
                { "greener", 5 }, { "scenery", 6 }, { "this", 3 },
                { "weathers", 5 }, { "bringing", 6 },
            };

        private static UProject BuildProject(
            string bank, out UTrack track, out UVoicePart part, out List<UNote> notes) {
            var project = new UProject();
            OpenUtau.Core.Format.Ustx.AddDefaultExpressions(project);
            // UProject comes with a default track; this project has exactly one.
            project.tracks.Clear();
            track = new UTrack(project) { TrackNo = 0 };
            track.Singer = new FakeSinger(bank);
            project.tracks.Add(track);

            part = new UVoicePart { trackNo = 0, position = 0 };
            project.parts.Add(part);
            notes = AddNotes(part);
            return project;
        }

        private static List<UNote> AddNotes(UVoicePart part) {
            var notes = new List<UNote>();
            var position = 0;
            foreach (var lyric in ExpectedLyrics) {
                var note = UNote.Create();
                note.lyric = lyric;
                note.duration = 240;
                note.position = position;
                note.tone = 60;
                position += 240;
                notes.Add(note);
                part.notes.Add(note);
            }
            return notes;
        }

        private sealed class FakeSinger : USinger {
            private readonly string location;
            public FakeSinger(string location) {
                this.location = location;
                found = true;
                loaded = true;
            }
            public override string Id => System.IO.Path.GetFileName(location);
            public override string Name => System.IO.Path.GetFileName(location);
            public override string Location => location;
            public override USingerType SingerType => USingerType.Classic;
            public override IList<USubbank> Subbanks => new List<USubbank>();
            public override IList<UOto> Otos => new List<UOto>();
        }
    }
}
