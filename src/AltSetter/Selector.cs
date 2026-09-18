using System;
using System.Collections.Generic;
using System.Linq;

namespace AltSetter;

internal sealed class AltChoice {
    public int Alt;
    public Take Take;
    public int Score;
    public string Reason = "";
    public string Quality = "ordinary";
    public bool Changed;
}

/// <summary>
/// Chooses which recording of a transition to use for a given phrase position.
///
/// This is a direct port of the reference selector: every take of a diphone is
/// scored against the phones that actually neighbour the transition in the
/// song, with exact phone matches worth more than same-class matches, and the
/// light/dark "l" split treated as a hard constraint. Care is taken that a take
/// is only ever chosen when there is positive evidence for it, because a wrong
/// alternate is worse than the bank's own base take.
/// </summary>
internal sealed class Selector {
    private readonly Bank bank;

    public Selector(Bank bank) {
        this.bank = bank;
    }

    /// <summary>All recorded takes for a transition, or null when unrecorded.</summary>
    public Diphone Find(string left, string right) {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) {
            return null;
        }
        return bank.Find(
            PhoneClass.StripTakeSuffix(left),
            PhoneClass.StripTakeSuffix(right));
    }

    /// <summary>Whether the bank uses this phone as a transition's left phone.</summary>
    public bool KnowsPhone(string phone) =>
        bank.KnownLeftPhones.Contains(PhoneClass.StripTakeSuffix(phone));

    /// <summary>
    /// Pick the best take for <paramref name="left"/>-<paramref name="right"/>
    /// sung between <paramref name="outerLeft"/> and <paramref name="outerRight"/>.
    /// </summary>
    public AltChoice Choose(string left, string right,
                            string outerLeft, string outerRight) {
        var diphone = Find(left, right);
        if (diphone == null) {
            return null;
        }
        var choice = new AltChoice { Alt = 0, Take = diphone.Base };
        if (!diphone.HasAlternates) {
            choice.Reason = "only one recording exists";
            return choice;
        }

        var l = PhoneClass.StripTakeSuffix(left);
        var r = PhoneClass.StripTakeSuffix(right);
        var lClass = LClass(l, r, outerRight);

        var rows = diphone.Takes.ToList();
        // A take whose alias mentions a breath is a bad mid-phrase choice.
        if (!IsEdge(outerLeft) && !IsEdge(outerRight)) {
            var filtered = rows.Where(t => !MentionsBreath(t)).ToList();
            if (filtered.Count > 0) {
                rows = filtered;
            }
        }

        var baseTake = diphone.Base;
        if (!rows.Contains(baseTake)) {
            baseTake = rows[0];
        }

        Take best;
        var bestScore = 0;
        string reason;

        if (PhoneClass.VoicedSibilants.Contains(r)) {
            var supportive = rows
                .Where(t => PhoneClass.SibilantQuality(t.RightContext) == "verified_supportive")
                .ToList();
            var unknown = rows
                .Where(t => PhoneClass.SibilantQuality(t.RightContext) == "unknown")
                .ToList();
            var pool = supportive.Count > 0 ? supportive : unknown;
            if (pool.Count > 0) {
                best = pool[0];
                bestScore = Score(best, lClass, outerLeft, outerRight);
                foreach (var take in pool.Skip(1)) {
                    var score = Score(take, lClass, outerLeft, outerRight);
                    if (score > bestScore) {
                        best = take;
                        bestScore = score;
                    }
                }
                reason = supportive.Count > 0
                    ? "recorded right context is a voiced, continuant phone, which keeps the sibilant from being cut off"
                    : "no recorded take has a clearly safe right context, so an unannotated one is preferred";
            } else {
                best = baseTake;
                bestScore = Math.Max(0, Score(best, lClass, outerLeft, outerRight));
                reason = "every recorded right context is risky for this voiced sibilant, so the base take is kept";
            }
        } else {
            best = baseTake;
            bestScore = Score(best, lClass, outerLeft, outerRight);
            var baseScore = bestScore;
            foreach (var take in rows) {
                if (ReferenceEquals(take, best) || UnsafePhraseEdgeShortcut(take, outerLeft, outerRight)) {
                    continue;
                }
                var score = Score(take, lClass, outerLeft, outerRight);
                if (score > bestScore) {
                    best = take;
                    bestScore = score;
                }
            }
            reason = ReferenceEquals(best, baseTake)
                ? (baseScore > 0
                    ? "base take already matches the surrounding phones"
                    : "no take matched the surrounding phones better than the base")
                : "recorded take matches the surrounding phones better than the base";
        }

        choice.Alt = best.Number;
        choice.Take = best;
        choice.Score = bestScore;
        choice.Reason = reason;
        choice.Quality = PhoneClass.VoicedSibilants.Contains(r)
            ? PhoneClass.SibilantQuality(best.RightContext)
            : "ordinary";
        choice.Changed = choice.Alt != 0;
        return choice;
    }

    /// <summary>
    /// Enumerate the alternates that are actually recommended somewhere in a
    /// phone sequence. Index i is the transition phones[i] to phones[i+1].
    /// </summary>
    public AltChoice[] ForPhrase(IReadOnlyList<string> phones) {
        var result = new AltChoice[phones.Count];
        for (var i = 0; i < phones.Count - 1; i++) {
            var outerLeft = i - 1 >= 0 ? phones[i - 1] : "*";
            var outerRight = i + 2 < phones.Count ? phones[i + 2] : "*";
            result[i] = Choose(phones[i], phones[i + 1], outerLeft, outerRight);
        }
        return result;
    }

    private static bool IsEdge(string phone) {
        var text = (phone ?? "*").Trim().ToLowerInvariant();
        return text.Length == 0 || text == "*" || text == "pau" || text == "sil" || text == "sp";
    }

    private static bool MentionsBreath(Take take) {
        foreach (var token in take.Alias.Replace('-', ' ').Split(
                     new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
            var text = token.ToLowerInvariant();
            if (text == "inh" || text == "exh" || text == "br") {
                return true;
            }
        }
        return false;
    }

    private static string LClass(string left, string right, string outerRight) {
        var lightFollowers = new HashSet<string>(PhoneClass.Vowels, StringComparer.Ordinal) { "y" };
        if (left == "l") {
            return lightFollowers.Contains(right) ? "light" : "dark";
        }
        if (right == "l") {
            return lightFollowers.Contains(PhoneClass.StripTakeSuffix(outerRight))
                ? "light" : "dark";
        }
        return "*";
    }

    private static int Score(Take take, string lClass, string outerLeft, string outerRight) {
        var score = Side(take.LeftContext, outerLeft, exact: 6, rightEdge: true);
        score += Side(take.RightContext, outerRight, exact: 7, rightEdge: false);
        if (lClass != "*" && take.LClass != "*" && take.LClass.Length > 0) {
            score += take.LClass == lClass ? 20 : -100;
        }
        return score;
    }

    private static int Side(string expected, string actual, int exact, bool rightEdge) {
        var expectedText = string.IsNullOrEmpty(expected) ? "*" : expected;
        if (expectedText == "*") {
            return 0;
        }
        if (expectedText == (actual ?? "*")) {
            return exact;
        }
        var wanted = PhoneClass.EdgeClass(expectedText, rightEdge);
        var actualClass = PhoneClass.EdgeClass(actual ?? "*", !rightEdge);
        return wanted != "wildcard" && wanted != "other" && wanted == actualClass
            ? 4
            : -8;
    }

    /// <summary>
    /// A take is not worth taking when the only thing it improves is an exact
    /// pause at a phrase edge while it makes the other side worse.
    /// </summary>
    private static bool UnsafePhraseEdgeShortcut(Take take, string outerLeft, string outerRight) {
        var left = Relation(take.LeftContext, outerLeft, rightEdge: true);
        var right = Relation(take.RightContext, outerRight, rightEdge: false);
        return (outerLeft == "pau" && left == "exact" && right == "mismatch") ||
               (outerRight == "pau" && right == "exact" && left == "mismatch");
    }

    private static string Relation(string expected, string actual, bool rightEdge) {
        var expectedText = string.IsNullOrEmpty(expected) ? "*" : expected;
        if (expectedText == "*") {
            return "wildcard";
        }
        if (expectedText == (actual ?? "*")) {
            return "exact";
        }
        var wanted = PhoneClass.EdgeClass(expectedText, rightEdge);
        var actualClass = PhoneClass.EdgeClass(actual ?? "*", !rightEdge);
        return wanted != "wildcard" && wanted != "other" && wanted == actualClass
            ? "class"
            : "mismatch";
    }
}
