using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AltSetter;

/// <summary>
/// Finds the voicebank that a project is singing with.
///
/// A legacy plugin is only handed a note list, so the bank has to be located
/// from outside the project. The order below prefers something explicit (a
/// command line switch, an environment variable, or a voice_path.txt next to
/// the executable) and only then falls back to UTAU's usual voice folders.
/// </summary>
internal static class BankResolver {
    private static readonly string[] VoiceFolderNames = { "voice", "Voice", "voices" };

    public static Bank Resolve(Options options, UstDoc doc, string projectSinger = null) {
        foreach (var candidate in Candidates(options, doc, projectSinger)) {
            if (candidate == null) {
                continue;
            }
            var path = candidate;
            if (!Directory.Exists(path)) {
                continue;
            }
            if (!LooksLikeVoicebank(path)) {
                continue;
            }
            return BankReader.Load(path);
        }
        throw new DirectoryNotFoundException(BuildMessage(options));
    }

    private static IEnumerable<string> Candidates(
        Options options, UstDoc doc, string projectSinger) {
        if (!string.IsNullOrWhiteSpace(options.Bank)) {
            yield return options.Bank;
        }
        var environment = Environment.GetEnvironmentVariable("ALTSETTER_BANK");
        if (!string.IsNullOrWhiteSpace(environment)) {
            yield return environment;
        }
        var configured = ReadConfiguredPath();
        if (!string.IsNullOrWhiteSpace(configured)) {
            yield return configured;
        }
        if (doc != null) {
            var voiceDir = ReadVoiceDir(doc);
            if (!string.IsNullOrWhiteSpace(voiceDir)) {
                yield return voiceDir;
            }
        }
        if (!string.IsNullOrWhiteSpace(projectSinger)) {
            foreach (var root in VoiceRoots()) {
                if (!Directory.Exists(root)) {
                    continue;
                }
                var exact = Path.Combine(root, projectSinger);
                if (Directory.Exists(exact)) {
                    yield return exact;
                }
                foreach (var match in SafeEnumerate(root, projectSinger)) {
                    yield return match;
                }
            }
        }
        // Unambiguous single-bank install: only one voicebank exists at all.
        foreach (var root in VoiceRoots()) {
            if (!Directory.Exists(root)) {
                continue;
            }
            var banks = SafeBanks(root);
            if (banks.Count == 1) {
                yield return banks[0];
            }
        }
    }

    private static bool LooksLikeVoicebank(string path) {
        if (File.Exists(Path.Combine(path, "oto.ini"))) {
            return true;
        }
        try {
            return Directory.EnumerateFiles(path, "oto.ini", SearchOption.AllDirectories)
                .Any();
        } catch (Exception) {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string name) {
        try {
            return Directory.EnumerateDirectories(root)
                .Where(d => Path.GetFileName(d)
                    .Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        } catch (Exception) {
            return Array.Empty<string>();
        }
    }

    private static List<string> SafeBanks(string root) {
        try {
            return Directory.EnumerateDirectories(root)
                .Where(LooksLikeVoicebank)
                .ToList();
        } catch (Exception) {
            return new List<string>();
        }
    }

    private static string ReadVoiceDir(UstDoc doc) {
        foreach (var line in doc.Lines) {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("VoiceDir=", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            var value = trimmed.Substring("VoiceDir=".Length).Trim();
            value = value.Replace("%VOICE%", "").Replace("%DATA%", "").Trim();
            if (value.Length > 0) {
                return value;
            }
        }
        return null;
    }

    private static string ReadConfiguredPath() {
        var candidate = Path.Combine(AppContext.BaseDirectory, "voice_path.txt");
        if (!File.Exists(candidate)) {
            return null;
        }
        foreach (var raw in File.ReadAllLines(candidate)) {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) {
                continue;
            }
            return line;
        }
        return null;
    }

    private static IEnumerable<string> VoiceRoots() {
        var roots = new List<string>();
        void Add(string root) {
            if (!string.IsNullOrWhiteSpace(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase)) {
                roots.Add(root);
            }
        }

        var utauDir = Environment.GetEnvironmentVariable("UTAU_DIR");
        foreach (var name in VoiceFolderNames) {
            if (!string.IsNullOrWhiteSpace(utauDir)) {
                Add(Path.Combine(utauDir, name));
            }
            Add(Path.Combine(@"D:\UTAU", name));
            Add(Path.Combine(@"C:\UTAU", name));
        }
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UTAU", "voice"));
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "UTAU", "voice"));

        // OpenUtau's own singer folder is the standard install location for
        // anything the user added there.
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenUtau", "Singers"));
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OpenUtau", "Singers"));
        return roots;
    }

    private static string BuildMessage(Options options) {
        var lines = new List<string> {
            "Could not find the voicebank.",
            "The plugin is only given the notes, so it needs to be told where the bank is.",
            "Fix it with any one of:",
            "  1. Put the bank folder path on its own line in a file called voice_path.txt",
            "     next to AltSetter.exe, e.g.  D:\\UTAU\\voice\\Lem_V4Bi_Civet",
            "  2. Set --bank PATH (command line) or the ALTSETTER_BANK environment variable.",
            "  3. Load the bank as the singer of the track before running the plugin.",
            "  4. Install the bank under a UTAU \"voice\" folder that this tool searches:",
        };
        foreach (var root in VoiceRoots()) {
            lines.Add("       " + root);
        }
        return string.Join(Environment.NewLine, lines);
    }
}
