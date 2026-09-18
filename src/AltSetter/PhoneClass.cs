using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AltSetter;

/// <summary>
/// Broad articulatory classes, ported from the FestVox builder so that OTO
/// context can be compared across banks that spell phones differently
/// (for example an English phone next to a Japanese CV alias).
/// </summary>
internal static class PhoneClass {
    public static readonly HashSet<string> Vowels = new HashSet<string>(new[] {
        "a", "aa", "ae", "ah", "ao", "aw", "ax", "ay", "e", "eh",
        "er", "ey", "i", "ih", "iy", "o", "ow", "oy", "u", "uh", "uw",
    }, StringComparer.Ordinal);

    private static readonly (string Name, string[] Phones)[] Groups = {
        ("stop_voiceless", "p t k q py ty ky cl".Split(' ')),
        ("stop_voiced", "b d g by dy gy dx dxy".Split(' ')),
        ("affricate_voiceless", "ch ts".Split(' ')),
        ("affricate_voiced", "jh dz".Split(' ')),
        ("fricative_voiceless", "f s sh th h hh fy hy".Split(' ')),
        ("fricative_voiced", "v z zh dh vy zi".Split(' ')),
        ("nasal", "m n ng nn mm nng xn my ny ngy".Split(' ')),
        ("liquid", "l r rr ly ry ri".Split(' ')),
        ("glide", "w y wi".Split(' ')),
    };

    private static readonly Dictionary<string, string> Map = Build();
    private static readonly HashSet<string> Silence = new HashSet<string>(
        new[] { "pau", "sil", "sp" }, StringComparer.Ordinal);

    /// <summary>Phones whose recordings are risky to splice unless the outer context is safe.</summary>
    public static readonly HashSet<string> VoicedSibilants = new HashSet<string>(
        new[] { "z", "zh", "zi", "dz", "jh" }, StringComparer.Ordinal);

    private static readonly HashSet<string> Supportive = new HashSet<string>(
        new[] { "vowel", "nasal", "liquid", "glide", "fricative_voiced" },
        StringComparer.Ordinal);

    private static readonly HashSet<string> HardRisk = new HashSet<string>(
        new[] {
            "stop_voiceless", "stop_voiced",
            "affricate_voiceless", "affricate_voiced",
        }, StringComparer.Ordinal);

    private static Dictionary<string, string> Build() {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var phone in Vowels) {
            map[phone] = "vowel";
        }
        foreach (var (name, phones) in Groups) {
            foreach (var phone in phones) {
                map[phone] = name;
            }
        }
        return map;
    }

    public static string StripTakeSuffix(string phone) {
        var text = (phone ?? string.Empty).Trim().ToLowerInvariant().TrimEnd('_');
        var digits = text.Length;
        while (digits > 0 && char.IsDigit(text[digits - 1])) {
            digits--;
        }
        return digits > 0 ? text.Substring(0, digits) : text;
    }

    public static string Of(string phone) {
        var basePhone = StripTakeSuffix(phone);
        if (basePhone.Length == 0) {
            return "other";
        }
        if (basePhone == "*") {
            return "wildcard";
        }
        if (Map.TryGetValue(basePhone, out var name)) {
            return name;
        }
        if (Silence.Contains(basePhone)) {
            return "silence";
        }
        // A trailing "y" is a palatalised onset in this bank (by, ty, ny...).
        if (basePhone.EndsWith("y", StringComparison.Ordinal) && basePhone.Length > 1) {
            var head = Of(basePhone.Substring(0, basePhone.Length - 1));
            if (head != "other") {
                return head;
            }
        }
        return "other";
    }

    /// <summary>
    /// Classify the acoustic edge of an OTO context token that faces the
    /// diphone. Tokens may be atomic phones or strict CV compounds such as
    /// "ka": in that case the edge nearest the diphone is used.
    /// </summary>
    public static string EdgeClass(string token, bool rightEdge) {
        var text = (token ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0 || text == "*") {
            return "wildcard";
        }
        var direct = Of(text);
        if (direct != "other") {
            return direct;
        }
        foreach (var vowel in Vowels.OrderByDescending(v => v.Length)) {
            if (text.Length <= vowel.Length ||
                !text.EndsWith(vowel, StringComparison.Ordinal)) {
                continue;
            }
            var onset = text.Substring(0, text.Length - vowel.Length);
            var onsetClass = Of(onset);
            if (onsetClass == "other" || onsetClass == "wildcard" ||
                onsetClass == "silence" || onsetClass == "vowel") {
                continue;
            }
            return rightEdge ? vowel : onsetClass;
        }
        return "other";
    }

    /// <summary>Supportive / unknown / risky verdict for a take's recorded right context.</summary>
    public static string SibilantQuality(string rightContext) {
        var contextClass = EdgeClass(rightContext, rightEdge: false);
        if (Supportive.Contains(contextClass)) {
            return "verified_supportive";
        }
        if (contextClass == "wildcard" || contextClass == "other") {
            return "unknown";
        }
        return HardRisk.Contains(contextClass)
            ? "verified_risky_stop"
            : "verified_risky";
    }
}

/// <summary>One recorded take of one phone-to-phone transition.</summary>
internal sealed class Take {
    public int Number;            // UTAU alt index: 0 is the unnumbered base take
    public string Alias = "";     // full oto alias, e.g. "ay b1E3"
    public string Wav = "";       // wav basename, e.g. "ay_b_r_iy..."
    public string OtoFile = "";   // relative oto.ini label
    public int OtoLine;
    public double OffsetMs;
    public double PreutterMs;

    public string Left = "";      // phone recorded on the left of the transition
    public string Right = "";     // phone recorded on the right of the transition

    public string LeftContext = "*";
    public string RightContext = "*";
    public string LeftContextSource = "unavailable";
    public string RightContextSource = "unavailable";
    public string LClass = "*";
}

/// <summary>All recorded takes for one diphone, ordered base-first.</summary>
internal sealed class Diphone {
    public string Left = "";
    public string Right = "";
    public List<Take> Takes = new List<Take>();

    public string Key => Left + "-" + Right;

    public Take Base => Takes.Count > 0 ? Takes[0] : null;

    public bool HasAlternates => Takes.Count > 1;
}
