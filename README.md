# Image Opt + Faster Game Loading Compatibility Patch

A RimWorld 1.6 compatibility patch for running [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568) together with
[Faster Game Loading - Continued (Preview)](https://steamcommunity.com/sharedfiles/filedetails/?id=3797541348).
**It needs both of those mods installed.** It patches around them and does not replace or modify either.

It was built to make Image Opt usable on a very large mod list (the ~1,478-mod Progression pack),
where Image Opt otherwise black-screened during loading.

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

## Faster Game Loading: use the Preview build

If you run [Faster Game Loading](https://steamcommunity.com/sharedfiles/filedetails/?id=3797541348), use **Faster Game Loading - Continued (Preview)**. It carries `ImageOptEarlyLoadCoordinator`, which stops FGL's early content loading from closing Image Opt's texture channel before loading finishes. Without it, Image Opt can black-screen at load.

This can't be declared as a dependency: both FGL builds share the package id `Taranchuk.FasterGameLoading`, so `About.xml` can't tell them apart. The patch checks for the coordinator **by capability** at startup and warns in the log and on its settings page if it's missing. 

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

## What it does

| Feature | Problem it solves |
|---|---|
| **Vehicle readback** | Vehicle Framework rebuilds liveries by reading and rewriting texture pixels. Image Opt's textures live on the GPU and can't serve a CPU read. Textures from vehicle mods are swapped for CPU-readable copies. |
| **Early-load guards** | Vanilla Expanded Framework and Worldbuilder read data that isn't ready yet on the pre-load screen. Image Opt lengthens that window enough to throw every frame, which blanks the screen. Both are held back until the data exists. |
| **Orphan sweep** | Removes Image Opt `.dds.zstd` cache files whose source image has gone, which would otherwise be served as stale textures. It **never** touches plain `.dds` — mods ship those deliberately. |

Everything is a no-op when Image Opt isn't active, so disabling Image Opt to track down a problem still gives you a clean boot.

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

**Those numbers come from the build immediately before the rename to `ImageOptCompat`** — identical logic, but compiled against different references. This release build has not yet been run in-game.

## Known gaps — please read before relying on it

- **The vehicle livery has not been visually confirmed.** Textures convert without error, but no one has yet opened a vehicle paint page and checked the turret. This is the fix the patch exists for, and it's unverified.
- **The vehicle list is hardcoded to ten mods.** Around 67 mods in a large pack reference pixel-reading APIs. Others may need adding to `VehicleReadback.cs`.
- **Most fixes touch Unity types the tests can't load.** The 19 unit tests cover the pure decision logic only. The rest is verified by reading the compiled IL, not by tests.
- **The original Image Opt textures are not freed by default** (see `destroyOriginalTexture`). They wrap memory that Image Opt's native library still owns.

If you can check any of these, a report is the most useful thing you can send.

## Building

```bash
cd Source/ImageOptCompat
dotnet build -c Release            # output goes to ../../Assemblies/
cd ../ImageOptCompat.Tests
dotnet test
```

References come from NuGet (`Krafs.Rimworld.Ref`, `Lib.Harmony`), so a local RimWorld install isn't needed to build.
Analysers are on (`AnalysisMode=All` plus Meziantou.Analyzer) and the build is expected to be warning-free.

## How this was made

Written with AI assistance (Claude) and reviewed by a second, independent AI reviewer (Codex), which found four
real bugs the first pass had missed. All four are fixed. Each fix was checked in the compiled IL rather than
trusted from the build log, since a clean build doesn't prove the fix landed.

That's said openly so you can weight it appropriately. The gaps above are real and listed on purpose.

## Credits

- **soeur** — [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568). This patch only exists because Image Opt ships its source.
- **Taranchuk** — original author of Faster Game Loading. Its art, used in the preview image, is
  Copyright (c) 2022 Taranchuk under the MIT licence.
- **Green_Mushroom** — Faster Game Loading (Preview), whose Image Opt compatibility layer this relies on.
- **ferny** and the Progression pack maintainers.

## Takedown

The preview image combines art from Image Opt (soeur) and Faster Game Loading. If soeur, Taranchuk,
Green_Mushroom, or any author whose work this touches would like anything changed or removed, open an
issue or ask on the Workshop page and it will be done promptly.

## Licence

MIT — see [LICENSE](LICENSE). Fixes and pull requests welcome.
