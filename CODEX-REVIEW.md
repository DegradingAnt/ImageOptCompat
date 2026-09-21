# Quick review — ImageOptCompat

Reviewed 2026-09-10, HEAD 24c88da (including generic pixel-read patches from 60cdbcf). Working tree was clean. No game launch, DLL deployment, or publishing performed.

## Findings for Claude

### P2 — Give cached Unity copies an explicit lifetime

Source/ImageOptCompat/Texture2DReadPatches.cs:33,59-63 stores newly allocated Texture2D copies in a ConditionalWeakTable, but no code destroys these cache-owned textures. Weak managed references do not themselves destroy Unity native allocations. Unlike the vehicle-holder replacements, these copies are not part of ModContentHolder cleanup. Reading additional native textures accumulates CPU-readable copies; dropping keys or rebuilding holders does not explicitly release their native storage. UnloadUnusedAssets may eventually recover unused resources, but the patch has no deterministic cleanup. Add main-thread teardown for cache-owned copies when content is cleared, and ensure destroyed cached values can be recreated. Do not destroy Image Opt's source textures. This is a source-level lifecycle finding, not a measured VRAM regression.

### P3 — Update README to describe the actual release

README.md:83 says the measured pre-rename build has identical logic, but 60cdbcf introduces seven generic Texture2D read patches and a new copy cache. Those performance/boot results do not validate that path. Line 88 still describes the hardcoded vehicle list as the general coverage limit, whereas About.xml now describes the generic fix. Line 55 promises everything is disabled without Image Opt, while ImageOptCompatMod installs EarlyUiGuards unconditionally. Update these together so testers know what changed and what remains untested.

## Additional edge case

VehicleReadback.cs:116 still assigns dst using an object initializer. If a Unity property getter/setter inside the initializer throws after construction, the outer dst remains null and finally cannot destroy the new allocation. Separate the allocation assignment from property assignments to fully cover that failure path. This is a narrow exception-path observation; no live reproduction was attempted.

## Validation

- Built Release and ran the test project from an isolated source copy: 19 passed, 0 failed.
- The test invocation emits NU1702 because net9.0 tests reference the net481 project. This is not a failing test, but the full test invocation is not warning-free.
- Existing tests cover pure decision logic, not Harmony installation or Unity readback/copy lifetime.
- Source inspection confirms earlier holder-lifetime, resolved-folder and inactive-sweep guards are present.
- Runtime release validation remains outstanding: boot with the actual release, inspect a vehicle livery/turret, exercise a non-vehicle pixel reader, and repeat a language/content reload while observing memory.

## Preview image

Generated with the built-in image tool from both authors' supplied artwork. Preview-v2.png is a sibling candidate, leaving the existing Preview.png intact while Ant's requested palette is pending. Uses cream/charcoal, source crab colours and FGL green. Exact generation prompt is in PREVIEW-PROMPT.txt. Generated PNG is about 2.37 MB; no upload attempted, so Workshop upload-size acceptance has not been checked.
