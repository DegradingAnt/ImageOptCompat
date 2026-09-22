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
| **Missing-texture report** | Opt-in. Records every texture a def asked for that doesn't resolve, with the def and the owning mod, and copies a paste-ready report to the clipboard. Off by default - it patches `ContentFinder`. |
| **Null-texture guard** | Unity logs `null texture passed to GUI.DrawTexture` once per call with no deduplication - Unity emits it itself, so RimWorld's repeat filter never sees it. Measured at **222,128 lines in one session, 94% of a 236,731-line log**. Drawing and ticking share a thread, so it costs tick rate and shows as stutter above 1× while 1× looks fine. Skips the null repaint draw by default, so the screen stays pixel-identical (a null draws nothing and Unity's warning is suppressed); `BadTex` (magenta) is available as an opt-in diagnostic. Names the first 8 distinct callers once each, with the owning mod. |

Texture fixes and the orphan sweep are disabled when Image Opt is inactive. The early-UI guards and the null-texture guard remain active: neither problem is Image Opt's doing, and both cost frame time regardless.

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

On one boot of the full Progression pack with Image Opt and this patch enabled:

- reached the main menu, where Image Opt alone had black-screened
- 552 vehicle textures converted across 6 vehicle mods
- zero `Root level exception` errors (previously thrown every frame)
- GC pauses fell from 21 (74.9 s total) to 1 (2.6 s); the Prepatcher phase fell from 51.8 s to 6.1 s

That's one machine and one mod list. Your numbers will differ.

**Those numbers come from an earlier build.** The generic readback cache, its cleanup and the configuration checks have since changed. These measurements do not validate the current release, which still needs its final in-game boot test.

## Known gaps - please read before relying on it

- **The vehicle livery has not been visually confirmed.** Textures convert without error, but no one has yet opened a vehicle paint page and checked the turret. This is the fix the patch exists for, and it's unverified.
- **Proactive vehicle conversion covers ten mods.** Generic readback additionally handles other Image Opt textures on the main thread. Direct calls to the native five-argument GetPixels overload remain uncovered.
- **Real Unity rendering still needs testing.** The 80 unit tests cover pure decisions, settings reflection and the path-correction rule, using explicit Unity stand-ins. `UnityApiContractTests` reads the installed game's own assemblies as metadata to verify every name each patch binds to, but nothing here executes GPU operations or Harmony detours.
- **The null-texture guard has not run in a live game.** Its rules are covered by tests, and four planted defects were each caught by the suite, but the before-and-after log count that would prove the flood stops is still outstanding.
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

## Credits

- **soeur** - [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568). This patch only exists because Image Opt ships its source.
- **Taranchuk** - original author of Faster Game Loading. Its art, used in the preview image, is
  Copyright (c) 2022 Taranchuk under the MIT licence.
- **Green_Mushroom** - Faster Game Loading (Preview), whose Image Opt compatibility layer this relies on.
- **ferny** and the Progression pack maintainers.

Development note: I used AI tools while working on this patch, then checked the changes against the
source, tests and installed game assemblies myself. User-facing changes are listed in the changelog.

## Takedown

The preview image combines art from Image Opt (soeur) and Faster Game Loading. If soeur, Taranchuk,
Green_Mushroom, or any author whose work this touches would like anything changed or removed, open an
issue or ask on the Workshop page and it will be done promptly.

## Licence

MIT - see [LICENSE](LICENSE). Fixes and pull requests welcome.
