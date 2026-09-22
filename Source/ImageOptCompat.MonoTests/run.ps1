param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [string]$HarmonyDll = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077\Current\Assemblies\0Harmony.dll',
    [string]$Python = 'python',
    # A sound that failed in boot 2 (extensible WAV header). Skipped where that mod is absent.
    [string]$AudioSample = 'C:/Program Files (x86)/Steam/steamapps/workshop/content/294100/2848286000/1.6/Sounds/Hampter/call/Hamster5.wav'
)
$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'ImageOptCompat.MonoTests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Mono probe build failed.' }
$outputDir = Join-Path $PSScriptRoot 'bin\Release\net481'
# Exercise the same Harmony build as the game, not merely the NuGet version used to compile.
Copy-Item -LiteralPath $HarmonyDll -Destination (Join-Path $outputDir '0Harmony.dll') -Force
$probe = Join-Path $outputDir 'ImageOptCompat.MonoTests.exe'
$runner = Join-Path $PSScriptRoot 'run_mono.py'
& $Python $runner $GameRoot $probe --legacy
if ($LASTEXITCODE -ne 0) { throw 'Legacy failure could not be reproduced on this runtime.' }
& $Python $runner $GameRoot $probe
if ($LASTEXITCODE -ne 0) { throw 'Mono/Harmony regression checks failed.' }
# The game's own decoder, end to end: the original file must fail, the rewritten copy must decode.
if (Test-Path -LiteralPath $AudioSample) {
    & $Python $runner $GameRoot $probe --audio $AudioSample
    if ($LASTEXITCODE -ne 0) { throw 'The game decoder check failed.' }
}
