using System;
using System.Collections.Generic;
using System.Linq;

namespace AltSetter;

/// <summary>
/// Sets the "alt" expression on a .ustx, and nothing else.
///
/// The tool is deliberately conservative about the project file: it writes real
/// <c>phoneme_expressions</c> entries with <c>abbr: alt</c>, and every other line
/// is written back exactly as it was read, so unknown or future USTX fields
/// cannot be lost or reworded.
/// </summary>
internal sealed class AltApplier {
    public sealed class NoteResult {
        public int NoteIndex;
        public string Lyric = "";
        public int PhonemeCount;
        public bool PhonesFromHint;
        public readonly List<(int Index, int Alt, string Left, string Right, string Context)>
            Picks = new List<(int, int, string, string, string)>();
    }

    public sealed class Result {
        public readonly List<NoteResult> Notes = new List<NoteResult>();
        public int NotesWritten;
        public int ExpressionsWritten;
        public int NotesWithoutPhones;
        public int UnknownWords;
        public int Transitions;
        public int Alternates;
        public readonly Dictionary<int, int> AltHistogram = new Dictionary<int, int>();
        public readonly List<string> Warnings = new List<string>();

        public int NotesWithAlt => Notes.Count(n => n.Picks.Count > 0);
    }

    public static Result Run(
        Ustx project, Selector selector, Pronouncing pronouncing,
        bool useFallbackG2p, bool dryRun) {
        var inputs = new List<PhrasePlan.Input>();
        for (var i = 0; i < project.NoteCount; i++) {
            var lyric = project.LyricOf(i);
            inputs.Add(new PhrasePlan.Input(Key(i), lyric, IsExtender(lyric)));
        }
        var plan = PhrasePlan.Build(inputs, selector, pronouncing, useFallbackG2p);
        var result = new Result {
            NotesWithoutPhones = plan.Stats.NotesWithoutPhones,
            UnknownWords = plan.Stats.UnknownWords,
            Transitions = plan.Stats.Transitions,
            Alternates = plan.Stats.Alternates,
        };
        foreach (var pair in plan.Stats.AltHistogram) {
            result.AltHistogram[pair.Key] = pair.Value;
        }

        // Work out every note's choices first, then write them from the last
        // note backwards. Editing a .ustx inserts lines, and inserting for an
        // earlier note moves the later ones, so going backwards is what keeps
        // every line number valid while it is used.
        // One flat phone sequence for the whole part, so a transition that
        // reaches into the following note can name the phone it reaches.
        var flat = plan.Slots.ToList();

        var planned = new List<NoteResult>();
        for (var i = 0; i < project.NoteCount; i++) {
            if (!plan.Notes.TryGetValue(Key(i), out var notePlan)) {
                continue;
            }
            var note = new NoteResult {
                NoteIndex = i,
                Lyric = project.LyricOf(i),
                PhonemeCount = notePlan.Slots.Count,
                PhonesFromHint = notePlan.HadHint,
            };
            foreach (var slot in notePlan.Slots) {
                if (slot.Alt <= 0) {
                    continue;
                }
                // The take belongs to the transition this phone's alias names,
                // which is the one coming from the phone before it.
                var position = flat.IndexOf(slot);
                var left = position > 0 ? flat[position - 1].Phone : "-";
                note.Picks.Add((slot.NoteSlot, slot.Alt, left, slot.Phone,
                    $"{left}/{slot.Phone}"));
            }
            planned.Add(note);
        }

        result.Notes.AddRange(planned.OrderBy(n => n.NoteIndex));

        for (var i = planned.Count - 1; i >= 0; i--) {
            var note = planned[i];
            if (note.Picks.Count == 0) {
                continue;
            }
            if (!dryRun) {
                var values = note.Picks.Select(p => (p.Index, p.Alt)).ToList();
                result.ExpressionsWritten += project.SetAlternates(
                    note.NoteIndex, values);
            }
            result.NotesWritten++;
        }

        if (result.NotesWithoutPhones > 0) {
            result.Warnings.Add(
                $"{result.NotesWithoutPhones} note(s) have no phonemes, so nothing " +
                "could be decided for them (unknown words, or a lyric the " +
                "dictionary does not contain)");
        }
        return result;
    }

    /// <summary>
    /// "+" and a bare "-" sustain the previous syllable and add no phonemes, so
    /// they must not contribute phones to the sequence. Treating them as words
    /// would shift every later transition's context.
    /// </summary>
    private static bool IsExtender(string lyric) {
        var text = (lyric ?? "").Trim().Trim('\'', '"');
        return text.StartsWith("+", StringComparison.Ordinal) || text == "-";
    }

    private static string Key(int index) => "note#" + index;
}
