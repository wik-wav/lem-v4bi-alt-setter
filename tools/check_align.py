# -*- coding: utf-8 -*-
"""Check PhoneAlign on the pairs the field keeps failing on.

    python tools/check_align.py
"""
import shutil
import subprocess
import sys
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
work = repo / "build" / "align"
shutil.rmtree(work, ignore_errors=True)
work.mkdir(parents=True, exist_ok=True)
(work / "AlignProbe.csproj").write_text("""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Probe.cs" />
    <Compile Include="../../src/AltSetter/PhoneAlign.cs" Link="PhoneAlign.cs" />
    <Compile Include="../../src/AltSetter/PhoneClass.cs" Link="PhoneClass.cs" />
  </ItemGroup>
</Project>
""", encoding="utf-8")

cases = [
    # (mine, theirs, expected) — None means refused.
    # The window entries are aliases, so most of them name the phone before and
    # the phone they start. These are the exact strings from the plugin dump.
    (["ay"], ["- ay", "ay l"], [0]),
    (["l", "ah", "v"], ["ay l", "l ah"], [0, 1, -1]),
    (["y", "uw"], ["ah v", "v y", "y uw", "uw -"], [1, 2]),
    # A one-phone window, and entries that are already bare.
    (["ay"], ["-", "ay", "ay", "l"], [1]),
    (["l", "ah", "v"], ["ay", "l", "ah"], [1, 2, -1]),
    (["l", "ah", "v"], ["ay", "l", "ah", "v"], [1, 2, 3]),
    (["y", "uw"], ["ah", "v", "y", "uw", "-"], [2, 3]),
    (["y", "uw"], ["ao", "r", "y", "uw"], [2, 3]),
    (["w", "eh", "dh", "er", "z"], ["w", "eh", "dh", "er"], [0, 1, 2, 3, -1]),
    (["f", "ao", "r"], ["ah", "v", "f", "ao"], [2, 3, -1]),
    # Nothing in common: refuse rather than pair different sounds.
    (["m", "ow"], ["- ay", "ay l"], None),
    # Same kind of phone is not the same phone. One class-only pair in two is
    # allowed; two in three, deciding which way the reading runs, is not.
    (["ax", "ah"], ["ah", "ah"], [0, 1]),
    (["ax", "er", "f"], ["ah", "ah", "f"], None),
]
body = "using System;\nusing AltSetter;\n\ninternal static class Probe {\n" \
    "    private static int bad;\n\n" \
    "    private static int Main() {\n"
for mine, theirs, want in cases:
    want_src = "null" if want is None else ("new[] { " + ", ".join(str(v) for v in want) + " }")
    body += ("        Check(new[] { " +
             ", ".join(f'"{p}"' for p in mine) + " }, new[] { " +
             ", ".join(f'"{p}"' for p in theirs) + " }, " + want_src + ");\n")
body += """        if (bad > 0) {
            Console.WriteLine($"{bad} case(s) wrong");
            return 1;
        }
        Console.WriteLine("all cases agree");
        return 0;
    }

    private static void Check(string[] mine, string[] theirs, int[] want) {
        var map = PhoneAlign.Map(mine, theirs);
        var got = map == null ? "REFUSED" : string.Join(" ", map);
        var expect = want == null ? "REFUSED" : string.Join(" ", want);
        var ok = got == expect;
        if (!ok) {
            bad++;
        }
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} mine [{string.Join(" ", mine)}] " +
            $"vs theirs [{string.Join(" ", theirs)}] -> {got} (want {expect})");
    }
}
"""
(work / "Probe.cs").write_text(body, encoding="utf-8")

result = subprocess.run(
    ["dotnet", "build", str(work / "AlignProbe.csproj"), "-c", "Release",
     "-o", str(work / "out"), "--nologo", "-v", "q"],
    capture_output=True, text=True)
if result.returncode != 0:
    print(result.stdout[-3000:])
    print(result.stderr[-3000:])
    sys.exit(1)
run = subprocess.run([str(work / "out" / "AlignProbe.exe")], capture_output=True, text=True)
print(run.stdout)
if run.returncode != 0:
    print(run.stderr[-2000:])
    sys.exit(1)
