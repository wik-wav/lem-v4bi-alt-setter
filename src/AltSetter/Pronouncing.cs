using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AltSetter;

/// <summary>
/// Minimal English grapheme-to-phoneme fallback.
///
/// The plugin normally reads the phonemes OpenUtau already computed (the
/// phonetic hints it writes into its plugin temp file). This table only covers
/// notes that have no hint at all, so that the plugin still does something
/// useful before the user has run "Add phonetic hints". The word list is the
/// same CMUdict OpenUtau itself uses, with stress digits removed, so the
/// fallback cannot disagree with the phonemizer about a word it knows.
/// </summary>
internal sealed class Pronouncing {
    private readonly Dictionary<string, string[]> words =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public int Count => words.Count;

    public static Pronouncing LoadEmbedded() {
        var result = new Pronouncing();
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("cmudict.txt.gz", StringComparison.Ordinal));
        if (name == null) {
            return result;
        }
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null) {
            return result;
        }
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        result.Read(reader);
        return result;
    }

    /// <summary>
    /// The dictionary the bank's own phonemizer reads, when it ships one.
    ///
    /// An ArpasingPlus bank carries a complete Arpasing dictionary beside its
    /// OTO files, and OpenUtau phonemizes with that. Reading it here is what
    /// makes this tool's reading of a lyric agree with the phoneme list OpenUtau
    /// will actually use; the built-in CMU dictionary is only the fallback for a
    /// bank that has no dictionary of its own.
    /// </summary>
    public static Pronouncing LoadForBank(string bankRoot) {
        if (!string.IsNullOrEmpty(bankRoot)) {
            foreach (var file in new[] { "arpasing.yaml", "arpasing_plus.yaml" }) {
                var path = Path.Combine(bankRoot, file);
                if (File.Exists(path)) {
                    var loaded = LoadBankDictionary(path);
                    if (loaded.Count > 0) {
                        return loaded;
                    }
                }
            }
        }
        return LoadEmbedded();
    }

    /// <summary>Read the "entries:" list of a bank's arpasing.yaml.</summary>
    private static Pronouncing LoadBankDictionary(string path) {
        var result = new Pronouncing();
        try {
            var inEntries = false;
            string grapheme = null;
            foreach (var raw in File.ReadLines(path)) {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') {
                    continue;
                }
                if (!inEntries) {
                    if (line.StartsWith("entries:", StringComparison.Ordinal)) {
                        inEntries = true;
                    }
                    continue;
                }
                if (line.StartsWith("- grapheme:", StringComparison.Ordinal)) {
                    grapheme = line.Substring("- grapheme:".Length).Trim();
                    continue;
                }
                if (line.StartsWith("phonemes:", StringComparison.Ordinal) && grapheme != null) {
                    var value = line.Substring("phonemes:".Length).Trim();
                    var open = value.IndexOf('[');
                    var close = value.LastIndexOf(']');
                    if (open >= 0 && close > open) {
                        value = value.Substring(open + 1, close - open - 1);
                    }
                    var phones = value
                        .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim().Trim('\'', '"'))
                        .Where(p => p.Length > 0)
                        .ToArray();
                    if (phones.Length > 0 && !result.words.ContainsKey(grapheme)) {
                        result.words[grapheme] = phones;
                    }
                    grapheme = null;
                }
            }
        } catch (IOException) {
            return result;
        }
        return result;
    }

    public static Pronouncing LoadFile(string path) {
        var result = new Pronouncing();
        using var reader = new StreamReader(path, Encoding.UTF8);
        result.Read(reader);
        return result;
    }

    private void Read(TextReader reader) {
        string line;
        while ((line = reader.ReadLine()) != null) {
            if (line.Length == 0 || line[0] == '#') {
                continue;
            }
            var tab = line.IndexOf('\t');
            if (tab <= 0) {
                continue;
            }
            var word = line.Substring(0, tab);
            var phones = line.Substring(tab + 1)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (phones.Length == 0 || !words.ContainsKey(word)) {
                words[word] = phones;
            }
        }
    }

    private static readonly (string From, string To)[] InformalSuffixes = {
        // Only reached when the whole word is missing from the dictionary.
        ("in", "ing"),   // "keepin" for "keeping"
        ("n", "ng"),     // "goin" for "going"
        ("em", "them"),
    };

    /// <summary>Look up one word. Returns null when the word is unknown.</summary>
    public string[] Query(string word) {
        var text = (word ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) {
            return null;
        }
        if (words.TryGetValue(text, out var direct)) {
            return direct;
        }
        // The phonemizer queries raw lyrics, which may carry punctuation.
        var trimmed = text.Trim('\'', '"', '.', ',', '!', '?', ';', ':', '-');
        if (trimmed.Length > 0 && trimmed != text && words.TryGetValue(trimmed, out var clean)) {
            return clean;
        }
        if (trimmed.Length == 0) {
            return null;
        }
        // Dropped-g ("keepin'", "goin'") never reaches a dictionary.
        foreach (var (from, to) in InformalSuffixes) {
            if (trimmed.Length <= from.Length ||
                !trimmed.EndsWith(from, StringComparison.Ordinal)) {
                continue;
            }
            var expanded = trimmed.Substring(0, trimmed.Length - from.Length) + to;
            if (words.TryGetValue(expanded, out var recovered)) {
                return recovered;
            }
        }
        return null;
    }
}
