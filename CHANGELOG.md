# Changelog

## Unreleased

- **Double-extension path fix is now complete for the full cache path too.** It previously corrected a
  path ending `.dds` (a mod that stripped `.zstd` off Image Opt's `name.dds.zstd`). It now also corrects a
  path built from the cache file's whole name (`name.dds.zstd`), so a mod that used the full cache filename
  instead of stripping one extension is rescued as well. The rule stays narrow: only the two Image Opt
  artefact suffixes, never a content path, and the corrected path can never end in another artefact
  suffix, so the one-retry cannot recurse.
- **Null-texture guard is now discoverable.** It had been running silently, so a flood was only visible by
  opening the settings page. Once 50+ null draws are intercepted it logs a one-line hint pointing at the
  'Copy diagnostic report' button and the 'Show a placeholder' option. One hint per session.
- **The generic pixel-readback now reports how often it served a read.** The feature has counted
  every read it answered from a CPU-readable copy since it was added, but the counter was never
  shown. It is now a read-out in the settings page ("This session") next to the vehicle readback
  count, and it resets to zero on content teardown so it never describes copies from a previous
  content set. Added a logic test covering the reset.

- **Harmony version is now checked alongside Image Opt and Faster Game Loading.** The patch reflects into
  both mods' internals, so a renamed field in a newer Harmony can silently switch part of the patch off.
  The tested Harmony version (`2.4.2.0`) is now compared at startup too, and an untested Harmony is reported
  in the same 'Untested versions' line. The version comparison was extracted into a Verse-free `VersionCheck`
  helper (kept out of `ImageOptCompatMod` so it runs under the test host without `Assembly-CSharp`) and is
  covered by new unit tests, including the new Harmony case.

- **Runtime behaviour tests added for the failed-audio crash guard and mod attribution.** The crash guard
  now proves, at runtime, that a decode-failed clip is reported missing while Unloaded/Loading clips are
  kept and the toggle works; the attribution caller-walk is covered too.

## 0.2.0

- Added cached CPU-readable copies for Image Opt textures that other mods need to read.
- Added vehicle texture readback support for Vehicle Framework and compatible vehicle packs.
- Added loading guards for Vanilla Expanded Framework and Worldbuilder.
- Added the Image Opt cache path fix and cleanup for orphaned `.dds.zstd` files.
- Added the missing-texture report and null-texture guard settings.
- Added startup checks for the Faster Game Loading Preview build and its tested settings.
- Added load-order hints for the required mods and Sarcho Turtle.
- Added tests for the patch logic and the installed RimWorld/Unity API surface.
- Release checks include 146 automated tests and a warning-free Release package build.
