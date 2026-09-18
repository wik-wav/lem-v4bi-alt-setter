using System;
using System.Collections.Generic;

namespace AltSetter;

/// <summary>
/// Lines a note's own reading of its lyrics up with the phoneme list OpenUtau
/// will actually ask the resampler for.
///
/// The two only match when the bank's phonemizer and this tool's built-in
/// dictionary agree. ArpasingPlus banks ship their own dictionary, and for words
/// the built-in one reads differently — "hallelujah" is seven phones there and
/// eight here — the counts differ while nearly every phone still lines up. An
/// alt expression is indexed by OpenUtau's list, so the reading has to be mapped
/// onto it; skipping the note throws away a decision that is almost entirely
/// right.
/// </summary>
internal static class PhoneAlign {
    /// <summary>What one aligned pair of phones is worth.</summary>
    private static int Score(string mine, string theirs) {
        if (string.Equals(mine, theirs, StringComparison.Ordinal)) {
            return 2;
        }
        var a = PhoneClass.Of(mine);
        var b = PhoneClass.Of(theirs);
        if (a.Length > 0 && a == b) {
            // Sounding the same kind of noise in the same place is close enough
            // to place a value: "ah" against "ax", or "rr" against "r".
            return 1;
        }
        return -3;
    }

    /// <summary>
    /// Map each of this tool's phones onto the index of the phone OpenUtau uses
    /// at the same place, or return null when the two readings are too far apart
    /// to be trusted.
    /// </summary>
    public static int[] Map(IReadOnlyList<string> mine, IReadOnlyList<string> theirs) {
        if (mine.Count == 0 || theirs.Count == 0) {
            return null;
        }
        // A window entry is an alias, not a bare phone: it names the phone before
        // and the phone it starts, "ay l" for the "l" after "ay". What this tool
        // reads is the bare phone, so the name at the end of the entry is the one
        // to compare with — comparing whole entries is comparing "l" with "ay l"
        // and never matches anything.
        var sound = new string[theirs.Count];
        for (var j = 0; j < theirs.Count; j++) {
            sound[j] = LastPhone(theirs[j]);
        }

        // The two readings are the same phones in the same order, with a few
        // entries that only one of them has: the window carries the phone before
        // the note and the next note's first phone, and the bank's phonemizer may
        // read a word slightly differently from this tool's dictionary. So the
        // pairing is the longest run of phones common to both, in order, and a
        // phone with no counterpart is left without an index rather than paired
        // with a different sound to avoid the gap.
        var table = new int[mine.Count + 1, theirs.Count + 1];
        for (var i = mine.Count - 1; i >= 0; i--) {
            for (var j = theirs.Count - 1; j >= 0; j--) {
                table[i, j] = Same(mine[i], sound[j])
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var mapped = new int[mine.Count];
        for (var k = 0; k < mapped.Length; k++) {
            mapped[k] = -1;
        }
        var placed = 0;
        for (int a = 0, b = 0; a < mine.Count && b < theirs.Count;) {
            if (Same(mine[a], sound[b])) {
                mapped[a] = b;
                placed++;
                a++;
                b++;
            } else if (table[a + 1, b] >= table[a, b + 1]) {
                a++;
            } else {
                b++;
            }
        }

        // Most of a note's phones have to find a place: a reading that shares
        // barely any sound with the window is not the same word at all. And the
        // places have to be the same phone, not merely the same kind of phone:
        // pairing "ow" with "ay" because both are vowels would put the value on
        // the wrong context, which sounds worse than no value at all.
        var exact = 0;
        for (var a = 0; a < mapped.Length; a++) {
            if (mapped[a] >= 0 && mine[a] == sound[mapped[a]]) {
                exact++;
            }
        }
        if (placed * 2 < mine.Count || exact == 0 || exact * 2 < placed) {
            return null;
        }
        return mapped;
    }

    /// <summary>The phone an alias names, which is the name at the end of it.</summary>
    private static string LastPhone(string alias) {
        var text = (alias ?? "").Trim();
        var space = text.LastIndexOf(' ');
        return space < 0 ? text : text.Substring(space + 1);
    }

    /// <summary>Whether two phones are the same sound, or near enough.</summary>
    private static bool Same(string a, string b) {
        return Score(a, b) > 0;
    }
}