using System;
using System.Collections.Generic;
using System.Linq;

namespace AltSetter;

/// <summary>Tally of one pass over a part, shared by every entry point.</summary>
internal sealed class RewriteStats {
    public int Notes;
    public int NotesWithPhones;
    public int Transitions;
    public int Alternates;
    public int NotesRewritten;
    public int NotesWithoutPhones;
    public int NotesWithoutHint;
    public int UnknownWords;
    public readonly Dictionary<int, int> AltHistogram = new Dictionary<int, int>();
    public readonly List<string> Samples = new List<string>();
    public readonly List<string> Trace = new List<string>();
    public readonly List<string> Warnings = new List<string>();
}

/// <summary>
/// Which alternates a whole part wants and why, produced once and then written
/// either as phonetic hints (legacy UST plugin) or as per-phoneme alt
/// expressions (direct .ustx edit).
/// </summary>
internal sealed class PhrasePlan {
    /// <summary>One phoneme of the part, in singing order.</summary>
    internal sealed class Slot {
        public string Phone = "";
        public string NoteKey = "";
        public int NoteSlot;

        /// <summary>
        /// The take number of the transition that ends at this phone, which is
        /// the transition this phone's alias names — "ay l1" is the first
        /// alternate of ay going to l, and OpenUtau glues the number onto the l.
        /// Zero means the base recording, either because the base is the best
        /// take or because nothing recorded leads into this phone, as at the
        /// start of a part.
        /// </summary>
        public int Alt;

        public AltChoice Choice;
        public bool FromG2p;
    }

    internal sealed class NotePlan {
        public string Key = "";
        public string Display = "";
        public bool HadHint;
        public readonly List<Slot> Slots = new List<Slot>();
    }

    public readonly List<Slot> Slots = new List<Slot>();
    public readonly Dictionary<string, NotePlan> Notes =
        new Dictionary<string, NotePlan>(StringComparer.Ordinal);
    public readonly List<string> Warnings = new List<string>();
    public readonly RewriteStats Stats = new RewriteStats();

    public readonly struct Input {
        public readonly string Key;
        public readonly string Lyric;
        public readonly bool IsExtender;
        public Input(string key, string lyric, bool isExtender) {
            Key = key;
            Lyric = lyric;
            IsExtender = isExtender;
        }
    }

    public static PhrasePlan Build(
        IReadOnlyList<Input> inputs, Selector selector, Pronouncing pronouncing,
        bool useFallbackG2p, bool quiet = false) {
        var plan = new PhrasePlan();
        var stats = plan.Stats;

        foreach (var input in inputs) {
            var hint = LyricHint.Parse(input.Lyric);
            var notePlan = new NotePlan {
                Key = input.Key,
                Display = hint.Display,
                HadHint = hint.HasHint,
            };
            plan.Notes[input.Key] = notePlan;

            if (input.IsExtender) {
                // "+" notes sustain the previous syllable and add no phonemes.
                continue;
            }

            List<string> tokens;
            if (hint.HasHint && hint.Phones.Count > 0) {
                tokens = hint.Phones;
            } else if (hint.Display.Trim().Length == 0) {
                continue;
            } else {
                stats.NotesWithoutHint++;
                var phones = Lookup(hint.Display, pronouncing, useFallbackG2p, stats);
                tokens = phones;
                if (phones.Count == 0) {
                    stats.NotesWithoutPhones++;
                    continue;
                }
                if (!quiet) {
                    plan.Warnings.Add(
                        $"\"{hint.Display}\" had no phonetic hint, so phonemes were " +
                        "guessed from the built-in dictionary");
                }
            }

            var slotIndex = 0;
            var guessed = !hint.HasHint;
            foreach (var token in tokens) {
                LyricHint.SplitPhone(token, out var phone, out var _);
                if (phone.Length == 0) {
                    continue;
                }
                var slot = new Slot {
                    Phone = phone,
                    NoteKey = input.Key,
                    NoteSlot = slotIndex++,
                    FromG2p = guessed,
                };
                plan.Slots.Add(slot);
                notePlan.Slots.Add(slot);
            }
            if (notePlan.Slots.Count > 0) {
                stats.NotesWithPhones++;
            }
        }

        var phoneSequence = plan.Slots.Select(s => s.Phone).ToList();

        // The first phone of a part is sung out of silence, and its alias says so
        // — "- ay" — so the take that belongs on it is the one chosen for silence
        // going into it. Choosing for the transition leaving it instead would
        // name a take the "- ay" recordings never had.
        if (plan.Slots.Count > 0) {
            var opening = selector.Choose(
                "-", phoneSequence[0], "*",
                plan.Slots.Count > 1 ? phoneSequence[1] : "*");
            stats.Transitions++;
            if (opening != null) {
                plan.Slots[0].Alt = opening.Alt;
                plan.Slots[0].Choice = opening;
                Tally(stats, opening, "-", phoneSequence[0]);
            }
        }

        for (var i = 0; i + 1 < plan.Slots.Count; i++) {
            var outerLeft = i - 1 >= 0 ? phoneSequence[i - 1] : "*";
            var outerRight = i + 2 < plan.Slots.Count ? phoneSequence[i + 2] : "*";
            var choice = selector.Choose(
                phoneSequence[i], phoneSequence[i + 1], outerLeft, outerRight);
            stats.Transitions++;
            if (choice == null) {
                continue;
            }
            // The choice is for the transition phones[i] to phones[i+1], and the
            // alias that records it is the one belonging to phones[i+1]: OpenUtau
            // names a phone's alias "<phone before> <phone>", so "ay l" is the l
            // after ay. Storing the take on phones[i] instead puts the number on
            // the wrong alias, where it names a take of a different transition —
            // one the bank may not have recorded, which is silence.
            var slot = plan.Slots[i + 1];
            slot.Alt = choice.Alt;
            slot.Choice = choice;
            Tally(stats, choice, phoneSequence[i], phoneSequence[i + 1]);
        }
        stats.Notes = inputs.Count;
        return plan;
    }

    /// <summary>Record one chosen transition in the report's totals.</summary>
    private static void Tally(
        RewriteStats stats, AltChoice choice, string left, string right) {
        if (choice == null || choice.Alt <= 0) {
            return;
        }
        stats.Alternates++;
        stats.AltHistogram[choice.Alt] =
            stats.AltHistogram.TryGetValue(choice.Alt, out var n) ? n + 1 : 1;
        if (stats.Samples.Count < 12) {
            stats.Samples.Add(
                $"{left} {right}: alt {choice.Alt} " +
                $"(left ctx {choice.Take.LeftContext}, right ctx {choice.Take.RightContext})");
        }
    }

    private static List<string> Lookup(
        string display, Pronouncing pronouncing, bool useFallbackG2p,
        RewriteStats stats) {
        var result = new List<string>();
        if (!useFallbackG2p) {
            return result;
        }
        foreach (var word in display.Split(
                     new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) {
            if (word == "+" || word == "R" || word == "r" || word == "-") {
                continue;
            }
            var phones = pronouncing.Query(word);
            if (phones == null) {
                stats.UnknownWords++;
                continue;
            }
            result.AddRange(phones);
        }
        return result;
    }
}
