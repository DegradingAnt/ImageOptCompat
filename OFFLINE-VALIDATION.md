# Offline validation — ImageOptCompat 0.2.0

Date: 2026-09-10. Repository HEAD: 24c88da.
Result: **FAIL — release validation has unresolved findings.**

Validated the unchanged packaged DLL at C:/Users/linde/source/repos/ImageOptCompat/Assemblies/ImageOptCompat.dll.
SHA256: 35D147CC749D1FEA508F8C613BA645CA40D57A7308B5B1C617385101421D7F5A.
The same hash was checked again after validation; the project DLL and production source were not changed.

## Results

| Check | Result | Evidence |
|---|---|---|
| Offline restore and Release build | Passed using cached packages and a NuGet.Config with all package sources cleared; audit network requests disabled | restore.log, tests.log |
| Existing NUnit tests | 19 passed, 0 failed | results/unit-tests.trx |
| Installed-assembly metadata checks | 16 passed, 5 failed | metadata.log |
| Targeted source-logic tests | 15 passed, 3 failed | probes.log |
| Packaged code freshness | All 84 method bodies, signatures, locals and exception regions match a fresh **Debug** build | metadata.log |
| Actual Unity rendering / Harmony installation / complete mod pack | Not executed | Outside this offline validation |

All evidence and the reusable harness are under:
C:/Users/linde/.codex/visualizations/2026/09/09/01a085f3-370e-71b2-8728-21127f9ff1f3/offline-validation

The net9.0 test project's reference to the net481 production project emits NU1702. The validation harness builds cleanly under the locally installed .NET 10 SDK. Its initial offline setup needed UseAppHost=false to avoid an uncached executable-host package; no package source was enabled or download performed.

## Findings for Claude

### P2 — Four Faster Game Loading configuration checks silently do nothing

Source/ImageOptCompat/ImageOptCompatMod.cs:24-31,94 reflects lower-case field names. In the installed FGL 2026.09.07.1 DLL, only earlyModContentLoading is a matching field. The other values are public static Boolean **properties**:

| Requested field | Actual property |
|---|---|
| enableMultiThreading | EnableMultiThreading |
| xPathCaching | XPathCaching |
| delayGraphicLoading | DelayGraphicLoading |
| staticAtlasesBaking | StaticAtlasesBaking |

AccessTools.Field returns null for these four names, and the null-conditional read silently excludes them. Changing these settings therefore does not produce the promised untested-configuration warning. This is confirmed against the installed DLL, not just source comments. Read the correctly named properties (and the one field), and report unresolved members rather than treating them as tested defaults.

### P2 — Cached copies are not recreated after Unity destroys them

Source/ImageOptCompat/Texture2DReadPatches.cs:59 returns the cached Texture2D without checking Unity's destroyed-object/null semantics. If its native object is destroyed while the source is still alive, later prefixes see r == null and invoke the original unreadable texture method; the cache never retries allocation. A source-level test modeling Unity's overloaded equality reproduces this behavior. Remove invalid cache entries and recreate the copy. This establishes the broken recovery path, not that a particular installed mod currently destroys these hidden copies.

### P2 — The destination still leaks when an initializer property throws

Source/ImageOptCompat/VehicleReadback.cs:116-122 creates a Texture2D in an object initializer. The assignment to outer dst happens only after every property assignment succeeds. Injecting a failure from src.filterMode leaves the allocated destination unreachable to the finally cleanup. ReadPixels, Apply, Compress and Blit failure tests all pass; this narrower initializer path fails. Assign the newly constructed texture to dst first, then set its properties.

### Lifecycle concern — Weak-table collection does not explicitly release native storage

Source/ImageOptCompat/Texture2DReadPatches.cs:33,61-63 has no destruction path for cache-owned textures. The test collects a weak source key while tracking simulated native allocations separately; the native-copy allocation remains because no Destroy method is called. This confirms the absence of explicit cleanup under the modeled ownership contract. It is **not** a measurement of Unity VRAM leakage: Resources.UnloadUnusedAssets may later recover unused textures. Add a main-thread content-teardown path and validate it with real Unity resource lifetime before making a memory claim.

### P2 — The distributed assembly is Debug, and the documented test command can overwrite Release

The packaged DLL's AssemblyConfiguration is Debug, and DebuggableAttribute includes DisableOptimizations. A fresh isolated Debug build matches all 84 method bodies. This is current code, but it is not the Release artifact previously claimed.

Source/ImageOptCompat/ImageOptCompat.csproj:8 sends both Debug and Release output to the same ../../Assemblies directory. README's sequence builds Release and then runs plain dotnet test, whose default Debug project-reference build writes into that same directory. Thus the documented validation sequence can replace the intended release DLL with Debug. Use configuration-specific intermediate outputs and an explicit final Release packaging step, or consistently test with -c Release, then assert the packaged configuration. No claim is made that Debug itself causes the observed texture failures.

## What passed

- All eight declarative Harmony patch classes in the shipped DLL resolve to installed game methods. These comprise seven Texture2D overloads plus ModContentPack.AnyContentLoaded.
- Prefix/postfix instance types, result types, argument names/types and the injected textures field agree with the installed APIs. Each target is managed. This is metadata compatibility, not proof of successful runtime detouring.
- Image Opt 0.1.13 exposes the expected static native-texture HashSet<int>.
- FGL's ImageOptEarlyLoadCoordinator exists; the earlyModContentLoading field matches.
- VEF and Worldbuilder early-UI method signatures and guarded fields match the installed DLLs.
- The direct five-argument GetPixels overload is native, confirming the documented coverage gap.
- The actual source, linked unchanged into a harness with explicit framework stand-ins, passes worker-thread deferral, per-texture cache reuse, all seven read dispatch cases, retry after readback failure, original-texture preservation, and aligned/unaligned compression decisions.
- Render target restoration and temporary/destination cleanup pass when Blit, ReadPixels, Apply or Compress throws.
- Holder replay passes after an empty call, after an off-thread call and after a new holder is constructed for the same package; repeated calls do not reconvert the same holder.
- File-based sweep fixtures confirm resolved nested folders are scanned, source-backed .dds.zstd and plain .dds are preserved, disabled Image Opt prevents forced deletion, counters reset, and an empty first pass does not latch the sweep.
- Early-UI stand-ins confirm null dependencies are skipped, vanilla RNG still runs, and initialized dependencies are allowed through.

## Scope and next validation

No game process was launched, saves loaded, mod list changed, installed texture cache swept, or asset published. No web access or package download was used. The installed game calls SteamManager.InitIfNeeded unconditionally from Root.CheckGlobalInit; a game boot was not treated as an offline-only check.

A full release decision still needs actual Unity/Harmony execution: load the final packaged Release DLL, verify a vehicle livery/turret and a non-vehicle pixel reader, then repeat content/language reloads while observing texture memory. The present run cannot certify every interaction in the full mod pack.

The user-approved art is already installed as About/Preview.png; the older review's statement that its palette is pending is superseded.
