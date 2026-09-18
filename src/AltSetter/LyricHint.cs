using System;
using System.Collections.Generic;
using System.Text;

namespace AltSetter;

/// <summary>
/// The part of a note's lyric that carries phonemes: the "[...]" hint OpenUtau
/// stores after the displayed text. Everything outside the brackets is
/// untouched.
/// </summary>
internal sealed class LyricHint {
    public string Display = "";                 // text before the hint
    public bool HasHint;
    public readonly List<string> Phones = new List<string>();

    public static LyricHint Parse(string lyric) {
        var result = new LyricHint();
        var text = lyric ?? "";
        var open = text.LastIndexOf('[');
        if (open < 0) {
            result.Display = text;
            return result;
        }
        var close = text.IndexOf(']', open + 1);
        if (close < 0) {
            result.Display = text;
            return result;
        }
        result.Display = text.Substring(0, open).TrimEnd();
        result.HasHint = true;
        var body = text.Substring(open + 1, close - open - 1);
        foreach (var token in body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) {
            result.Phones.Add(token);
        }
        return result;
    }

    public string Format() {
        if (!HasHint) {
            return Display;
        }
        var builder = new StringBuilder();
        if (Display.Length > 0) {
            builder.Append(Display).Append(' ');
        }
        builder.Append('[').Append(string.Join(" ", Phones)).Append(']');
        return builder.ToString();
    }

    /// <summary>
    /// Take a phone as written in a hint and return the symbol plus an optional
    /// inline alternate number ("ay1" -&gt; "ay", 1).
    /// </summary>
    public static void SplitPhone(string token, out string phone, out int alt) {
        phone = (token ?? "").Trim().ToLowerInvariant();
        alt = 0;
        var digits = phone.Length;
        while (digits > 0 && char.IsDigit(phone[digits - 1])) {
            digits--;
        }
        if (digits == phone.Length) {
            return;
        }
        if (digits == 0) {
            phone = "";
            return;
        }
        var numberText = phone.Substring(digits);
        phone = phone.Substring(0, digits);
        if (int.TryParse(numberText, out var parsed)) {
            alt = parsed;
        }
    }

    public static string JoinPhone(string phone, int alt) =>
        alt > 0 ? phone + alt.ToString(System.Globalization.CultureInfo.InvariantCulture) : phone;
}
