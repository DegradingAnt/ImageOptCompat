# Codex handover — 2026-09-21

Everything that changed since your `OFFLINE-VALIDATION.md` (which returned **FAIL** on HEAD `24c88da`,
DLL `35D147CC…`). Read this before re-reviewing; the shape of the mod has changed substantially.

**Nothing is published.** The repo still has 0 remotes. Ant runs you *before* booting the game, so a
finding here costs nothing but a rebuild.

---

## 1. Your two blockers are cleared

Both were already fixed in the working tree when this session started. Verified from bytes, not filenames:

| Finding | Evidence it is fixed |
|---|---|
| **P2-A** FGL settings read as fields, four are properties | `FglSettingsCheck.cs:28` — `var property = type.GetProperty(member, flags);` and 8 new tests in `FglSettingsCheckTests.cs` |
| **P2-D** packaged DLL was Debug | `ImageOptCompat.csproj:26` — `<Error Condition="'$(Configuration)' != 'Release'" Text="Only Release builds may be packaged." />`. `OutputPath` is gone; the only route into `Assemblies/` is the explicit `PackageRelease` target. `AssemblyConfiguration("Release")` blob confirmed in the shipped DLL. |

---

## 2. The problem this session solved

Ant reported **stutter and low TPS above 1× speed**, fine at 1×. `Player-prev.log`:

```
222,128 × "null texture passed to GUI.DrawTexture"   = 94% of a 236,731-line log
     60 × "Could not load Texture2D at '…Hologram_small_NN.dds'"
     60 × "MatFrom with null sourceTex."
```

Ticks and GUI share RimWorld's main thread. At 1× there is one tick per frame and slack absorbs the
cost; at 3× there are three, and a fixed per-frame tax comes straight out of tick throughput. This is
also why per-pawn profiling found nothing — the work is not in the tick at all.

### Root cause, proven

1. Image Opt caches beside the source image as `name.dds.zstd`.
2. **Holograms And Projectors** (`Vesper.HologramsAndProjectors`, Workshop `2847321165`) builds its
   animation frame paths by scanning its own texture folder — decompiled DLL, line 3139-3144:
   `Directory.EnumerateFiles(...)` then `Path.GetFileNameWithoutExtension(...)`.
3. `Path.GetFileNameWithoutExtension` strips only the **last** extension:
   `"Hologram_small_01.dds.zstd"` → `"Hologram_small_01.dds"`.
4. `ContentFinder` is asked for `…/Hologram_small_01.dds`. Content paths carry no extension, so it
   resolves to null.
5. `MaterialPool.MatFrom` builds a Material with a null texture.
6. That material is drawn every frame, forever.

**This is generic.** Any mod that scans its own texture directory breaks the same way once Image Opt
has written a cache file next to the images. The fix is not a packageId list.

---

## 3. What was added

### `MissingTextureReport.cs` — the root-cause fix (new)

Postfix on `ContentFinder<Texture2D>.Get(string, bool)`. Two jobs, deliberately split:

- **Repairs** a path ending `.dds` by retrying the stripped path. `TryCorrectPath` is the pure rule.
- **Observes only** for every other failure. This restriction is the important one:
  `ContentFinder.Get(path, reportFailure: false)` is the supported way a mod tests whether an optional
  texture exists. Substituting there would turn every such test into a false positive across a whole
  modpack. A null here is frequently correct.

Safety argument for the one case it does repair:
- runs only *after* the lookup already returned null, so no successful result is changed;
- fires only on a `.dds` suffix, and a real content path never carries an extension;
- the corrected path cannot itself end in `.dds`, so the retry cannot recurse (test-enforced).

Also records missing paths with the **def** (`ContentFinderRequester.requester`) and the **owning mod**,
for a paste-ready author report. That recording is **off by default** — Ant's call, and correct: it is
a patch on a method some mods call on hot paths.

### `NullTextureGuard.cs` — the cost fix (new)

Harmony prefix on the **terminal** `GUI.DrawTexture` overload only. Unity's public overloads are pure
forwarders into a 12-argument internal method that holds the null check (`UnityEngine.GUI`, decompiled
line 586). Patching all ten would run the prefix 3–4× per draw to reach a check that exists once.
`UnityApiContractTests` pins the terminal arity (12 and 4) so a Unity restructure fails a test instead
of silently patching a forwarder.

**Substitutes a transparent 1×1 by default, not `BadTex`.** A null currently draws *nothing*; BadTex
draws magenta. At 222k draws that would trade a log flood for a visual one. `BadTex` is available via
`nullTextureShowPlaceholder` for hunting the culprit on screen.

### `ModAttribution.cs` (new)

Inverts `ModContentPack.assemblies.loadedAssemblies` so a stack frame or def resolves to
`"Mod Name (packageid)"` rather than a bare type name.

### Every fix is now toggleable

12 settings, 12 checkboxes, 1:1. Ant's requirement: any user or dev can bisect without uninstalling.
`genericPixelReadback` and `earlyUiGuards` previously had none.
`Texture2DReadPatches.Readable` returns null when its toggle is off, so that fix goes inert
*without a restart* — the patches stay installed deliberately, so it is reversible mid-game.

---

## 4. Verification actually performed

```
Build              0 Warning(s), 0 Error(s)          (AnalysisMode=All + Meziantou.Analyzer)
Tests              Failed: 0, Passed: 80, Skipped: 0
Packaged DLL       Assemblies/ImageOptCompat.dll
                   53,760 bytes, sha256 ca12089c4a609790fbc2512d1e35371ede002fa8
                   AssemblyConfiguration("Release") blob present
```

### Mutation testing — the harness was tested, not just run

Each defect was planted, the suite run, then the source restored and confirmed byte-identical.

```
NullTextureGuard                                   baseline  Failed: 0, Passed: 59
  M1  IsDrawMethodName always true                           Failed: 5
  M2  terminal picks SHORTEST overload                       Failed: 1
  M3  parameter-name rule dropped                            Failed: 2
  M4  IsPlumbingFrame always false                           Failed: 4

MissingTextureReport                               baseline  Failed: 0, Passed: 80
  M5  EndsWith -> Contains (substring not suffix)            Failed: 6
  M6  empty-remainder check dropped                          Failed: 1
  M7  case-sensitive comparison                              Failed: 3
  M8  artefact extension wrong (".png")                      Failed: 8
```

### API contract tests — new, and worth your attention

`UnityApiContractTests.cs` reads the **installed game's own assemblies** as metadata
(`MetadataLoadContext`, never executing them) and asserts the names every patch binds to. It skips
with `Assert.Ignore` when RimWorld is absent, so the repo still builds anywhere; set
`RIMWORLD_MANAGED` to point it elsewhere.

This exists because Harmony binds injected parameters **by name**, and a rename produces a patch that
reports itself installed and does nothing. That already happened here once: Unity spells it `miplevel`
on `GetPixels` but `mipLevel` on `GetPixel`/`GetPixelBilinear`. Both are now pinned.

Note the resolver detail: only the game's `Managed` directory is fed in. Adding the host framework
directory too supplies a second `mscorlib` and `MetadataLoadContext` refuses it.

---

## 5. Known gaps — please probe these hardest

1. **Never run in a live game.** The internal verdict is `test`, not `pass`. No before/after log count
   exists yet. Ant boots after your review.
2. **`Substituted` over-counts.** OnGUI runs at least twice per frame (Layout, then Repaint) and Unity
   only warns on Repaint. Gating on `Event.current.type` was rejected because this prefix sits *above*
   Unity's own `GUIUtility.CheckOnGUI()`, so `Event.current` may legitimately be null there — an NRE on
   the hottest path in the game is worse than a counter that reads high. Documented at the field.
3. **Per-call cost is reasoned, not measured.** Reduced from 3–4 prefix invocations per draw to 1.
   The real number needs the in-game run.
4. **Two analyzer suppressions, both the same underlying cause** — Unity overloads `==`/`!=` on
   `Object`, and neither Roslyn nor Meziantou models it, so they mis-analyse a *destroyed* texture as
   unreachable. `Texture2DReadPatches.cs:78,81` (CA1508) and `MissingTextureReport.cs` (`found!`).
   Both are documented in place. Please check I suppressed rather than "fixed" correctly — rewriting
   either to satisfy the analyzer would leak destroyed copies.
5. **`ModAttribution` is untested.** It needs `LoadedModManager`, which the test host cannot load.
6. The vehicle livery has still never been visually confirmed.

---

## 6. Repo state

```
HEAD        24c88da  Credit Taranchuk for Faster Game Loading's art (MIT)
remotes     none — nothing published
```

Uncommitted (everything above is working-tree only):

```
 M About/About.xml                                 new fix documented per Ant's standing rule
 M README.md                                       same
 M Assemblies/ImageOptCompat.dll                   repackaged Release
 M Source/ImageOptCompat/ImageOptCompatMod.cs      wiring + full settings UI
 M Source/ImageOptCompat/ImageOptCompatSettings.cs 12 toggles
 M Source/ImageOptCompat/Texture2DReadPatches.cs   toggle + CA1508 note
 M Source/ImageOptCompat.Tests/…csproj             + System.Reflection.MetadataLoadContext
?? Source/ImageOptCompat/NullTextureGuard.cs
?? Source/ImageOptCompat/MissingTextureReport.cs
?? Source/ImageOptCompat/ModAttribution.cs
?? Source/ImageOptCompat.Tests/NullTextureGuardTests.cs
?? Source/ImageOptCompat.Tests/MissingTextureReportTests.cs
?? Source/ImageOptCompat.Tests/UnityApiContractTests.cs
```

---

## 7. Commands

```bash
# build + package (the ONLY route into Assemblies/; refuses non-Release)
dotnet build Source/ImageOptCompat/ImageOptCompat.csproj -c Release -t:PackageRelease

# tests
dotnet test Source/ImageOptCompat.Tests/ImageOptCompat.Tests.csproj -c Release

# assert the packaged DLL is Release, from the bytes rather than the build log
python -c "b=open('Assemblies/ImageOptCompat.dll','rb').read(); print(b.find(b'\x01\x00\x07Release\x00\x00')>=0)"
```

Note for byte-level checks: .NET stores type and member names UTF-8 in `#Strings`, but **string
literals UTF-16 in `#US`**. A UTF-8 grep for a literal returns 0 and means nothing — that caught me
this session.

### Tools used here, if you want to reproduce any of it

- `ilspycmd` (on PATH) — decompiled `UnityEngine.GUI`, `Texture2D`, `Verse.ContentFinder<T>`,
  `Verse.BaseContent`, `FasterGameLoading.dll`, `HologramsAndProjectors.dll`. `-il` for raw IL.
- `python` — raw-byte reads of the packaged DLL for the `AssemblyConfiguration` blob.
- Image Opt **ships its C# and Rust source** at
  `steamapps/workshop/content/294100/3543873568/Source/` — read `TextureLoadPatch.cs`,
  `Texture2DPatch.cs`, `BatchedTextureCopier.cs` and `image_lib/src/lib.rs` from there rather than
  decompiling.

---

## 8. What would most help

Ant's sequence is: your review → she boots → green light → publish (GitHub + Steam, MIT).

The highest-value findings would be:
- a case where `TryCorrectPath` fires on a path it should leave alone;
- a caller that reaches Unity's null check **without** passing through the terminal `GUI.DrawTexture`,
  which would make the guard incomplete;
- anything about the transparent-substitute choice that makes it wrong for a real mod.
