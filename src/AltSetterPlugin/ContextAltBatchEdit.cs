using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
// OpenUtau's own name for the alternate expression is UstxFormat.ALT; this is
// the same string, spelled out so the plugin does not depend on that path.
using UstxFormat = OpenUtau.Core.Format.Ustx;
namespace AltSetter.Plugin {

    /// <summary>
    /// "Lem V4Bi Alt Setter: set alts from recorded context", for OpenUtau's
    /// Notes -> External menu.
    ///
    /// It exists as a batch edit so that it runs inside OpenUtau, on the part the
    /// user is looking at. That matters for two reasons: the voicebank comes from
    /// that part's own track, so a project with several singers in different
    /// tracks is handled correctly, and every change goes through OpenUtau's undo
    /// system, so Ctrl+Z puts it back.
    ///
    /// Only the "alt" phoneme expression is ever written.
    /// </summary>
    public class ContextAltBatchEdit : BatchEdit {
        public string Name => "Lem V4Bi Alt Setter: set alts from recorded context";

        public void Run(UProject project, UVoicePart part,
                        List<UNote> selectedNotes, DocManager docManager) {
            try {
                Apply(project, part, selectedNotes, docManager);
            } catch (Exception error) {
                // This runs from a menu click, so a failure has to surface as a
                // message rather than take the window down.
                Report(docManager, "Alt Setter failed: " + error.Message);
            }
        }

        /// <summary>What one note should get, and why it might have got nothing.</summary>
        public sealed class NoteDecision {
            public UNote Note;
            /// <summary>Phone index within the note, and the take to use there.</summary>
            public readonly List<(int Index, int Alt)> Values =
                new List<(int, int)>();
            /// <summary>Set when the note was skipped, for reporting.</summary>
            public string Skipped;
            /// <summary>The phones this tool read, for the dump.</summary>
            public string Derived = "";
            /// <summary>The phones OpenUtau has for the note, for the dump.</summary>
            public string OpenUtau = "";
            /// <summary>Those phones reduced to bare phones, for the dump.</summary>
            public string Phones = "";
            /// <summary>How the reading was placed on OpenUtau's list, for the dump.</summary>
            public string Mapping = "";
        }

        public sealed class Decisions {
            public readonly List<NoteDecision> Notes = new List<NoteDecision>();
            /// <summary>The voicebank these choices came from, for reporting.</summary>
            public string BankRoot;
            public string Failure;
            public int SkippedByDrift;
            public int Realigned;
            public int KeptBaseTake;

            public string BankName =>
                string.IsNullOrEmpty(BankRoot)
                    ? ""
                    : BankRoot.Split('\\', '/').LastOrDefault() ?? BankRoot;
        }

        /// <summary>
        /// Work out what every note in a part should be given, without touching
        /// the project.
        ///
        /// This is the whole decision: which voicebank, which phone sequence,
        /// which take per transition, and where a note has to be left alone. It
        /// is separate from applying the result so it can be checked on its own.
        /// </summary>
        public Decisions Plan(UProject project, UVoicePart part,
                              List<UNote> selectedNotes) {
            var result = new Decisions();
            var track = project.tracks[part.trackNo];
            var singer = track?.Singer;
            if (singer == null || string.IsNullOrWhiteSpace(singer.Location)) {
                result.Failure = "no singer on this track, so there is no voicebank " +
                    "to read recorded context from";
                return result;
            }
            var bankRoot = BankLocator.Resolve(singer);
            if (bankRoot == null) {
                result.Failure = "could not find an oto.ini for \"" + singer.Name +
                    "\" at " + singer.Location;
                return result;
            }
            try {
                result.BankRoot = bankRoot;
            } catch (Exception error) {
                result.Failure = "could not read " + bankRoot + ": " + error.Message;
                return result;
            }

            // Work on the selection when there is one, the whole part otherwise,
            // matching how OpenUtau's own batch edits behave.
            var notes = selectedNotes != null && selectedNotes.Count > 0
                ? selectedNotes.OrderBy(n => n.position).ToList()
                : part.notes.OrderBy(n => n.position).ToList();
            if (notes.Count == 0) {
                result.Failure = "the part has no notes";
                return result;
            }

            var bank = BankReader.Load(result.BankRoot);
            var selector = new Selector(bank);
            // The phone sequence has to be the whole part even when only some
            // notes are being worked on: a transition that crosses into a note
            // outside the selection names the phone it reaches, and the first
            // phone of the part is sung out of silence while the first phone of a
            // selection is not. So the plan is built over every note and only the
            // chosen ones are read back.
            var all = part.notes.OrderBy(n => n.position).ToList();
            var plan = BuildPlan(all, selector,
                Pronouncing.LoadForBank(result.BankRoot));
            var chosen = new List<int>();
            var limit = Environment.GetEnvironmentVariable("ALTSETTER_NOTES");
            if (!string.IsNullOrWhiteSpace(limit) &&
                int.TryParse(limit.Split('-')[0], out var first)) {
                var last = limit.Contains("-") && int.TryParse(limit.Split('-')[1], out var to)
                    ? to : first;
                for (var i = 0; i < all.Count; i++) {
                    if (i >= first && i <= last) {
                        chosen.Add(i);
                    }
                }
            } else {
                var wanted = selectedNotes != null && selectedNotes.Count > 0
                    ? new HashSet<UNote>(selectedNotes)
                    : null;
                for (var i = 0; i < all.Count; i++) {
                    if (wanted == null || wanted.Contains(all[i])) {
                        chosen.Add(i);
                    }
                }
            }

            // An alt expression is indexed by the note's phoneme list, which is
            // the list OpenUtau hands the resampler. That list is only filled in
            // for the notes OpenUtau has phonemized so far — often a handful of
            // them — and where it is filled in, "phoneme" is the voicebank alias
            // ("ayPA3", "ah4PA3"), not a bare phone. Both of those are handled
            // here: the note's own list is used when there are no OpenUtau phones
            // to compare against, and the aliases are reduced to phones before
            // they are compared.
            var byNote = IndexPhonemes(part);
            foreach (var i in chosen) {
                var note = all[i];
                var decision = new NoteDecision { Note = note };
                result.Notes.Add(decision);
                if (!plan.Notes.TryGetValue(Key(i), out var notePlan) ||
                    notePlan.Slots.Count == 0) {
                    decision.Skipped = "no phonemes";
                    continue;
                }
                decision.Derived = string.Join(" ", notePlan.Slots.Select(s => s.Phone));
                var phonemes = byNote.TryGetValue(note, out var list)
                    ? list
                    : new List<UPhoneme>();
                var filled = phonemes.Count > 0;
                decision.OpenUtau = filled
                    ? string.Join(" ", phonemes.Select(p => p.phoneme ?? "?"))
                    : "";
                decision.Phones = filled
                    ? string.Join(" ", phonemes.Select(p => Phones.Of(p.phoneme, bank, note.tone)))
                    : "";

                int[] where;
                if (!filled || phonemes.Count == notePlan.Slots.Count) {
                    // Either there is nothing to line up against, or the two
                    // already agree phone for phone.
                    where = Identity(notePlan.Slots.Count);
                } else {
                    var mine = notePlan.Slots.ConvertAll(s => s.Phone);
                    var theirs = phonemes.ConvertAll(p => Phones.Of(p.phoneme, bank, note.tone));
                    where = PhoneAlign.Map(mine, theirs);
                    // What was compared is kept for the report: when a note is
                    // refused, the two readings are the evidence.
                    decision.Phones = string.Join("/", theirs);
                    decision.Mapping =
                        $"mine={string.Join(",", mine)} theirs={string.Join(",", theirs)}";
                    if (where == null) {
                        decision.Skipped = "the bank's phones for this note " +
                            "cannot be matched to the lyric";
                        result.SkippedByDrift++;
                        continue;
                    }
                    decision.Mapping += " -> " + string.Join(" ",
                        where.Select((w, k) => w >= 0 ? $"{k}->{w}" : $"{k}->-"));
                    result.Realigned++;
                }
                decision.Mapping += " | " + string.Join(" ",
                    where.Select((w, k) => w >= 0 ? $"{k}->{w}" : $"{k}->-"));
                for (var slot = 0; slot < notePlan.Slots.Count; slot++) {
                    var alt = notePlan.Slots[slot].Alt;
                    if (alt > 0 && where[slot] >= 0) {
                        decision.Values.Add((where[slot], alt));
                    } else if (alt < 0) {
                        result.KeptBaseTake++;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// OpenUtau's name for the sound behind a phoneme.
        ///
        /// <c>UPhoneme.phoneme</c> holds a voicebank alias as often as a bare
        /// phone: "ay", "ayPA3", "ah4PA3", "r-PA3". An alias carries the bank's
        /// subbank suffix, a take number and the "-" that marks a hold. The
        /// suffixes come straight from the bank's character.yaml, and every bank
        /// spells them differently — "A3" in Civet, "PA2" in Phascogale, "QA2" in
        /// Quoll, "A3H" for head voice — so they are read from there rather than
        /// assumed.
        /// </summary>


        private static int[] Identity(int count) {
            var where = new int[count];
            for (var i = 0; i < count; i++) {
                where[i] = i;
            }
            return where;
        }

        private void Apply(UProject project, UVoicePart part,
                           List<UNote> selectedNotes, DocManager docManager) {
            var decisions = Plan(project, part, selectedNotes);
            if (decisions.Failure != null) {
                Report(docManager, "Alt Setter: " + decisions.Failure + ".");
                return;
            }
            if (decisions.Notes.Count == 0) {
                Report(docManager, "Alt Setter: nothing to do, the part has no notes.");
                return;
            }
            var track = project.tracks[part.trackNo];
            var byNote = IndexPhonemes(part);
            var written = 0;
            var dump = new System.Text.StringBuilder();
            dump.AppendLine("part\ttrack\tnote\tposition\tlyric\topenutau_aliases\t" +
                "openutau_phones\tderived_phones\tmapping\twritten\twhy");

            docManager.StartUndoGroup("command.batch.alt", true);
            try {
                var order = 0;
                foreach (var decision in decisions.Notes) {
                    var note = decision.Note;
                    var phonemes = byNote.TryGetValue(note, out var list)
                        ? list
                        : new List<UPhoneme>();
                    var applied = new List<string>();
                    foreach (var (index, alt) in decision.Values) {
                        var phoneme = phonemes.FirstOrDefault(p => p.index == index);
                        if (phoneme != null) {
                            // Only the alt parameter is set. Its alias is left
                            // exactly as the phonemizer wrote it: an override on
                            // the phoneme is the user's business, not this tool's.
                            docManager.ExecuteCmd(new SetPhonemeExpressionCommand(
                                project, track, part, phoneme, UstxFormat.ALT, alt));
                            applied.Add($"{index}:{alt}");
                            written++;
                        } else if (phonemes.Count == 0) {
                            // The part has not been phonemized yet; the note's own
                            // expression list is still the right place for it.
                            note.SetExpression(project, track, UstxFormat.ALT,
                                new float?[] { alt });
                            applied.Add($"{index}:{alt}(note)");
                            written++;
                        } else {
                            // OpenUtau's list is missing the phone this value was
                            // meant for, which is a bug here rather than a case to
                            // paper over: a note-level expression would put the
                            // value on the note instead of on the phone.
                            applied.Add($"{index}:{alt}(no phone!)");
                        }
                    }
                    dump.AppendLine(string.Join("\t", new[] {
                        part.name ?? "",
                        (part.trackNo + 1).ToString(),
                        order.ToString(),
                        note.position.ToString(),
                        note.lyric ?? "",
                        decision.OpenUtau,
                        decision.Phones,
                        decision.Derived,
                        decision.Mapping,
                        string.Join(" ", applied),
                        decision.Skipped ?? "",
                    }));
                    order++;
                }
            } finally {
                docManager.EndUndoGroup();
            }

            WriteDump(dump.ToString());

            Report(docManager, Summary(decisions.BankName, written,
                decisions.SkippedByDrift, decisions.Realigned, decisions.KeptBaseTake));
        }

        /// <summary>
        /// Write the report where the user can find it.
        ///
        /// A plugin runs inside OpenUtau, so its own directory is not this file's
        /// directory and the base directory is OpenUtau's, not the Plugins
        /// folder. All of the likely places are written, and the console gets a
        /// line for each, because a diagnostic nobody can find is no diagnostic.
        /// </summary>
        private static void WriteDump(string text) {
            foreach (var path in DumpPaths) {
                try {
                    System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
                    Console.WriteLine("Alt Setter: wrote " + path);
                } catch (Exception error) {
                    Console.WriteLine("Alt Setter: could not write " + path + ": " + error.Message);
                }
            }
        }

        private static IEnumerable<string> DumpPaths {
            get {
                var configured = Environment.GetEnvironmentVariable("ALTSETTER_DUMP");
                if (!string.IsNullOrWhiteSpace(configured)) {
                    yield return configured;
                }
                var plugin = typeof(ContextAltBatchEdit).Assembly.Location;
                if (!string.IsNullOrEmpty(plugin)) {
                    var directory = System.IO.Path.GetDirectoryName(plugin);
                    if (!string.IsNullOrEmpty(directory)) {
                        yield return System.IO.Path.Combine(directory, "altsetter-dump.tsv");
                    }
                }
                yield return System.IO.Path.Combine(
                    AppContext.BaseDirectory, "altsetter-dump.tsv");
                yield return System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "altsetter-dump.tsv");
            }
        }

        /// <summary>
        /// Derive a phone sequence for the notes and choose a take for every
        /// transition. Mirrors what the command line tool does, using the same
        /// shared code.
        /// </summary>
        private static PhrasePlan BuildPlan(
            IReadOnlyList<UNote> notes, Selector selector, Pronouncing pronouncing) {
            var inputs = new List<PhrasePlan.Input>();
            for (var i = 0; i < notes.Count; i++) {
                var lyric = notes[i].lyric ?? "";
                inputs.Add(new PhrasePlan.Input(Key(i), lyric, IsExtender(lyric)));
            }
            return PhrasePlan.Build(inputs, selector, pronouncing, useFallbackG2p: true);
        }

        /// <summary>
        /// OpenUtau's phonemes for this part, grouped by note and ordered by
        /// index, which is the order the alt index refers to.
        /// </summary>
        private static Dictionary<UNote, List<UPhoneme>> IndexPhonemes(UVoicePart part) {
            var result = new Dictionary<UNote, List<UPhoneme>>();
            foreach (var phoneme in part.phonemes) {
                if (phoneme?.Parent == null) {
                    continue;
                }
                if (!result.TryGetValue(phoneme.Parent, out var list)) {
                    result[phoneme.Parent] = list = new List<UPhoneme>();
                }
                list.Add(phoneme);
            }
            foreach (var list in result.Values) {
                list.Sort((a, b) => a.index.CompareTo(b.index));
            }
            return result;
        }

        /// <summary>
        /// "+" and a bare "-" sustain the previous syllable and add no phonemes,
        /// so they must not contribute phones to the sequence.
        /// </summary>
        private static bool IsExtender(string lyric) {
            var text = (lyric ?? "").Trim().Trim('\'', '"');
            return text.StartsWith("+", StringComparison.Ordinal) || text == "-";
        }

        private static string Key(int index) => "note#" + index;

        private static string Summary(string name, int written, int drifted,
                                      int realigned, int negative) {
            if (written == 0 && drifted == 0) {
                return $"Alt Setter: nothing to change. Every transition in " +
                    $"{name} already sounds best with the bank's own base recording.";
            }
            var text = $"Alt Setter: set {written} alt value(s) from {name}.";
            if (realigned > 0) {
                text += $" {realigned} note(s) were read differently by the bank's " +
                    "phonemizer, so the phones were matched up to place the values.";
            }
            if (drifted > 0) {
                text += $" Skipped {drifted} note(s) whose phonemes do not match " +
                    "this bank's, to avoid putting a value on the wrong phone.";
            }
            if (negative > 0) {
                text += $" {negative} transition(s) had no safe recording and kept " +
                    "the base take.";
            }
            return text;
        }

        private static void Report(DocManager docManager, string message) {
            if (docManager == null) {
                // Outside OpenUtau, for tests; there is no UI to notify.
                Console.WriteLine(message);
                return;
            }
            docManager.ExecuteCmd(new ErrorMessageNotification(message));
        }
    }

    internal static class Phones {
        /// <summary>
        /// The phone inside a voicebank alias such as "ayPA3", "ah4PA3" or
        /// "r-PA3", or the alias itself when it is already a bare phone.
        ///
        /// The bank knows every phone it was recorded with, so the answer is the
        /// known phone that explains the most of the alias, as long as what is
        /// left over is only a take number, the "-" that marks a hold, and the
        /// subbank suffix that character.yaml declares for this note's tone.
        /// Cutting a suffix off the right instead is guesswork: it turns "wPA3"
        /// into "wP" whenever the suffix is spelled "A3".
        /// </summary>
        public static string Of(string alias, Bank bank, int tone) {
            var text = (alias ?? "").Trim().ToLowerInvariant();
            if (text.Length == 0) {
                return "";
            }
            // The alias of a note in a colored subbank opens with that subbank's
            // prefix, which character.yaml declares next to the suffix; it is not
            // part of any phone.
            var prefix = bank.AffixesFor(tone).Prefix;
            if (prefix.Length > 0 &&
                text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                text = text.Substring(prefix.Length);
            }
            if (bank.KnownPhones.Contains(text)) {
                return text;
            }
            var best = "";
            var bestTake = int.MaxValue;
            var bestLeft = int.MaxValue;
            foreach (var phone in bank.KnownPhones) {
                // A phone may be two words: OpenUtau writes the phone before as part
                // of the alias, so "ay l" and "- m" are phones it hands over.
                if (phone.Length == 0 ||
                    !text.StartsWith(phone, StringComparison.Ordinal)) {
                    continue;
                }
                var rest = text.Substring(phone.Length);
                // A take number sits between the phone and the suffix, and it is
                // what says the alias is a numbered take of this phone rather than
                // some longer phone: "m1A3" is the first take of "m", not a phone
                // called "m1". The earliest take wins, and only then the phone that
                // explains the most of the alias. Digits inside the suffix itself
                // ("A3") are not a take, which is why the suffix is removed first.
                var take = TakeIn(rest, bank, tone);
                if (take < 0) {
                    continue;
                }
                var left = rest.Length;
                if (take < bestTake || (take == bestTake &&
                        (left < bestLeft ||
                         (left == bestLeft && phone.Length > best.Length)))) {
                    best = phone;
                    bestTake = take;
                    bestLeft = left;
                }
            }
            return best.Length > 0 ? best : TrimTake(text, bank);
        }

        /// <summary>
        /// Where the take number starts in an alias tail, or -1 when the tail
        /// cannot be a take number followed by one of the bank's suffixes.
        ///
        /// The suffix sits at the end of the alias and a take number may follow
        /// the phone, so the suffix is removed from the end first and what is left
        /// has to be digits: "1A3" is take 1, "A3" is the base take. Reading it the
        /// other way round makes "A3" look like a phone followed by a take in "3".
        /// </summary>
        private static int TakeIn(string tail, Bank bank, int tone) {
            var declared = bank.AffixesFor(tone).Suffix;
            foreach (var suffix in bank.SuffixesLongestFirst) {
                if (suffix.Length == 0 || suffix.IndexOf(' ') >= 0 ||
                    !tail.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                var take = tail.Substring(0, tail.Length - suffix.Length);
                foreach (var c in take) {
                    if (!char.IsDigit(c) && c != '-') {
                        return -1;
                    }
                }
                if (declared.Length > 0 &&
                    suffix.Equals(declared, StringComparison.OrdinalIgnoreCase)) {
                    return take.Length;
                }
                if (take.Length == 0) {
                    return 0;
                }
            }
            return -1;
        }

        /// <summary>
        /// How much of an alias tail is not a subbank suffix, or -1 when it cannot
        /// be one. What may be there is a take number, the "-" that marks a hold,
        /// and one of the suffixes the bank declares — preferring the one its
        /// pitch map gives this note's tone. A suffix that merely ends the string
        /// the same way is not accepted, which is what stops "wPA3" from being
        /// read as "w" plus a stray "P" in a bank that spells its suffixes "A3".
        /// </summary>
        private static int LeftOver(string tail, Bank bank, int tone) {
            var declared = bank.AffixesFor(tone).Suffix;
            if (declared.Length > 0 &&
                TryCut(tail, declared, out var preferred)) {
                return preferred;
            }
            var best = -1;
            foreach (var suffix in bank.SuffixesLongestFirst) {
                if (suffix.Length > 0 && suffix.IndexOf(' ') < 0 &&
                    TryCut(tail, suffix, out var left) && (best < 0 || left < best)) {
                    best = left;
                }
            }
            return best;
        }

        /// <summary>Tail with this suffix removed, when only a take is left.</summary>
        private static bool TryCut(string tail, string suffix, out int left) {
            left = -1;
            if (suffix.Length == 0 || !tail.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            var rest = tail.Substring(0, tail.Length - suffix.Length);
            foreach (var c in rest) {
                if (!char.IsDigit(c) && c != '-') {
                    return false;
                }
            }
            left = rest.Length;
            return true;
        }

        private static string TrimTake(string text, Bank bank) {
            var end = text.Length;
            while (end > 0 && char.IsDigit(text[end - 1])) {
                end--;
            }
            if (end > 0 && end < text.Length) {
                var stem = text.Substring(0, end);
                if (bank.KnownPhones.Contains(stem)) {
                    return stem;
                }
            }
            return text;
        }
    }

    /// <summary>Finds the voicebank folder a loaded singer came from.</summary>
    internal static class BankLocator {
        public static string Resolve(USinger singer) {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(singer?.Location)) {
                candidates.Add(singer.Location);
            }
            var configured = Environment.GetEnvironmentVariable("ALTSETTER_BANK");
            if (!string.IsNullOrWhiteSpace(configured)) {
                candidates.Add(configured);
            }
            var nearby = System.IO.Path.Combine(
                AppContext.BaseDirectory, "voice_path.txt");
            var fromFile = ReadFirstLine(nearby);
            if (fromFile != null) {
                candidates.Add(fromFile);
            }
            foreach (var candidate in candidates) {
                // Always climb: a singer can point at the bank or at one pitch
                // folder inside it, and the bank is the answer either way.
                var root = ResolveRoot(candidate);
                if (root != null) {
                    return root;
                }
            }
            return null;
        }

        /// <summary>
        /// Turn a singer location into a voicebank folder.
        ///
        /// A singer normally points straight at the bank, and that is the answer.
        /// It can also point at one pitch folder inside a multi-pitch bank, and
        /// then the enclosing bank is the right answer, because its other pitch
        /// folders are what the alternate takes have to be valid across.
        ///
        /// The bank's own character.yaml is what tells the two apart: a bank has
        /// one and declares its subbanks in it, a pitch folder does not. Sibling
        /// names would not work, because the folder next to a bank is often just
        /// another bank.
        /// </summary>
        private static string ResolveRoot(string candidate) {
            if (string.IsNullOrWhiteSpace(candidate)) {
                return null;
            }
            var current = candidate.TrimEnd('\\', '/');
            var parent = System.IO.Path.GetDirectoryName(current);
            // A pitch folder is one of several folders carrying an oto.ini under
            // a bank that declares itself with character.yaml. Only the bank
            // knows about every pitch's takes, so that is the answer.
            if (!string.IsNullOrEmpty(parent) && DeclaresBank(parent) &&
                HasPitchSibling(current, parent)) {
                return parent;
            }
            if (ReadsAsBank(current)) {
                return current;
            }
            if (!string.IsNullOrEmpty(parent) && ReadsAsBank(parent)) {
                return parent;
            }
            return null;
        }

        /// <summary>Whether a folder is a voicebank that declares its subbanks.</summary>
        private static bool DeclaresBank(string path) =>
            System.IO.File.Exists(System.IO.Path.Combine(path, "character.yaml"));

        /// <summary>Whether a folder sits next to another folder with an oto.ini.</summary>
        private static bool HasPitchSibling(string path, string parent) {
            try {
                return System.IO.Directory.EnumerateDirectories(parent)
                    .Any(sibling =>
                        !string.Equals(sibling, path, StringComparison.OrdinalIgnoreCase) &&
                        System.IO.File.Exists(
                            System.IO.Path.Combine(sibling, "oto.ini")));
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Whether a folder is a voicebank: it holds usable oto rows itself, has
        /// the character.yaml every bank carries, or contains pitch folders that
        /// hold rows.
        /// </summary>
        private static bool ReadsAsBank(string path) {
            if (string.IsNullOrWhiteSpace(path) ||
                !System.IO.Directory.Exists(path)) {
                return false;
            }
            // The marker file is the strongest signal, and it is cheap.
            if (System.IO.File.Exists(System.IO.Path.Combine(path, "character.yaml"))) {
                return true;
            }
            try {
                if (HasRows(path)) {
                    return true;
                }
                return System.IO.Directory.EnumerateDirectories(path)
                    .Take(4)
                    .Any(HasRows);
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>Whether a folder holds usable oto rows of its own.</summary>
        private static bool HasRows(string path) {
            try {
                return BankReader.Load(path, 0).Diphones.Count > 0;
            } catch (Exception) {
                return false;
            }
        }

        private static string ReadFirstLine(string path) {
            try {
                if (!System.IO.File.Exists(path)) {
                    return null;
                }
                foreach (var raw in System.IO.File.ReadAllLines(path)) {
                    var line = raw.Trim();
                    if (line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)) {
                        return line;
                    }
                }
            } catch (Exception) {
            }
            return null;
        }
    }
}
