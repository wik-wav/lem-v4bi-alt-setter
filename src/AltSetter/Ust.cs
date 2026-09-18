using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AltSetter;

/// <summary>One note block of the UST file OpenUtau hands to a legacy plugin.</summary>
internal sealed class UstNote {
    public string Header = "";
    public int Length;
    public int NoteNum;
    public string Lyric = "";
    public int LyricLine = -1;
    public int Index;                 // index within the ordered note sequence
    public bool IsRest;               // the synthetic R notes OpenUtau inserts
    public bool IsNeighbour;          // [#PREV] / [#NEXT]

    /// <summary>
    /// "+" and "-" notes extend the previous syllable instead of starting a
    /// new one. A "-" only counts when it stands alone, so a word like
    /// "keepin-" is not mistaken for an extender.
    /// </summary>
    public bool IsExtender =>
        Lyric.StartsWith("+", StringComparison.Ordinal) ||
        Lyric == "-";
}

/// <summary>
/// The legacy plugin contract is a Shift_JIS UST file whose path arrives as
/// argv[0]. Only the note blocks matter: OpenUtau maps them back onto the
/// project's notes by order, and it rewrites every rebuilt note wholesale, so
/// leaving the rest of the file byte-identical is both safe and sufficient.
/// </summary>
internal sealed class UstDoc {
    public List<string> Lines = new List<string>();
    private readonly List<UstNote> notes = new List<UstNote>();
    private readonly Dictionary<int, UstNote> byLyricLine = new Dictionary<int, UstNote>();

    public IReadOnlyList<UstNote> Notes => notes;
    public IReadOnlyList<UstNote> RealNotes =>
        notes.Where(n => !n.IsRest && !n.IsNeighbour).ToList();

    /// <summary>Whether any note block was recognised at all.</summary>
    public bool LooksLikeUst => notes.Count > 0 || Lines.Any(
        line => line.TrimStart().StartsWith("[#", StringComparison.Ordinal));

    public static UstDoc Parse(string path) {
        var text = TextEnc.Decode(File.ReadAllBytes(path));
        return ParseFromLines(SplitLines(text));
    }

    public static UstDoc ParseFromLines(IEnumerable<string> sourceLines) {
        var doc = new UstDoc();
        doc.Lines.AddRange(sourceLines);

        UstNote current = null;
        var index = 0;
        for (var i = 0; i < doc.Lines.Count; i++) {
            var line = doc.Lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) {
                continue;
            }
            if (trimmed[0] == '[' && trimmed.EndsWith("]", StringComparison.Ordinal)) {
                var header = trimmed;
                current = null;
                if (header == "[#PREV]" || header == "[#NEXT]") {
                    current = new UstNote { Header = header, IsNeighbour = true, Index = -1 };
                    doc.notes.Add(current);
                } else if (header.Length > 3 &&
                           int.TryParse(header.Substring(2, header.Length - 3), out _)) {
                    current = new UstNote { Header = header, Index = index++ };
                    doc.notes.Add(current);
                }
                continue;
            }
            if (current == null) {
                continue;
            }
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) {
                continue;
            }
            var key = trimmed.Substring(0, eq).Trim().ToLowerInvariant();
            var value = trimmed.Substring(eq + 1).Trim();
            switch (key) {
                case "length":
                    Num.TryParseInt(value, out current.Length);
                    break;
                case "notenum":
                    Num.TryParseInt(value, out current.NoteNum);
                    break;
                case "lyric":
                    current.Lyric = value;
                    current.LyricLine = i;
                    doc.byLyricLine[i] = current;
                    break;
            }
        }

        foreach (var note in doc.notes) {
            if (note.IsNeighbour) {
                continue;
            }
            note.IsRest = string.Equals(note.Lyric, "R", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(note.Lyric, "r", StringComparison.Ordinal);
        }
        return doc;
    }

    public UstNote NoteAtLyricLine(int line) =>
        byLyricLine.TryGetValue(line, out var note) ? note : null;

    public void SetLyric(UstNote note, string lyric) {
        if (note.LyricLine < 0) {
            return;
        }
        note.Lyric = lyric;
#if NET
        Lines[note.LyricLine] = "Lyric=" + lyric;
#else
        Lines[note.LyricLine] = "Lyric=" + lyric;
#endif
    }

    public void Save(string path, Encoding encoding) {
        var text = string.Join("\r\n", Lines) + "\r\n";
        File.WriteAllBytes(path, encoding.GetBytes(text));
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
