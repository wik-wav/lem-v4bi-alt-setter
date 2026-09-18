using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AltSetter;

internal static class Program {
    private const string PluginName = "Lem V4Bi Alt Setter";

    private static int Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;
        try {
            var options = Options.Parse(args);
            if (options.Help) {
                PrintHelp();
                return 0;
            }
            if (options.Mode != "inspect" && options.File == null) {
                Console.Error.WriteLine(PluginName + ": no project file was given.");
                PrintHelp();
                return 2;
            }
            return options.Mode switch {
                "ust" => RunUst(options),
                "ustx" => RunUstx(options),
                "inspect" => RunInspect(options),
                _ => throw new ArgumentException("Unknown mode: " + options.Mode),
            };
        } catch (Exception error) {
            Console.Error.WriteLine(PluginName + " failed: " + error.Message);
            return 1;
        }
    }

    // ------------------------------------------------------------- modes

    /// <summary>Legacy OpenUtau plugin entry point: rewrite hints in place.</summary>
    private static int RunUst(Options options) {
        var doc = UstDoc.Parse(options.File);
        if (!doc.LooksLikeUst) {
            throw new InvalidDataException(
                options.File + " has no UST note blocks. This mode is called by " +
                "OpenUtau with a temporary UST; for a .ustx use --mode ustx.");
        }
        var bank = BankResolver.Resolve(options, doc);
        var selector = new Selector(bank);
        var stats = HintWriter.Run(doc, selector, options.Pronouncing, !options.NoFallbackG2p);
        if (stats.NotesRewritten > 0) {
            doc.Save(options.File, options.FileEncoding);
        }
        Report(options, bank, stats);
        return 0;
    }

    /// <summary>
    /// The main job: set the "alt" expression on every phoneme that starts a
    /// recorded transition, and change nothing else in the project.
    /// </summary>
    private static int RunUstx(Options options) {
        var project = Ustx.Load(options.File);
        if (project.NoteCount == 0) {
            throw new InvalidDataException("No notes found in " + options.File);
        }
        var bank = BankResolver.Resolve(options, null, project.VoiceName);
        var selector = new Selector(bank);
        var result = AltApplier.Run(
            project, selector, Pronouncing.LoadForBank(bank.Root), !options.NoFallbackG2p,
            options.DryRun);

        if (result.NotesWritten > 0 && !options.DryRun) {
            if (options.Backup) {
                var copy = options.File + ".bak";
                File.Copy(options.File, copy, true);
                Console.WriteLine($"  backup: {copy}");
            }
            project.Save(options.File);
        }
        if (options.Report != null) {
            WriteReport(options.Report, options.File, bank, project, result);
        }
        ReportUstx(options, bank, result);
        return 0;
    }

    /// <summary>
    /// Write one line per chosen alternate, plus what the project now holds.
    ///
    /// Written by the tool rather than captured from its console output, because
    /// a shell pipeline can re-encode it and a report that cannot be read back
    /// exactly is useless for checking the result.
    /// </summary>
    private static void WriteReport(
        string path, string projectPath, Bank bank, Ustx project,
        AltApplier.Result result) {
        var builder = new StringBuilder();
        builder.AppendLine($"# AltSetter report");
        builder.AppendLine($"project\t{projectPath}");
        builder.AppendLine($"voicebank\t{bank.Root}");
        builder.AppendLine($"notes\t{result.Notes.Count}");
        builder.AppendLine($"alternates\t{result.Alternates}");
        builder.AppendLine("# note\tindex\tleft\tright\talt\tcontext");
        foreach (var note in result.Notes) {
            foreach (var pick in note.Picks) {
                builder.AppendLine(
                    $"{note.NoteIndex}\t{pick.Index}\t{pick.Left}\t{pick.Right}\t" +
                    $"{pick.Alt}\t{pick.Context}");
            }
        }
        builder.AppendLine("# written");
        builder.AppendLine("# note\tindex\talt");
        for (var i = 0; i < project.NoteCount; i++) {
            foreach (var (index, alt) in project.AlternatesOf(i)) {
                builder.AppendLine($"{i}\t{index}\t{alt}");
            }
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void ReportUstx(Options options, Bank bank, AltApplier.Result result) {
        if (options.Quiet) {
            return;
        }
        Console.WriteLine($"{PluginName}: {options.File}");
        Console.WriteLine($"  voicebank       : {bank.Root}");
        var withPhones = result.Notes.Count(n => n.PhonemeCount > 0);
        Console.WriteLine(
            $"  notes           : {result.Notes.Count}, " +
            $"{withPhones} sing something ({result.Notes.Count - withPhones} " +
            "extenders, unknowns or rests)");
        var fromHint = result.Notes.Count(n => n.PhonesFromHint && n.PhonemeCount > 0);
        Console.WriteLine(
            $"  phonemes from   : {fromHint} hint(s), " +
            $"{withPhones - fromHint} from the built-in dictionary");
        Console.WriteLine(
            $"  transitions     : {result.Transitions} scored, " +
            $"{result.Alternates} got an alternate");
        var writtenLabel = options.DryRun
            ? "alt expressions that would be written"
            : "alt expressions written";
        Console.WriteLine(
            $"  notes with alt  : {result.NotesWithAlt}, {writtenLabel}: " +
            $"{result.ExpressionsWritten}");
        var histogram = string.Join(", ",
            result.AltHistogram.OrderBy(p => p.Key).Select(p => $"alt{p.Key}={p.Value}"));
        if (histogram.Length > 0) {
            Console.WriteLine("  " + histogram);
        }
        if (options.Verbose) {
            foreach (var note in result.Notes.Where(n => n.Picks.Count > 0)) {
                foreach (var pick in note.Picks) {
                    Console.WriteLine(
                        $"    note {note.NoteIndex}: {pick.Left} {pick.Right} " +
                        $"-> alt {pick.Alt} (ctx {pick.Context})");
                }
            }
        }
        foreach (var warning in bank.Warnings) {
            Console.WriteLine("  warning: " + warning);
        }
        foreach (var warning in result.Warnings) {
            Console.WriteLine("  note: " + warning);
        }
        if (result.NotesWritten == 0) {
            Console.WriteLine(
                "  nothing to change: no transition in this project has a better " +
                "recorded take than the bank's base recording.");
        } else if (options.DryRun) {
            Console.WriteLine("  dry run: the project file was not touched.");
        } else {
            Console.WriteLine(
                "  done. If the project is open in OpenUtau, close it first next " +
                "time or OpenUtau will overwrite this when it saves.");
        }
    }

    private static int RunInspect(Options options) {
        var bank = BankResolver.Resolve(options, null, null);
        if (options.Dump != null) {
            Dump(bank, options.Dump, options.DumpLimit);
            Console.WriteLine($"dumped {Math.Min(options.DumpLimit, bank.Diphones.Count)} diphone(s) to {options.Dump}");
        }
        Console.WriteLine($"bank            : {bank.Root}");
        Console.WriteLine($"pitch folders   : {string.Join(", ", bank.PitchFolders)}");
        Console.WriteLine($"reference folder: {bank.PrimaryFolder}");
        Console.WriteLine($"diphones        : {bank.Diphones.Count}");
        var withAlternates = bank.Diphones.Values.Count(d => d.HasAlternates);
        var takes = bank.Diphones.Values.Sum(d => d.Takes.Count);
        Console.WriteLine($"recorded takes  : {takes} ({withAlternates} diphones have alternates)");
        foreach (var warning in bank.Warnings) {
            Console.WriteLine("warning         : " + warning);
        }
        if (options.Phones != null && options.Phones.Count >= 2) {
            var selector = new Selector(bank);
            for (var i = 0; i + 1 < options.Phones.Count; i++) {
                var left = i - 1 >= 0 ? options.Phones[i - 1] : "*";
                var right = i + 2 < options.Phones.Count ? options.Phones[i + 2] : "*";
                var choice = selector.Choose(options.Phones[i], options.Phones[i + 1], left, right);
                var text = choice == null
                    ? "(not recorded)"
                    : $"alt {choice.Alt}{(choice.Alt > 0 ? " " + choice.Take.Alias : "")}  [{choice.Reason}]";
                Console.WriteLine($"  {options.Phones[i],-4} {options.Phones[i + 1],-4} -> {text}");
            }
        }
        return 0;
    }

    private static void Dump(Bank bank, string path, int limit) {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("folders\t" + string.Join(",", bank.PitchFolders));
        writer.WriteLine("reference\t" + bank.PrimaryFolder);
        foreach (var diphone in bank.Diphones.Values.Take(limit)) {
            writer.WriteLine($"== {diphone.Left} / {diphone.Right}  takes={diphone.Takes.Count}");
            foreach (var take in diphone.Takes) {
                writer.WriteLine(
                    $"   {take.Number}\t{take.Alias}\t{take.Wav}\t" +
                    $"L={take.LeftContext}({take.LeftContextSource})\t" +
                    $"R={take.RightContext}({take.RightContextSource})");
            }
        }
    }

    // ------------------------------------------------------------ reporting

    private static void Report(Options options, Bank bank, RewriteStats stats) {
        if (options.Quiet) {
            return;
        }
        Console.WriteLine($"{PluginName}: {bank.Root}");
        Console.WriteLine(
            $"  notes {stats.Notes}, with phones {stats.NotesWithPhones}, " +
            $"without hint {stats.NotesWithoutHint}, without phones {stats.NotesWithoutPhones}");
        Console.WriteLine(
            $"  transitions {stats.Transitions}, alternates chosen {stats.Alternates}, " +
            $"notes rewritten {stats.NotesRewritten}");
        if (stats.UnknownWords > 0) {
            Console.WriteLine($"  words not in the fallback dictionary: {stats.UnknownWords}");
        }
        var histogram = string.Join(", ",
            stats.AltHistogram.OrderBy(p => p.Key).Select(p => $"alt{p.Key}={p.Value}"));
        if (histogram.Length > 0) {
            Console.WriteLine("  " + histogram);
        }
        if (options.Verbose) {
            foreach (var line in stats.Trace) {
                Console.WriteLine("  " + line);
            }
        }
        foreach (var sample in stats.Samples) {
            Console.WriteLine("  e.g. " + sample);
        }
        foreach (var warning in bank.Warnings) {
            Console.WriteLine("  warning: " + warning);
        }
        var shown = 0;
        foreach (var warning in stats.Warnings) {
            if (shown++ >= 3) {
                Console.WriteLine($"  note: {stats.Warnings.Count - 3} more note(s) had no hint");
                break;
            }
            Console.WriteLine("  note: " + warning);
        }
        if (stats.NotesRewritten == 0) {
            Console.WriteLine(
                "  nothing changed. If every note already has a hint, the bank " +
                "context may simply agree with the base takes.");
        }
    }

    private static void PrintHelp() {
        Console.WriteLine(
@"Lem V4Bi Alt Setter - set the Alt expression from the voicebank's recorded context.

The Lem V4Bi banks recorded several takes of the same transition, each sung next
to different neighbours. This tool reads that context out of oto.ini, scores the
takes against the phones around each transition in your song, and writes the
winner as the note's per-phoneme ""alt"" expression.

It changes nothing else: lyrics, timing, phonemes and every other expression are
written back byte for byte.

    AltSetter.exe song.ustx [options]

Options:
    --bank PATH     voicebank folder. Defaults to the project's singer, then to
                    voice_path.txt next to this exe.
    --dry-run, -n   report what would change without writing
    --print         list every chosen alternate per note
    --report PATH   also write a machine-readable report of every choice
    --backup        keep a copy of the project as song.ustx.bak before writing
    --no-fallback   never guess phonemes from the built-in CMU dictionary
    --quiet         only report errors
    --help          this text

Other modes:
    AltSetter.exe --mode inspect --bank PATH [--phones ""ay b iy""]
    AltSetter.exe --mode inspect --bank PATH --dump takes.txt

Close the project in OpenUtau before running this, otherwise OpenUtau will
overwrite the file the next time it saves.");
    }
}

internal sealed class Options {
    public string Mode = "ust";
    public string File;
    public string Bank;
    public bool Quiet;
    public bool Help;
    public bool NoFallbackG2p;
    public bool Verbose;
    public bool DryRun;
    public bool Backup;
    public string Report;
    public List<string> Phones;
    public string Dump;
    public int DumpLimit = 40;
    public Encoding FileEncoding = TextEnc.ShiftJis;
    public Pronouncing Pronouncing = Pronouncing.LoadEmbedded();

    public static Options Parse(string[] args) {
        var options = new Options();
        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            string Next() {
                if (i + 1 >= args.Length) {
                    throw new ArgumentException(arg + " needs a value");
                }
                return args[++i];
            }
            switch (arg) {
                case "--help":
                case "-h":
                case "/?":
                    options.Help = true;
                    break;
                case "--mode":
                    options.Mode = Next().ToLowerInvariant();
                    break;
                case "--file":
                    options.File = Next();
                    break;
                case "--bank":
                    options.Bank = Next();
                    break;
                case "--phones":
                    options.Phones = Next()
                        .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .ToList();
                    break;
                case "--dump":
                    options.Dump = Next();
                    break;
                case "--dump-limit":
                    Num.TryParseInt(Next(), out options.DumpLimit);
                    break;
                case "--encoding":
                    var name = Next();
                    try {
                        options.FileEncoding = Encoding.GetEncoding(name);
                    } catch (Exception) {
                        throw new ArgumentException("Unknown encoding: " + name);
                    }
                    break;
                case "--no-fallback":
                    options.NoFallbackG2p = true;
                    break;
                case "--quiet":
                    options.Quiet = true;
                    break;
                case "--verbose":
                case "--print":
                    options.Verbose = true;
                    break;
                case "--dry-run":
                case "-n":
                    options.DryRun = true;
                    break;
                case "--backup":
                    options.Backup = true;
                    break;
                case "--report":
                    options.Report = Next();
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1) {
                        throw new ArgumentException("Unknown option: " + arg);
                    }
                    options.File ??= arg;
                    break;
            }
        }
        if (options.File == null) {
            options.Mode = "inspect";
        } else if (options.File.EndsWith(".ustx", StringComparison.OrdinalIgnoreCase) &&
                   options.Mode == "ust") {
            // Pointing the tool at a project is the common case; the UST mode is
            // only ever reached from OpenUtau's plugin call.
            options.Mode = "ustx";
        }
        return options;
    }
}
