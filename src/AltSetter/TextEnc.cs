using System;
using System.Collections.Generic;
using System.Text;

namespace AltSetter;

internal static class TextEnc {
    /// <summary>
    /// Legacy UTAU files are Shift_JIS, but OpenUtau writes plugin temp files
    /// with the encoding declared in plugin.txt and some banks are UTF-8.
    /// </summary>
    public static Encoding ShiftJis { get; } = CreateShiftJis();

    private static Encoding CreateShiftJis() {
        try {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        } catch (Exception) {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Decode a byte blob as UTF-8 when it is valid UTF-8, otherwise as
    /// Shift_JIS. Files that are pure ASCII decode identically either way.
    ///
    /// A byte order mark is removed rather than kept. OpenUtau writes one on
    /// some projects, and a stray U+FEFF in front of the first key stops that
    /// key from matching anything, which silently reads as an empty project.
    /// </summary>
    public static string Decode(byte[] bytes) {
        if (bytes == null || bytes.Length == 0) {
            return string.Empty;
        }
        string text;
        try {
            var strict = new UTF8Encoding(false, true);
            text = strict.GetString(bytes);
        } catch (DecoderFallbackException) {
            text = ShiftJis.GetString(bytes);
        }
        return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
    }
}
