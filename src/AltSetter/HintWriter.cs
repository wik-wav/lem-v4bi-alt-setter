using System;
using System.Collections.Generic;
using System.Linq;

namespace AltSetter;

/// <summary>
/// Writes a phrase plan back into a legacy-plugin UST as phonetic hints.
///
/// The hint list is positionally identical to the phoneme list OpenUtau feeds
/// its phonemizer, so an alternate number placed on hint slot i governs the
/// i-th phoneme of that note. OpenUtau maps the notes back by order and
/// recalculates positions from the lengths, so only Lyric lines change.
/// </summary>
internal static class HintWriter {
    public static RewriteStats Run(
        UstDoc doc, Selector selector, Pronouncing pronouncing, bool useFallbackG2p) {
        var inputs = new List<PhrasePlan.Input>();
        foreach (var note in doc.RealNotes) {
            inputs.Add(new PhrasePlan.Input(
                NoteKey(note), note.Lyric, note.IsExtender));
        }
        var plan = PhrasePlan.Build(inputs, selector, pronouncing, useFallbackG2p);
        var stats = plan.Stats;
        foreach (var warning in plan.Warnings) {
            stats.Warnings.Add(warning);
        }

        foreach (var note in doc.RealNotes) {
            if (!plan.Notes.TryGetValue(NoteKey(note), out var notePlan) ||
                notePlan.Slots.Count == 0) {
                continue;
            }
            var hint = LyricHint.Parse(note.Lyric);
            var newPhones = notePlan.Slots
                .Select(s => LyricHint.JoinPhone(s.Phone, s.Alt))
                .ToList();
            var originalPhones = hint.HasHint
                ? hint.Phones.Select(t => {
                    LyricHint.SplitPhone(t, out var p, out var a);
                    return LyricHint.JoinPhone(p, a);
                }).ToList()
                : new List<string>();
            if (hint.HasHint && newPhones.SequenceEqual(originalPhones)) {
                continue;
            }
            hint.HasHint = true;
            hint.Phones.Clear();
            hint.Phones.AddRange(newPhones);
            var formatted = hint.Format();
            if (stats.Trace.Count < 400) {
                stats.Trace.Add($"{note.Lyric}  ->  {formatted}");
            }
            doc.SetLyric(note, formatted);
            stats.NotesRewritten++;
        }
        return stats;
    }

    private static string NoteKey(UstNote note) =>
        note.Header + "#" + note.Index;
}
