# Image Opt + Faster Game Loading Compatibility Patch

A small compatibility patch for using [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568) with
[Faster Game Loading - Continued (Preview)](https://steamcommunity.com/sharedfiles/filedetails/?id=3797541348).
Install both mods first, then load this patch after them. It leaves the original mods alone.

It started as a fix for a large Progression mod list (about 1,478 mods), where Image Opt could leave
the game on a black screen during loading.

## Tested versions

| Mod | Version |
|---|---|
| Image Opt | `0.1.13` |
| Faster Game Loading - Continued (Preview) | `2026.09.07.1`, all settings default |
| Harmony | `2.4.2` |
| RimWorld | `1.6.4871` |

The patch reads each mod's `modVersion` at startup and warns about any it wasn't tested with. It reads
`modVersion` rather than the assembly version because both authors leave the assembly version at a
placeholder (`0.0.0.0` and `1.0.0.0`), which identifies nothing. The check matters because the patch
reflects into both mods' internals: a renamed field in a newer version would otherwise switch parts of it
off silently.

## Which Faster Game Loading build to use

Use **Faster Game Loading - Continued (Preview)**. It includes the small Image Opt compatibility layer this patch expects. Without it, Image Opt can black-screen while the game is loading.

The regular and Preview builds share a package id, so RimWorld cannot tell them apart in the mod list. This patch checks which one is actually installed and warns if the required support is missing.

### Tested configuration

Faster Game Loading (Preview) with **all settings at their defaults**:

| Setting (as FGL saves it) | Default |
|---|---|
| `earlyModContentLoading` | on |
| `enableMultiThreading` | on |
| `XPathCaching` | on |
| `delayGraphicLoading` | off |
| `StaticAtlasesBaking` | off |

The patch reads these at startup and warns if any differ. The warning says *untested*, not *broken*: no changed setting has been shown to break Image Opt, but none has been tried with it either.

Note that FGL stores its config per **Workshop ID**, not per package ID. Switching between the official and Preview builds starts from a fresh config file with defaults.

## If RimWorld opens on the wrong monitor

Unity chooses the display before RimWorld loads its mods, so a compatibility patch cannot reliably correct the window during startup. In Steam, open **Properties → General → Launch Options** and add:

```text
-monitor 1
```

Unity numbers monitors from 1, so `1` selects the primary display. Fully exit RimWorld before testing the option; it is applied on the next process launch and is also used when RimWorld restarts itself.

## What it does

| Feature | Problem it solves |
|---|---|
| **Generic pixel readback** | Seven managed Texture2D read overloads use cached CPU-readable copies for Image Opt textures, including textures outside the vehicle list. Copies are released during content teardown and recreated if destroyed. |
| **Vehicle readback** | Vehicle Framework rebuilds liveries by reading and rewriting texture pixels. Image Opt's textures live on the GPU and can't serve a CPU read. Textures from vehicle mods are swapped for CPU-readable copies. |
| **Early-load guards** | Vanilla Expanded Framework and Worldbuilder read data that isn't ready yet on the pre-load screen. Image Opt lengthens that window enough to throw every frame, which blanks the screen. Both are held back until the data exists. |
| **Orphan sweep** | Removes Image Opt `.dds.zstd` cache files whose source image has gone, which would otherwise be served as stale textures. It **never** touches plain `.dds` - mods ship those deliberately. |
| **Double-extension path fix** | Image Opt caches as `name.dds.zstd`. A mod that builds texture paths by scanning its own folder calls `Path.GetFileNameWithoutExtension`, which strips only the **last** extension - yielding `name.dds`, a content path that resolves to nothing. Measured cause of the flood below: **Holograms And Projectors** (`Vesper.HologramsAndProjectors`) → 60 dead paths → 60 null-texture materials → 222,128 warnings. Retries the corrected path, and only ever after a lookup has already failed. |
| **Missing-texture report** | Opt-in. Observes final required texture-lookup errors, records the def and owning mod, and copies a report to the clipboard. Leaves the original error intact and ignores optional probes. |
| **Failed-audio guard** | Checks Unity and RimWorld's decoder state before a sound grain reads a failed clip's length. Uses one reusable silent clip for failed clips, including folder and custom grains. Loading and unloaded clips are preserved. |
| **Null-texture guard** | Unity logs `null texture passed to GUI.DrawTexture` once per call with no deduplication - Unity emits it itself, so RimWorld's repeat filter never sees it. Measured at **222,128 lines in one session, 94% of a 236,731-line log**. Drawing and ticking share a thread, so it costs tick rate and shows as stutter above 1× while 1× looks fine. Skips the null repaint draw by default, so the screen stays pixel-identical (a null draws nothing and Unity's warning is suppressed); `BadTex` (magenta) is available as an opt-in diagnostic. Samples the first 8 null draws and names the nearest mod on the call stack for each, looking through Harmony-patched methods. When no single mod can be named, it says so. |
| **Sound file loading repair** | Many audio editors save WAV files with an "extensible" header (`0xFFFE`) even for ordinary 16-bit stereo. RimWorld's decoder rejects it; the sound stays silent and the log shows a misleading "Value cannot be null". Reads such files as the plain PCM they are, in memory only, and lets the game log the real reason for any sound that truly cannot be decoded. Measured: all 8 sounds of the Hamster mod. |
| **Startup check** | While the game loads, checks that every enabled fix is in place, asking Harmony which of this patch's hooks are live rather than trusting its own bookkeeping. Also checks that Harmony-patched frames resolve (so reports name the right mod), and that the Faster Game Loading build, its settings and the versions are the tested ones. A check that could not run is shown as failed, never as passed. Shown in the main-menu corner the way the Harmony mod shows its version, and at the top of the settings page and the diagnostic report. |
| **Repeated-error finder** | Names the mod whose code keeps throwing the same error. It reads the exception object in `Environment.GetStackTrace`, the one method every exception passes through on its way to text (the Harmony mod hooks it too), so it sees Unity's own error lines and the errors mods catch and log themselves. That works even when the Harmony mod's trace cache leaves only "Duplicate stacktrace" in the log. Errors are told apart by type, throwing method and the nearest mod on the stack; with no mod's code on the stack, it lists the mods whose patches the error passed through. Notice at 100 repeats, on screen once at 1,000. |
| **Report level** | Quiet, Important (default), or Everything. Important means problems that can break the game, plus problems one of this patch's settings can fix, shown once after loading. |

Texture fixes and the orphan sweep are disabled when Image Opt is inactive. The early-UI guards and the null-texture guard remain active: neither problem is Image Opt's doing, and both cost frame time regardless.

The failed-audio guard also works without Image Opt. Texture repair and audio protection use
non-generic patch targets: patching `ContentFinder<Texture2D>.Get` or `ContentFinder<AudioClip>.Get`
on RimWorld's Mono can redirect both kinds of lookup into whichever specialization was patched
last. This defect was reproduced and removed in the September 22 review.

One terminal overload is patched for each of `GUI.DrawTexture` and `GUI.DrawTextureWithTexCoords`. Unity's public overloads are pure forwarders into one 12-argument internal method that holds the null check; patching every link would run the prefix three or four times per draw to reach a check that exists once. `UnityApiContractTests` pins that arity so a Unity restructure fails a test rather than silently no-opping.

## Also patches these other mods

Beyond the Image Opt + Faster Game Loading pairing, this patch reaches into three other mods. Each part
only runs if that mod is installed, and is a no-op otherwise.

| Mod | What's patched | Why |
|---|---|---|
| **Vehicle Framework** (SmashPhil), Vanilla Vehicles Expanded, Vehicle Map Framework, other vehicle packs | Texture pixel reads | Liveries are built by reading texture pixels, which Image Opt's GPU-only textures break |
| **Vanilla Expanded Framework** (Oskar Potocki) | `VanillaExpandedFramework_DebugWindowsOpener_DevToolStarterOnGUI_Patch.Prefix` | Reads a `KeyBindingDef` before DefOfs initialise, throwing every frame on the loading screen |
| **Worldbuilder** | `Rand_EnsureStateStackEmpty_Patch.Prefix` | Reads `WorldbuilderMod.settings` before settings load, with the same result |

The VEF and Worldbuilder guards skip those mods' own patches until the data they need exists. For
Worldbuilder the guard also sets the return value so that RimWorld's own `Rand.EnsureStateStackEmpty`
still runs, since skipping it would break more than it fixes.

## Measured results

Boot test of the reviewed build (2026-09-22): the full Progression pack with Image Opt and this
patch enabled, a session of about two hours.

- reached the main menu and loaded the save, where Image Opt alone had black-screened
- 552 vehicle textures made CPU-readable across 6 vehicle mods
- 0 Unity `null texture passed to GUI.DrawTexture` warnings. The session that found the flood logged
  222,128. The double-extension repair removes the main source at load, and the guard caught the
  null draws that remained.
- 0 `Could not load Texture2D` and 0 `Could not load AudioClip` errors. An earlier build's own
  generic `ContentFinder<T>` hooks had caused thousands of them. The review removed those hooks.

The attribution fix and the rename came after that boot. They change only which mod a diagnostic
names and what the mod is called. The test suites and the Mono probe cover them, but they were not
part of the boot.

An earlier build, on the same list, also measured GC pauses falling from 21 (74.9 s total) to 1
(2.6 s), and the Prepatcher phase from 51.8 s to 6.1 s. Those were not re-measured for this build,
and no TPS figure has been measured at all.

That's one machine and one mod list. Your numbers will differ.

## Known gaps - please read before relying on it

- **The vehicle livery has not been visually confirmed.** Textures convert without error, but no one has yet opened a vehicle paint page and checked the turret. This is the fix the patch exists for, and it's unverified.
- **Proactive vehicle conversion covers ten mods.** Generic readback additionally handles other Image Opt textures on the main thread. Direct calls to the native five-argument GetPixels overload remain uncovered.
- **The report level, startup check and repeated-error finder have not been through a full boot yet.** They came after the boot test above; unit, logic and Mono tests cover them. The sound repair has been seen in the game: a partial boot of 0.3.0 got past the point where the earlier boot logged 8 errors for the Hamster mod's sounds, and logged none.
- **No test runs Unity's native rendering or audio.** Unit and logic tests use explicit stand-ins;
  contract tests read installed game metadata. The separate Mono probe executes real Harmony
  detours on the installed game's runtime, but not Unity's native graphics or audio. The live
  boot above is the only end-to-end evidence.
- **Null-draw reports name the nearest mod on the call stack.** That is usually the mod to report
  to, but a missing texture can also come from elsewhere, such as a def another mod supplied. When
  no single mod is on the stack, the report says so rather than guessing.
- **The original Image Opt textures are not freed by default** (see `destroyOriginalTexture`). They wrap memory that Image Opt's native library still owns.

If you can check any of these, a report is the most useful thing you can send.

## Building

```bash
dotnet test Source/ImageOptCompat.Tests/ImageOptCompat.Tests.csproj -c Release
dotnet test Source/ImageOptCompat.LogicTests/ImageOptCompat.LogicTests.csproj -c Release
dotnet build Source/ImageOptCompat/ImageOptCompat.csproj -c Release -t:PackageRelease --no-restore
```

References come from NuGet (`Krafs.Rimworld.Ref`, `Lib.Harmony`), so a local RimWorld install isn't needed to build.
Run these commands from the repository root. Builds use separate bin/Debug and bin/Release directories. Only the explicit PackageRelease target copies a DLL into Assemblies; Debug packaging is rejected, and ordinary tests cannot overwrite the packaged DLL.
Analysers are on (`AnalysisMode=All` plus Meziantou.Analyzer) and the build is expected to be warning-free.

## Validation

Each release is built in Release mode and checked with the two test projects above. The tests cover
the patch decisions, settings, path handling, installed-mod contracts and the Unity API names used by
the Harmony patches. The parts that need live Unity objects still need an in-game check.

On Windows with RimWorld and 64-bit Python installed, also run:

```powershell
.\Source\ImageOptCompat.MonoTests\run.ps1
```

The runner accepts `-GameRoot`, `-HarmonyDll` and `-Python` overrides. It reproduces the former
generic-patch failure in a separate process, then verifies the current production hooks using real
Mono/Harmony and native-operation stand-ins. It does not start the game or change the mod list.

## Credits

- **soeur** - [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568). This patch only exists because Image Opt ships its source.
- **Taranchuk** - original author of Faster Game Loading. Its art, used in the preview image, is
  Copyright (c) 2022 Taranchuk under the MIT licence.
- **Green_Mushroom** - Faster Game Loading (Preview), whose Image Opt compatibility layer this relies on.
- **ferny** and the Progression pack maintainers.

Development note: I used AI tools while working on this patch, then checked the changes against the
source, tests and installed game assemblies myself. User-facing changes are listed in the changelog.

## Takedown

The preview image was made with an AI image tool from the artwork of Image Opt (soeur) and Faster
Game Loading. If soeur, Taranchuk,
Green_Mushroom, or any author whose work this touches would like anything changed or removed, open an
issue or ask on the Workshop page and it will be done promptly.

## Licence

MIT - see [LICENSE](LICENSE). Fixes and pull requests welcome.
