# Image Opt Compatibility Patch

A RimWorld 1.6 compatibility patch **for** [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568) by soeur.
You still need Image Opt installed — this patches around it and does not replace or modify it.

It was built to make Image Opt usable on a very large mod list (the ~1,478-mod Progression pack),
where Image Opt otherwise black-screened during loading.

## What it does

| Feature | Problem it solves |
|---|---|
| **Vehicle readback** | Vehicle Framework rebuilds liveries by reading and rewriting texture pixels. Image Opt's textures live on the GPU and can't serve a CPU read. Textures from vehicle mods are swapped for CPU-readable copies. |
| **Early-load guards** | Vanilla Expanded Framework and Worldbuilder read data that isn't ready yet on the pre-load screen. Image Opt lengthens that window enough to throw every frame, which blanks the screen. Both are held back until the data exists. |
| **Orphan sweep** | Removes Image Opt `.dds.zstd` cache files whose source image has gone, which would otherwise be served as stale textures. It **never** touches plain `.dds` — mods ship those deliberately. |

Everything is a no-op when Image Opt isn't active, so disabling Image Opt to track down a problem still gives you a clean boot.

## Measured results

On one boot of the full Progression pack with Image Opt and this patch enabled:

- reached the main menu, where Image Opt alone had black-screened
- 552 vehicle textures converted across 6 vehicle mods
- zero `Root level exception` errors (previously thrown every frame)
- GC pauses fell from 21 (74.9 s total) to 1 (2.6 s); the Prepatcher phase fell from 51.8 s to 6.1 s

That's one machine and one mod list. Your numbers will differ.

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
- **Green_Mushroom** — Faster Game Loading (Preview), whose Image Opt compatibility layer this relies on.
- **ferny** and the Progression pack maintainers.

## Licence

MIT — see [LICENSE](LICENSE). Fixes and pull requests welcome.
