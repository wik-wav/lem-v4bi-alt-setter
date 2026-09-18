using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AltSetter;

/// <summary>
/// Reads a .ustx far enough to find its notes and to place per-phoneme "alt"
/// expressions on them, and writes it back with every other line untouched.
///
/// Line oriented on purpose: an unknown or future USTX field cannot be lost or
/// reworded, because nothing is ever reformatted. The one structural rule it
/// relies on is that a note is a sequence item carrying a "position", which is
/// what keeps nested lists such as a note's pitch data from being mistaken for
/// notes.
/// </summary>
internal sealed class Ustx {
    /// <summary>A note sequence item: "- position: 17760".</summary>
    private static readonly Regex NoteStart = new Regex(
        @"^(?<indent>[ \t]*)-[ \t]+position:[ \t]*(?<value>-?\d+)[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnyKey = new Regex(
        @"^(?<indent>[ \t]*)(?:-[ \t]+)?(?<key>[A-Za-z_][A-Za-z0-9_]*):[ \t]?(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class NoteBlock {
        public int StartLine;
        public int EndLine;          // exclusive
        public int Indent;
        public int Position;
        public int Duration;
        public int Tone;
        public string Lyric = "";
    }

    private readonly string path;
    private readonly List<string> lines;
    private readonly List<NoteBlock> notes = new List<NoteBlock>();
    private readonly Dictionary<string, NoteBlock> index = new Dictionary<string, NoteBlock>();

    public string VoiceName = "";

    private Ustx(string path, List<string> lines) {
        this.path = path;
        this.lines = lines;
    }

    public int NoteCount => notes.Count;
    public int PositionOf(int index) => notes[index].Position;
    public int DurationOf(int index) => notes[index].Duration;
    public int ToneOf(int index) => notes[index].Tone;
    public string LyricOf(int index) => notes[index].Lyric;

    public static Ustx Load(string path) {
        var text = TextEnc.Decode(File.ReadAllBytes(path));
        var ustx = new Ustx(path, SplitLines(text).ToList());
        ustx.Parse();
        return ustx;
    }

    // ---------------------------------------------------------------- parsing

    private void Parse() {
        Name = ReadProjectName();
        Reindex();
    }

    /// <summary>
    /// Find each note's extent in the current lines and read its fields.
    ///
    /// This runs on load and again after the writer has inserted expression
    /// blocks, because those insertions move every later note and rebuilding the
    /// extents from the text is both simpler and impossible to get subtly wrong,
    /// unlike adjusting them as the lines move.
    /// </summary>
    private void Reindex() {
        notes.Clear();
        index.Clear();
        var section = FindNotesList();
        if (section == null) {
            return;
        }
        var (notesStart, notesEnd, contentIndent) = section.Value;

        // A note is a sequence item that carries a position, which is the same
        // shape the USTX writer uses, so nested lists are skipped.
        var starts = new List<int>();
        for (var i = notesStart; i < notesEnd; i++) {
            if (IndentOf(i) == contentIndent &&
                NoteStart.IsMatch(StripComment(lines[i]))) {
                starts.Add(i);
            }
        }
        for (var n = 0; n < starts.Count; n++) {
            var start = starts[n];
            var end = n + 1 < starts.Count ? starts[n + 1] : notesEnd;
            var block = new NoteBlock {
                StartLine = start,
                EndLine = end,
                Indent = IndentOf(start),
            };
            ReadFields(block);
            notes.Add(block);
        }
    }

    /// <summary>Whether a line is an entry of a flow sequence, "- {a: 1}".</summary>
    private static bool IsFlowEntry(string line) {
        var text = line.TrimStart();
        if (!text.StartsWith("-", StringComparison.Ordinal)) {
            return false;
        }
        text = text.Substring(1).TrimStart();
        return text.StartsWith("{", StringComparison.Ordinal);
    }

    /// <summary>
    /// The column a note marker starts at, or -1 when the line is not one.
    ///
    /// A note is a sequence item carrying a position, which is the same shape
    /// the USTX writer uses, so a nested list such as a note's pitch data is
    /// never mistaken for one.
    /// </summary>
    private int NoteItemIndent(int line) {
        if (line < 0 || line >= lines.Count) {
            return -1;
        }
        return IsSequenceItem(lines[line]) &&
               NoteStart.IsMatch(StripComment(lines[line]))
            ? IndentOf(line)
            : -1;
    }

    /// <summary>
    /// The voice part's notes list, as the range its items occupy.
    ///
    /// A project has one "notes:" list per track, and the tempo track comes
    /// first. Where that track's items sit depends on how the project was
    /// written, so no single column separates them from real notes: in one
    /// project the tempo track is spelled out at the same column a part's notes
    /// occupy in another. What does hold in both shapes is that the voice part's
    /// own notes are the deepest "notes:" list in the file, because a part is
    /// nested inside the parts list while the tempo track is not.
    /// </summary>
    private (int Start, int End, int Indent)? FindNotesList() {
        var notesStart = -1;
        var notesEnd = lines.Count;
        var contentIndent = -1;
        var listIndent = -1;
        var best = 0;

        for (var i = 0; i < lines.Count; i++) {
            var match = AnyKey.Match(StripComment(lines[i]));
            if (!match.Success || match.Groups["key"].Value != "notes" ||
                match.Groups["value"].Value.Trim().Length != 0) {
                continue;
            }
            var keyIndent = IndentOf(i);
            var items = new List<int>();
            for (var j = i + 1; j < lines.Count; j++) {
                if (lines[j].Trim().Length == 0) {
                    continue;
                }
                // A note marker sits at the same column as its "-" marker, so it
                // can be level with the "notes:" key when the key is itself
                // written as a list item. The list ends at the next line that is
                // shallower than the key, wherever the markers are.
                var itemIndent = NoteItemIndent(j);
                if (itemIndent >= 0) {
                    items.Add(j);
                    continue;
                }
                if (IndentOf(j) <= keyIndent) {
                    break;
                }
            }
            if (items.Count == 0) {
                continue;
            }
            // The deepest list is the voice part's. A note marker can sit on the
            // same column as the "notes:" key itself — "- notes:" followed by
            // "  - position:" in a part at column 0, or a part written with its
            // items level with the key — so an equally deep list is decided by
            // size: the tempo track holds a handful of markers, a part holds the
            // song, and the first part comes before the later ones.
            var noteIndent = IndentOf(items[0]);
            if (noteIndent > contentIndent ||
                (noteIndent == contentIndent && items.Count > best)) {
                contentIndent = noteIndent;
                listIndent = keyIndent;
                notesStart = items[0];
                notesEnd = items[^1] + 1;
                best = items.Count;
            }
        }
        if (notesStart < 0) {
            return null;
        }

        // The list runs to the end of the part, which is the next line of the
        // part itself. That matters for the last note: the part goes on with
        // other keys, such as its curves, and a note that stretched over them
        // would take an expression block written at its end with it. A note
        // marker has to be deeper than the list indent to stay inside, so
        // anything at or above it ends the list, sequence item or not.
        for (var i = notesEnd; i < lines.Count; i++) {
            if (lines[i].Trim().Length == 0) {
                continue;
            }
            if (IndentOf(i) <= listIndent) {
                break;
            }
            notesEnd = i + 1;
        }
        return (notesStart, notesEnd, contentIndent);
    }

    public string Name = "";

    /// <summary>
    /// The project's own "name:" key. It is also the singer name when the file
    /// carries no track entry, which is why the "ustx_version: 0.6" shape a
    /// project starts as still names a voicebank.
    /// </summary>
    private string ReadProjectName() {
        for (var i = 0; i < lines.Count; i++) {
            var line = StripComment(lines[i]);
            if (IndentOf(i) != 0) {
                continue;
            }
            var match = AnyKey.Match(line);
            if (match.Success && match.Groups["key"].Value == "name") {
                return BankReader.Unquote(match.Groups["value"].Value.Trim());
            }
        }
        return "";
    }

    /// <summary>The alt expressions a note carries, read back from the file.</summary>
    public IReadOnlyList<(int Index, int Alt)> AlternatesOf(int noteIndex) =>
        ReadAlternates(noteIndex);

    private List<(int Index, int Alt)> ReadAlternates(int noteIndex) {
        var result = new List<(int, int)>();
        if (noteIndex < 0 || noteIndex >= notes.Count) {
            return result;
        }
        var block = notes[noteIndex];
        var index = -1;
        var alt = -1;
        var inBlock = false;
        for (var i = block.StartLine; i < block.EndLine && i < lines.Count; i++) {
            var match = AnyKey.Match(StripComment(lines[i]));
            if (!match.Success) {
                continue;
            }
            var key = match.Groups["key"].Value;
            if (key == "phoneme_expressions") {
                inBlock = true;
                continue;
            }
            if (!inBlock) {
                continue;
            }
            var value = match.Groups["value"].Value.Trim();
            if (key == "index") {
                Num.TryParseInt(value, out index);
            } else if (key == "value" && index >= 0) {
                Num.TryParseInt(value, out alt);
                if (alt >= 0) {
                    result.Add((index, alt));
                }
                index = -1;
                alt = -1;
            }
        }
        return result;
    }

    /// <summary>
    /// The scalar fields a UNote owns, by name.
    ///
    /// Keys belonging to the note and keys belonging to a nested list can land on
    /// exactly the same column 窶・a pitch point's "x:" is indented the same as the
    /// note's "duration:" 窶・so indentation alone cannot separate them. The USTX
    /// note schema is fixed and documented, so the fields this tool needs are
    /// read by name and nothing else is touched.
    /// </summary>
    private static readonly HashSet<string> ScalarKeys = new HashSet<string>(
        new[] { "position", "duration", "tone", "lyric", "pitch", "vibrato",
                "tuning", "phonemizer" },
        StringComparer.Ordinal);

    private void ReadFields(NoteBlock block) {
        for (var i = block.StartLine; i < block.EndLine && i < lines.Count; i++) {
            var match = AnyKey.Match(StripComment(lines[i]));
            if (!match.Success) {
                continue;
            }
            var key = match.Groups["key"].Value;
            if (!ScalarKeys.Contains(key)) {
                continue;
            }
            var value = match.Groups["value"].Value.Trim();
            switch (key) {
                case "position":
                    Num.TryParseInt(value, out block.Position);
                    break;
                case "duration":
                    Num.TryParseInt(value, out block.Duration);
                    break;
                case "tone":
                    Num.TryParseInt(value, out block.Tone);
                    break;
                case "lyric":
                    block.Lyric = BankReader.Unquote(value);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Replace the note's alt expressions with the given per-phoneme values.
    /// Returns the number of expressions written.
    ///
    /// Nothing is written yet: the request is only remembered, because every
    /// note's line numbers are the ones found by the last <see cref="Reindex"/>
    /// and stay valid only until the first line moves. The whole set of changes
    /// is applied in one pass by <see cref="Save"/>.
    /// </summary>
    public int SetAlternates(int noteIndex, IReadOnlyList<(int Index, int Alt)> values) {
        if (noteIndex < 0 || noteIndex >= notes.Count) {
            return 0;
        }
        var positive = values
            .Where(v => v.Alt > 0)
            .GroupBy(v => v.Index)
            .Select(g => g.First())
            .OrderBy(v => v.Index)
            .ToList();
        if (positive.Count == 0) {
            return 0;
        }
        pending[noteIndex] = positive;
        return positive.Count;
    }

    /// <summary>The blocks to write, per note, until they are applied.</summary>
    private readonly Dictionary<int, List<(int Index, int Alt)>> pending =
        new Dictionary<int, List<(int, int)>>();

    /// <summary>Apply every requested change and re-index the result.</summary>
    private void Write() {
        // Walk the notes in file order and adjust positions as they are consumed.
        // The changes are monotonic: a note's block is always at or below every
        // block already written, so a running offset is the whole of the
        // bookkeeping, and every index used below is a current one.
        var offset = 0;
        for (var n = 0; n < notes.Count; n++) {
            var note = notes[n];
            var span = ExpressionSpan(note, offset);
            var start = span.Start + offset;
            var have = span.Count > 0;
            var wanted = pending.TryGetValue(n, out var values) ? values : null;
            if (!have && wanted == null) {
                continue;
            }

            // The block's own entries are read back before it is dropped, so the
            // ones that are not "alt" — attack, decay and the like — survive.
            var kept = new List<string>();
            if (have) {
                kept = ReadEntries(lines, start, span.Count, keepAlts: false);
                for (var i = start + span.Count - 1; i >= start; i--) {
                    lines.RemoveAt(i);
                }
                offset -= span.Count;
            }

            var added = new List<string>();
            var pad = new string(' ', span.Indent);
            if (wanted != null || kept.Count > 0) {
                added.Add(pad + "phoneme_expressions:");
            }
            if (wanted != null) {
                foreach (var (index, alt) in wanted) {
                    added.Add(pad + "- index: " + Num.I(index));
                    added.Add(pad + "  abbr: alt");
                    added.Add(pad + "  value: " + Num.I(alt));
                }
            }
            added.AddRange(kept);
            if (added.Count > 0) {
                lines.InsertRange(start, added);
                offset += added.Count;
            }
        }
        pending.Clear();
        Reindex();
    }

    /// <summary>
    /// The entries a phoneme_expressions block holds, one expression's lines at a
    /// time, so a rewrite can hand back everything it did not replace.
    ///
    /// A block mixes the two styles OpenUtau writes: entries spanning their own
    /// lines, and flow entries such as "- {index: 0, abbr: alt, value: 1}". Both
    /// open a new entry, so an entry runs until the next one.
    /// </summary>
    private static List<string> ReadEntries(
        List<string> lines, int start, int count, bool keepAlts) {
        var result = new List<string>();
        var current = new List<string>();
        var abbr = "";
        void Flush() {
            if (current.Count > 0 && (keepAlts || abbr != "alt")) {
                result.AddRange(current);
            }
            current.Clear();
            abbr = "";
        }
        for (var i = start + 1; i < start + count && i < lines.Count; i++) {
            var text = lines[i];
            if (IsSequenceItem(text)) {
                Flush();
                // A flow entry names its expression on the same line, so the
                // abbr has to be read from there; a block entry names it on the
                // line after the "- index:" one.
                if (IsFlowEntry(text)) {
                    abbr = FlowAbbr(text);
                }
            }
            if (BlockKey(text, out var key)) {
                abbr = key;
            }
            current.Add(text);
        }
        Flush();
        return result;
    }

    /// <summary>The abbr a flow entry declares, such as "- {index: 0, abbr: alt}".</summary>
    private static string FlowAbbr(string line) {
        var match = Regex.Match(line, @"(?:^|[{,\s])abbr\s*:\s*(?<value>[^,}\s]+)");
        return match.Success ? BankReader.Unquote(match.Groups["value"].Value.Trim()) : "";
    }

    /// <summary>
    /// The "abbr:" value a line carries when it belongs to a block, if any.
    ///
    /// Used to tell an "alt" entry from every other expression, which is what
    /// makes rewriting a block non-destructive.
    /// </summary>
    private static bool BlockKey(string line, out string key) {
        key = "";
        var match = AnyKey.Match(StripComment(line));
        if (!match.Success || match.Groups["key"].Value != "abbr") {
            return false;
        }
        key = BankReader.Unquote(match.Groups["value"].Value.Trim());
        return true;
    }

    /// <summary>Where a note's expression block is, and at what column to write one.</summary>
    private readonly struct Span {
        public readonly int Start;
        public readonly int Count;
        public readonly int Indent;
        public Span(int start, int count, int indent) {
            Start = start;
            Count = count;
            Indent = indent;
        }
    }

    /// <summary>
    /// Find the note's existing expression block in the lines as they are now.
    ///
    /// The range searched is the note's extent shifted by everything applied so
    /// far, which is what keeps the search correct while earlier notes are
    /// rewritten above it.
    /// </summary>
    private Span ExpressionSpan(NoteBlock block, int offset) {
        for (var i = block.StartLine; i < block.EndLine; i++) {
            var at = i + offset;
            if (at < 0 || at >= lines.Count) {
                break;
            }
            var match = AnyKey.Match(StripComment(lines[at]));
            if (!match.Success || match.Groups["key"].Value != "phoneme_expressions") {
                continue;
            }
            // The key sits at the same column as the note's own fields, and a
            // nested list's key sits deeper, so this is what tells the note's
            // expressions from, say, a pitch curve's. OpenUtau writes the key at
            // the note's column; an older one wrote it two deeper, and both are
            // accepted so a project written either way re-runs cleanly.
            var keyIndent = match.Groups["indent"].Value.Length;
            if (keyIndent != block.Indent && keyIndent != block.Indent + 2) {
                continue;
            }
            // A block-style value is written as its own line, and a flow-style
            // one as a "[" on the key line with the entries either after it or
            // on the following lines. Both are removed; leaving the entries of a
            // flow sequence behind would put the replacement inside it.
            var flow = match.Groups["value"].Value.Trim().StartsWith("[", StringComparison.Ordinal);
            var count = 1;
            var end = Math.Min(block.EndLine + offset, lines.Count);
            for (var j = at + 1; j < end; j++) {
                if (lines[j].Trim().Length == 0) {
                    continue;
                }
                // A flow sequence's entries may be written at the same column as
                // the key that opens it, so they are recognized by their shape
                // rather than by their indentation. A block's items sit at the
                // key's column or deeper, and the note's next field is a key at
                // that column, which is where the block ends.
                if (flow) {
                    if (!IsFlowEntry(lines[j])) {
                        break;
                    }
                } else if (IndentOf(j) < keyIndent ||
                           (IndentOf(j) == keyIndent && !IsSequenceItem(lines[j]))) {
                    break;
                }
                count++;
            }
            return new Span(i, count, keyIndent);
        }
        // No block yet, so one goes at the end of the note. The end of the line
        // list is one past the last line and the last note in a part runs to the
        // end of the file, so it is kept inside the list.
        return new Span(Math.Min(block.EndLine, lines.Count), 0, block.Indent + 2);
    }

    public void Save(string target) {
        Write();
        var text = string.Join("\r\n", lines) + "\r\n";
        var temp = target + ".altsetter.tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, target, true);
    }

    public string Path => path;

    // ---------------------------------------------------------------- helpers

    private int IndentOf(int line) {
        if (line < 0 || line >= lines.Count) {
            return 0;
        }
        var text = lines[line];
        var i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) {
            i++;
        }
        return i;
    }

    /// <summary>Whether a line is a YAML sequence item ("- key: value").</summary>
    private static bool IsSequenceItem(string line) {
        var text = line.TrimStart();
        return text.Length > 1 && text[0] == '-' &&
               (text[1] == ' ' || text[1] == '\t');
    }

    /// <summary>
    /// The column where a line's content begins, with a sequence marker counted
    /// as content rather than indentation.
    ///
    /// This matters because voice_parts is a list at column 0: its items have no
    /// leading whitespace at all, so measuring raw indentation makes a part's
    /// "duration:" look like a new top-level key and the search for notes: walk
    /// straight past it.
    /// </summary>
    private int ContentIndentOf(int line) {
        var indent = IndentOf(line);
        return IsSequenceItem(lines[line]) ? indent + 2 : indent;
    }

    /// <summary>
    /// A USTX comment only starts at a token boundary, so a lyric or path that
    /// contains "#" survives.
    /// </summary>
    private static string StripComment(string line) {
        for (var i = 0; i < line.Length; i++) {
            if (line[i] == '#' && (i == 0 || line[i - 1] == ' ' || line[i - 1] == '\t')) {
                return line.Substring(0, i);
            }
        }
        return line;
    }

    private static IEnumerable<string> SplitLines(string text) {
        var start = 0;
        for (var i = 0; i < text.Length; i++) {
            if (text[i] == '\n') {
                var end = i;
                if (end > start && text[end - 1] == '\r') {
                    end--;
                }
                yield return text.Substring(start, end - start);
                start = i + 1;
            }
        }
        if (start < text.Length) {
            yield return text.Substring(start);
        }
    }
}
