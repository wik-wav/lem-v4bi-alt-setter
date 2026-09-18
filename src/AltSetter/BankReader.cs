using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AltSetter;

internal sealed class Subbank {
    public string Color = "";
    public string Prefix = "";
    public string Suffix = "";
    public List<string> ToneRanges = new List<string>();
}

internal sealed class Bank {
    public string Root = "";
    public List<string> PitchFolders = new List<string>();
    public string PrimaryFolder = "";
    public List<Subbank> Subbanks = new List<Subbank>();
    public HashSet<string> Prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phone spellings the bank actually uses, learned from its aliases.</summary>
    public HashSet<string> KnownPhones = new HashSet<string>(StringComparer.Ordinal);
    public Dictionary<string, Diphone> Diphones =
        new Dictionary<string, Diphone>(StringComparer.Ordinal);
    public List<string> Warnings = new List<string>();

    /// <summary>Phone symbols that actually appear as a transition's left phone.</summary>
    public HashSet<string> KnownLeftPhones = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Declared subbank affixes, longest first so "A3H" wins over "A3".</summary>
    public IEnumerable<string> SuffixesLongestFirst =>
        Suffixes.OrderByDescending(s => s.Length);

    public IEnumerable<string> PrefixesLongestFirst =>
        Prefixes.OrderByDescending(s => s.Length);

    public Diphone Find(string left, string right) {
        Diphones.TryGetValue(left + "-" + right, out var diphone);
        return diphone;
    }

    /// <summary>
    /// The subbank a note at this tone is sung into, or null when the bank
    /// declares no range for it.
    ///
    /// This is OpenUtau's own rule: the first subbank whose tone range contains
    /// the note's tone, compared as MIDI numbers. It decides which suffix an
    /// alias carries, so a plugin that places alt values has to know it.
    /// </summary>
    public Subbank SubbankFor(int tone) {
        foreach (var subbank in Subbanks) {
            foreach (var range in subbank.ToneRanges) {
                if (ToneRange.Contains(range, tone)) {
                    return subbank;
                }
            }
        }
        return null;
    }

    /// <summary>The prefix and suffix an alias at this tone carries.</summary>
    public (string Prefix, string Suffix) AffixesFor(int tone) {
        var subbank = SubbankFor(tone);
        return subbank == null ? ("", "") : (subbank.Prefix ?? "", subbank.Suffix ?? "");
    }
}

/// <summary>"C1-C3" as a pair of MIDI note numbers, inclusive.</summary>
internal readonly struct ToneRange {
    private readonly int low;
    private readonly int high;

    private ToneRange(int low, int high) {
        this.low = low;
        this.high = high;
    }

    public static bool Contains(string text, int tone) {
        var range = Parse(text);
        return range.high > 0 && tone >= range.low && tone <= range.high;
    }

    /// <summary>Whether a "C1-C3" range covers a tone, for callers checking overlap.</summary>
    public static bool Covers(string text, int tone) => Contains(text, tone);

    private static ToneRange Parse(string text) {
        var parts = (text ?? "").Split('-');
        if (parts.Length != 2) {
            return default;
        }
        return new ToneRange(Pitch(parts[0]), Pitch(parts[1]));
    }

    /// <summary>A tone name such as "C#4" or "F#3" as a MIDI number.</summary>
    private static int Pitch(string name) {
        var text = (name ?? "").Trim();
        if (text.Length < 2) {
            return -1;
        }
        var letter = char.ToUpperInvariant(text[0]);
        var index = "C D EF G A B".IndexOf(letter);
        if (index < 0) {
            return -1;
        }
        var semitone = new[] { 0, 2, 4, 5, 7, 9, 11 }[index / 2];
        var at = 1;
        if (at < text.Length && (text[at] == '#' || text[at] == 'b')) {
            semitone += text[at] == '#' ? 1 : -1;
            at++;
        }
        if (!int.TryParse(text.Substring(at), out var octave)) {
            return -1;
        }
        return semitone + (octave + 1) * 12;
    }
}

/// <summary>
/// Reads a classic UTAU multi-pitch voicebank and rebuilds the directional
/// recording context that a FestVox-style selector needs.
///
/// Nothing here guesses from WAV file names: evidence comes only from oto.ini
/// aliases and their time order inside one recording, exactly like the
/// reference implementation this was ported from.
/// </summary>
internal static class BankReader {
    /// <summary>
    /// How deep to look for oto.ini files below the bank root.
    ///
    /// Multi-pitch banks keep their oto.ini in the pitch folders directly under
    /// the root, so two levels is enough. Bounding it matters: an unbounded
    /// search from a folder that is not a bank would walk an entire drive, which
    /// is both slow and a way to mistake a folder of banks for one bank.
    /// </summary>
    public const int DefaultMaxDepth = 2;

    public static Bank Load(string bankRoot) => Load(bankRoot, DefaultMaxDepth);

    public static Bank Load(string bankRoot, int maxDepth) {
        var bank = new Bank { Root = Path.GetFullPath(bankRoot) };
        if (!Directory.Exists(bank.Root)) {
            throw new DirectoryNotFoundException("Voicebank folder not found: " + bank.Root);
        }

        bank.Subbanks = ParseCharacterYaml(Path.Combine(bank.Root, "character.yaml"), bank);
        foreach (var subbank in bank.Subbanks) {
            if (subbank.Prefix.Length > 0) {
                bank.Prefixes.Add(subbank.Prefix);
            }
            if (subbank.Suffix.Length > 0) {
                bank.Suffixes.Add(subbank.Suffix);
            }
        }

        var folders = FindOtoFolders(bank.Root, maxDepth);
        if (folders.Count == 0) {
            throw new FileNotFoundException("No oto.ini found under " + bank.Root);
        }
        bank.PitchFolders = folders.Select(f => Relative(bank.Root, f)).ToList();

        // Folders that actually contribute usable diphone rows, ordered with
        // the shallowest and alphabetically first folder first so the choice
        // is deterministic across machines. A zero-byte root oto.ini, which
        // multi-pitch banks often keep, simply drops out here.
        var usable = new List<(string Path, List<RawOto> Rows)>();
        foreach (var folder in folders) {
            var rows = ParseOto(Path.Combine(folder, "oto.ini"), bank);
            if (rows.Count > 0) {
                usable.Add((folder, rows));
            }
        }
        if (usable.Count == 0) {
            throw new InvalidDataException("Every oto.ini in " + bank.Root + " was empty.");
        }
        bank.PitchFolders = usable.Select(u => Relative(bank.Root, u.Path)).ToList();

        var referenceFolder = usable[0].Path;
        bank.PrimaryFolder = Relative(bank.Root, referenceFolder);
        var referenceRows = usable[0].Rows;

        // Alias parsing is ambiguous on its own: "aa1E3" is phone aa, take 1,
        // pitch E3; "dza" is phone dz, pitch a. Collecting the phone vocabulary
        // first lets the parser check its work against what the bank really
        // contains instead of guessing.
        bank.KnownPhones = CollectPhones(usable, bank);

        var diphones = BuildDiphones(referenceRows, Relative(bank.Root, referenceFolder), bank);

        // Cross-pitch availability: an alt number only reaches the singer if
        // every pitch folder that has the diphone also has that take, because
        // OpenUtau resolves aliases through one merged alias map.
        TrimToUniversallyAvailable(diphones, usable, bank, bank.Warnings);

        bank.Diphones = diphones;

        // The vocabulary is finished off from the diphones, whose left side is the
        // whole phone: "ay l" for a transition from "ay" into "l", and "- m" for
        // the silence at the start of a phrase. The parser alone cannot know those,
        // because it only ever needs the right phone of each alias, and OpenUtau
        // writes them into the phoneme list it hands a plugin.
        foreach (var diphone in diphones.Values) {
            if (diphone.Left.Length > 0) {
                bank.KnownLeftPhones.Add(diphone.Left);
                bank.KnownPhones.Add(diphone.Left);
                bank.KnownPhones.Add(LastWord(diphone.Left));
            }
            if (diphone.Right.Length > 0) {
                bank.KnownPhones.Add(diphone.Right);
            }
            // OpenUtau writes the phone before as part of the alias, so the phones
            // it hands a plugin are two words: "l ah" for the transition out of
            // "l", and "- m" where a phrase begins. Every such pair the bank
            // records is a spelling a phoneme can arrive with, and the vocabulary
            // has to hold it or the alias cannot be read back.
            if (diphone.Left.Length > 0 && diphone.Right.Length > 0) {
                bank.KnownPhones.Add(LastWord(diphone.Left) + " " + diphone.Right);
            }
        }
        return bank;
    }

    private static string Relative(string root, string path) {
        var rel = Path.GetRelativePath(root, path);
        return rel == "." ? "" : rel.Replace('\\', '/');
    }

    /// <summary>
    /// Every folder at or below the root that holds an oto.ini, limited to
    /// <paramref name="maxDepth"/> levels. Unreadable folders are skipped rather
    /// than failing the whole bank.
    /// </summary>
    private static List<string> FindOtoFolders(string root, int maxDepth) {
        var found = new List<string>();
        void Walk(string folder, int depth) {
            try {
                if (File.Exists(Path.Combine(folder, "oto.ini"))) {
                    found.Add(folder);
                }
            } catch (Exception) {
                return;
            }
            if (depth >= maxDepth) {
                return;
            }
            try {
                foreach (var child in Directory.EnumerateDirectories(folder)) {
                    Walk(child, depth + 1);
                }
            } catch (Exception) {
                // An unreadable subfolder is not a reason to give up on the bank.
            }
        }
        Walk(root, 0);
        return found.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ oto

    private sealed class RawOto {
        public string Alias = "";
        public string Wav = "";
        public double Offset;
        public double Consonant;
        public double Cutoff;
        public double Preutter;
        public double Overlap;
        public int Line;
        public string OtoFile = "";
        public double Mid => Offset + Preutter;
    }

    private static List<RawOto> ParseOto(string path, Bank bank) {
        var rows = new List<RawOto>();
        var label = Path.GetFileName(Path.GetDirectoryName(path) ?? path);
        var lineNumber = 0;
        foreach (var line in ReadAllLines(path)) {
            lineNumber++;
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#' || text[0] == ';') {
                continue;
            }
            var eq = text.IndexOf('=');
            if (eq <= 0) {
                continue;
            }
            var wav = text.Substring(0, eq).Trim();
            var parts = text.Substring(eq + 1).Split(',');
            if (parts.Length < 6) {
                continue;
            }
            if (!Num.TryParse(parts[1].Trim(), out var offset) ||
                !Num.TryParse(parts[2].Trim(), out var consonant) ||
                !Num.TryParse(parts[3].Trim(), out var cutoff) ||
                !Num.TryParse(parts[4].Trim(), out var preutter) ||
                !Num.TryParse(parts[5].Trim(), out var overlap)) {
                continue;
            }
            rows.Add(new RawOto {
                Alias = parts[0].Trim(),
                Wav = wav,
                Offset = offset,
                Consonant = consonant,
                Cutoff = cutoff,
                Preutter = preutter,
                Overlap = overlap,
                OtoFile = label,
                Line = lineNumber,
            });
        }
        _ = bank;
        return rows;
    }

    private static IEnumerable<string> ReadAllLines(string path) {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, TextEnc.ShiftJis, true);
        string line;
        while ((line = reader.ReadLine()) != null) {
            yield return line;
        }
    }

    // -------------------------------------------------------------- diphones

    private static Dictionary<string, Diphone> BuildDiphones(
        List<RawOto> rows, string otoLabel, Bank bank) {
        var units = new List<Take>();
        foreach (var row in rows) {
            var split = SplitAlias(row.Alias, bank);
            if (split == null) {
                continue;
            }
            units.Add(new Take {
                Number = split.Value.Take,
                Alias = row.Alias,
                Wav = row.Wav,
                OtoFile = otoLabel,
                OtoLine = row.Line,
                OffsetMs = row.Offset,
                PreutterMs = row.Preutter,
                Left = split.Value.Left,
                Right = split.Value.Right,
            });
        }

        // Directional outer context from the adjacent, time-ordered
        // transitions of the same recording.
        var byWav = units.GroupBy(u => u.Wav, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byWav) {
            var ordered = group.OrderBy(u => u.OffsetMs + u.PreutterMs)
                .ThenBy(u => u.OtoLine)
                .ToList();
            for (var position = 0; position < ordered.Count; position++) {
                var unit = ordered[position];

                Take previous = null;
                for (var i = position - 1; i >= 0; i--) {
                    if (ordered[i].OffsetMs + ordered[i].PreutterMs < unit.OffsetMs + unit.PreutterMs - 0.001) {
                        previous = ordered[i];
                        break;
                    }
                }
                Take following = null;
                for (var i = position + 1; i < ordered.Count; i++) {
                    if (ordered[i].OffsetMs + ordered[i].PreutterMs > unit.OffsetMs + unit.PreutterMs + 0.001) {
                        following = ordered[i];
                        break;
                    }
                }

                var previousChains = previous != null &&
                    string.Equals(previous.Right, unit.Left, StringComparison.Ordinal);
                var followingChains = following != null &&
                    string.Equals(following.Left, unit.Right, StringComparison.Ordinal);

                if (previous != null) {
                    unit.LeftContext = previousChains ? previous.Left : previous.Right;
                    unit.LeftContextSource = previousChains
                        ? "adjacent_transition"
                        : "adjacent_oto_edge";
                } else {
                    unit.LeftContext = "*";
                    unit.LeftContextSource = "unavailable";
                }
                if (following != null) {
                    unit.RightContext = followingChains ? following.Right : following.Left;
                    unit.RightContextSource = followingChains
                        ? "adjacent_transition"
                        : "adjacent_oto_edge";
                } else {
                    unit.RightContext = "*";
                    unit.RightContextSource = "unavailable";
                }
            }
        }

        var grouped = new Dictionary<string, List<Take>>(StringComparer.Ordinal);
        foreach (var unit in units) {
            var key = unit.Left + "-" + unit.Right;
            if (!grouped.TryGetValue(key, out var list)) {
                grouped[key] = list = new List<Take>();
            }
            list.Add(unit);
        }

        var result = new Dictionary<string, Diphone>(StringComparer.Ordinal);
        foreach (var pair in grouped) {
            var ordered = pair.Value
                .OrderBy(t => t.Number)
                .ThenBy(t => t.Alias, StringComparer.Ordinal)
                .ThenBy(t => t.OtoLine)
                .ToList();
            var diphone = new Diphone {
                Left = ordered[0].Left,
                Right = ordered[0].Right,
            };
            var seenNumbers = new HashSet<int>();
            foreach (var take in ordered) {
                if (!seenNumbers.Add(take.Number)) {
                    continue; // duplicated alias row inside one oto.ini
                }
                take.LClass = LightDarkClass(take.Left, take.Right, take.RightContext);
                diphone.Takes.Add(take);
            }
            if (!result.ContainsKey(diphone.Key)) {
                result[diphone.Key] = diphone;
            }
        }
        return result;
    }

    private static string LightDarkClass(string left, string right, string outerRight) {
        var lightFollowers = new HashSet<string>(PhoneClass.Vowels, StringComparer.Ordinal) { "y" };
        var l = PhoneClass.StripTakeSuffix(left);
        var r = PhoneClass.StripTakeSuffix(right);
        if (l == "l") {
            return lightFollowers.Contains(r) ? "light" : "dark";
        }
        if (r == "l") {
            return lightFollowers.Contains(PhoneClass.StripTakeSuffix(outerRight))
                ? "light" : "dark";
        }
        return "*";
    }

    /// <summary>
    /// Drop any take number that is missing from a pitch folder which owns the
    /// diphone, so a written alt can never resolve to something OpenUtau cannot
    /// find. Folder-local recording gaps usually leave only a handful of takes
    /// affected and the base take always survives.
    /// </summary>
    private static void TrimToUniversallyAvailable(
        Dictionary<string, Diphone> reference,
        List<(string Path, List<RawOto> Rows)> folders,
        Bank bank,
        List<string> warnings) {
        var availability = new List<Dictionary<string, HashSet<int>>>();
        foreach (var (path, rows) in folders) {
            var map = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var row in rows) {
                var split = SplitAlias(row.Alias, bank);
                if (split == null) {
                    continue;
                }
                var key = split.Value.Left + "-" + split.Value.Right;
                if (!map.TryGetValue(key, out var set)) {
                    map[key] = set = new HashSet<int>();
                }
                set.Add(split.Value.Take);
            }
            availability.Add(map);
        }

        var dropped = 0;
        foreach (var diphone in reference.Values) {
            var keep = new List<Take>();
            foreach (var take in diphone.Takes) {
                var everywhere = true;
                foreach (var map in availability) {
                    // Folders that do not record this diphone at all are fine:
                    // OpenUtau cannot select them for it.
                    if (!map.TryGetValue(diphone.Key, out var numbers)) {
                        continue;
                    }
                    if (!numbers.Contains(take.Number)) {
                        everywhere = false;
                        break;
                    }
                }
                if (everywhere) {
                    keep.Add(take);
                } else {
                    dropped++;
                }
            }
            if (keep.Count == 0) {
                continue;
            }
            // The unnumbered base take must stay first: OpenUtau uses it when
            // alt is 0, and it is the anchor all other numbering is relative to.
            keep = keep.OrderBy(t => t.Number).ToList();
            if (keep.Count != diphone.Takes.Count) {
                diphone.Takes = keep;
            }
        }
        if (dropped > 0) {
            warnings.Add(
                $"{dropped} take(s) were dropped because the pitch folders do " +
                "not all contain them; remaining takes are valid in every pitch.");
        }
    }

    // ------------------------------------------------------------ alias parse

    public readonly struct AliasSplit {
        public readonly string Left;
        public readonly string Right;
        public readonly int Take;
        public readonly string PitchTag;
        public AliasSplit(string left, string right, int take, string pitchTag) {
            Left = left;
            Right = right;
            Take = take;
            PitchTag = pitchTag;
        }
    }

    /// <summary>
    /// Learn the bank's phone vocabulary from its aliases. Aliases that can be
    /// read without a take number are trusted as-is; numbered ones only
    /// contribute a phone when the stem is already known. That two-pass shape
    /// is what makes the later per-row decision checkable rather than a guess.
    /// </summary>
    private static HashSet<string> CollectPhones(
        List<(string Path, List<RawOto> Rows)> folders, Bank bank) {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string phone) {
            if (phone.Length == 0 || char.IsDigit(phone[0])) {
                return;
            }
            counts[phone] = counts.TryGetValue(phone, out var n) ? n + 1 : 1;
        }
        foreach (var (_, rows) in folders) {
            foreach (var row in rows) {
                // The alias is read with the bank's own parser, which is what the
                // rest of the tool uses, so the vocabulary learned here cannot
                // disagree with how every other alias is later understood. Slicing
                // the string here instead is how "ay l1A2" became the phones "ay"
                // and "l1A2" and left "ay l" unlearned.
                var split = SplitAlias(row.Alias, bank);
                if (split == null) {
                    continue;
                }
                var alias = split.Value;
                if (alias.Left.Length > 0) {
                    Add(alias.Left);
                    // A left side can be two phones, as in "ay l": learning the
                    // last word of it too is what makes "ay lPA3" readable.
                    Add(LastWord(alias.Left));
                }
                if (alias.Right.Length == 0 || LooksLikePitchTag(alias.Right)) {
                    continue;
                }
                Add(alias.Right);
                var phone = StripPitchTag(alias.Right);
                if (phone.Length > 0 && !EndsWithDigit(phone)) {
                    Add(phone);
                }
            }
        }

        // A token that is another token plus digits is a numbered pronunciation,
        // not a phone of its own: "eh2" is the second take of "eh". Left in, the
        // plugin reads OpenUtau's "eh2PA3" back as the phone "eh2", which the
        // bank has no recording for, and the whole note is thrown away.
        var named = new HashSet<string>(counts.Keys, StringComparer.Ordinal);
        var phones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in counts) {
            var phone = pair.Key;
            var stem = phone.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (stem.Length > 0 && stem != phone && named.Contains(stem)) {
                phone = stem;
            }
            if (pair.Value >= 2 || named.Contains(phone)) {
                phones.Add(phone);
            }
        }
        return phones;
    }

    /// <summary>The last word of a left side, which may name a phone of its own.</summary>
    private static string LastWord(string text) {
        var space = text.LastIndexOf(' ');
        return space < 0 ? text : text.Substring(space + 1);
    }

    private static string StripAffixes(string rawAlias, Bank bank) {
        var alias = (rawAlias ?? "").Trim().ToLowerInvariant();
        if (bank == null) {
            return alias;
        }
        foreach (var prefix in bank.PrefixesLongestFirst) {
            var lowered = prefix.ToLowerInvariant();
            if (alias.Length > lowered.Length &&
                alias.StartsWith(lowered, StringComparison.Ordinal)) {
                alias = alias.Substring(lowered.Length).Trim();
                break;
            }
        }
        foreach (var suffix in bank.SuffixesLongestFirst) {
            var lowered = suffix.ToLowerInvariant();
            if (alias.Length > lowered.Length &&
                alias.EndsWith(lowered, StringComparison.Ordinal)) {
                alias = alias.Substring(0, alias.Length - lowered.Length).Trim();
                break;
            }
        }
        return alias;
    }

    /// <summary>
    /// Split an oto alias into the transition it records. Handles the bank's
    /// "left right" form with an optional recording-take digit and the UTAU /
    /// OpenUtau pitch tag, plus the subbank affixes declared in character.yaml
    /// ("ay b1E3", "ayA3 b1", "- aa3A2", "iy dh1A2").
    /// </summary>
    public static AliasSplit? SplitAlias(string rawAlias, Bank bank) {
        var alias = StripAffixes(rawAlias, bank);
        if (alias.Length == 0) {
            return null;
        }
        var separator = alias.LastIndexOf(' ');
        if (separator <= 0) {
            return null;
        }
        var left = alias.Substring(0, separator).Trim();
        var right = alias.Substring(separator + 1).Trim();
        // A take number glued to the *left* phone means the alias carried a
        // pitch tag in the middle ("dh1A2"), which is not this bank's shape.
        if (left.Length == 0 || right.Length == 0 || EndsWithDigit(left)) {
            return null;
        }

        var pitchTag = "";
        string phone;
        var take = 0;
        if (EndsWithDigit(right)) {
            var digits = right.Length;
            while (digits > 0 && char.IsDigit(right[digits - 1])) {
                digits--;
            }
            var stem = right.Substring(0, digits);
            if (digits > 0 &&
                Num.TryParseInt(right.Substring(digits), out var parsed) &&
                (bank == null || bank.KnownPhones.Count == 0 ||
                 bank.KnownPhones.Contains(stem))) {
                take = parsed;
                phone = stem;
            } else {
                // The digit belonged to the pitch tag, not a take number.
                phone = StripPitchTag(right);
                if (phone.Length > 0 && phone != right) {
                    pitchTag = right.Substring(phone.Length);
                } else {
                    phone = right;
                }
            }
        } else {
            phone = StripPitchTag(right);
            if (phone.Length > 0 && phone != right) {
                pitchTag = right.Substring(phone.Length);
            } else {
                phone = right;
            }
        }
        if (phone.Length == 0) {
            return null;
        }
        return new AliasSplit(left, phone, take, pitchTag);
    }

    private static bool EndsWithDigit(string text) =>
        text.Length > 0 && char.IsDigit(text[text.Length - 1]);

    /// <summary>
    /// Remove the leading letters needed so the remainder ends with the pitch
    /// tag pattern, e.g. "dza" -&gt; "dz" for the tag "a", "aa3e3" -&gt; "aa3"
    /// for the tag "e3". The leading letters are recognised by shape
    /// (letters then an optional accidental then digits), not by a fixed list,
    /// so any UTAU pitch tag works.
    /// </summary>
    public static string StripPitchTag(string text) {
        for (var start = 0; start < text.Length; start++) {
            if (char.IsDigit(text[start])) {
                continue;
            }
            var rest = text.Substring(start);
            if (start > 0 && LooksLikePitchTag(rest)) {
                return text.Substring(0, start);
            }
        }
        return text;
    }

    private static bool LooksLikePitchTag(string text) {
        if (text.Length < 2 || !char.IsLetter(text[0])) {
            return false;
        }
        // UTAU pitch tags start on a note name, so the accidental (if any) has
        // to follow the letter immediately.
        var index = 1;
        if (index < text.Length && (text[index] == '#' || text[index] == 'b')) {
            index++;
        }
        var digitsStart = index;
        while (index < text.Length && char.IsDigit(text[index])) {
            index++;
        }
        if (index == digitsStart) {
            return false;
        }
        // A trailing "H" (as in "A3H") marks OpenUtau's head-voice subbank.
        if (index < text.Length && (text[index] == 'h' || text[index] == 'H')) {
            index++;
        }
        return index == text.Length;
    }

    private static List<Subbank> ParseCharacterYaml(string path, Bank bank) {
        var subbanks = new List<Subbank>();
        if (!File.Exists(path)) {
            return subbanks;
        }
        var lines = File.ReadAllLines(path);
        var inSubbanks = false;
        Subbank current = null;
        var inToneRanges = false;
        foreach (var raw in lines) {
            var line = StripYamlComment(raw);
            if (line.Trim().Length == 0) {
                continue;
            }
            var indent = line.Length - line.TrimStart().Length;
            // YAML writes padding before a comment, so "tone_ranges:  " has to be
            // recognized as the key it is.
            var text = line.Trim().TrimEnd();
            if (!inSubbanks) {
                if (text.TrimEnd() == "subbanks:") {
                    inSubbanks = true;
                }
                continue;
            }
            if (indent == 0 && !text.StartsWith("-", StringComparison.Ordinal)) {
                inSubbanks = false;
                inToneRanges = false;
                continue;
            }
            if (text.StartsWith("- ", StringComparison.Ordinal)) {
                // The subbank list and the tone_ranges list under it both write
                // their items as "- ...", and a bank may write a range at the same
                // column as the key that opens it, so neither column nor order
                // alone tells them apart. What does: an item that declares a
                // subbank field is a subbank, and any other item is a range.
                // Reading a subbank as a range is silent — the ranges come out
                // empty and nothing can then be said about a tone's suffix.
                var item = text.Substring(2).Trim();
                if (inToneRanges && !DeclaresSubbank(item)) {
                    current.ToneRanges.Add(Unquote(item));
                    continue;
                }
                current = new Subbank();
                subbanks.Add(current);
                inToneRanges = false;
                ReadKeyValue(item, current);
                continue;
            }
            if (current == null) {
                continue;
            }
            if (text == "tone_ranges:") {
                inToneRanges = true;
                continue;
            }
            inToneRanges = false;
            ReadKeyValue(text, current);
        }
        return subbanks;
    }

    /// <summary>Whether a list item opens a subbank rather than naming a range.</summary>
    private static bool DeclaresSubbank(string item) {
        var key = item;
        var colon = key.IndexOf(':');
        if (colon > 0) {
            key = key.Substring(0, colon);
        }
        switch (key.Trim().ToLowerInvariant()) {
            case "color":
            case "prefix":
            case "suffix":
            case "tone_ranges":
                return true;
            default:
                return false;
        }
    }

    private static void ReadKeyValue(string text, Subbank subbank) {
        var colon = text.IndexOf(':');
        if (colon <= 0) {
            return;
        }
        var key = text.Substring(0, colon).Trim().ToLowerInvariant();
        var value = Unquote(text.Substring(colon + 1).Trim());
        switch (key) {
            case "color": subbank.Color = value; break;
            case "prefix": subbank.Prefix = value; break;
            case "suffix": subbank.Suffix = value; break;
        }
    }

    /// <summary>
    /// Strip a YAML comment, which only starts at a token boundary. Inside a
    /// quoted scalar a "#" is literal, so a value such as "PF#3" survives.
    /// </summary>
    public static string StripYamlComment(string line) {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++) {
            var c = line[i];
            if (quote != '\0') {
                if (c == quote) {
                    quote = '\0';
                }
                continue;
            }
            if (c == '"' || c == '\'') {
                quote = c;
                continue;
            }
            if (c == '#' && (i == 0 || line[i - 1] == ' ' || line[i - 1] == '\t')) {
                return line.Substring(0, i);
            }
        }
        return line;
    }

    public static string Unquote(string value) {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[value.Length - 1] == '"') ||
             (value[0] == '\'' && value[value.Length - 1] == '\''))) {
            return value.Substring(1, value.Length - 2);
        }
        return value;
    }
}
