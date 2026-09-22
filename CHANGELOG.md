# Changelog

## Unreleased — 2026-09-22 review fixes

- Fixed the texture/audio lookup mix-up: removed both Harmony patches on `ContentFinder<T>.Get`.
  RimWorld's Mono shares that method across reference types, so a texture patch redirected audio
  lookups into textures and the audio patch redirected texture lookups into audio. Reproduced using
  the installed Mono and Harmony. Texture repair now uses the non-generic resource fallback, and
  missing-texture reporting observes final errors without suppressing them. Existing mod, resource
  and bundle assets keep priority over corrected paths; optional probes remain unreported.
- Moved the failed-audio guard to the non-generic sound-grain constructor. It now checks both Unity
  and RimWorld's `RuntimeAudioClipLoader` state, and also covers folder/custom grains. Failed clips
  use one reusable silent clip before their length is read; Loading/Unloaded clips stay intact.
- Kept the vehicle-holder key snapshot and corrected its explanation: the installed Mono dictionary
  invalidates enumeration on an existing-key overwrite, even without another writer. The earlier
  claim that removing this snapshot was safe was wrong.
- Check the main thread before reading IMGUI's `Event.current`. Count cached pixel reads as well
  as first reads in the session counter. Show the audio guard as disabled when its toggle is off.
- Make asset-scan owner selection deterministic and cap distinct mods instead of file hits, so
  several hits in one mod cannot hide other candidates. Include the matching file in the report
  and avoid claiming a negative scan proves the path was constructed at runtime.
- Added a separate regression runner using the installed game's Mono and Harmony, including a
  reproduction of the former generic-patch bug. Unity native rendering/audio remain stand-ins.

- Earlier performance changes retained: read `Event.current` once per intercepted draw and check
  texture-folder existence in parallel. The attempted vehicle key-snapshot removal was reverted
  after it broke loading; it was not a behavior-preserving optimization.

- **Double-extension path fix is now complete for the full cache path too.** It previously corrected a
  path ending `.dds` (a mod that stripped `.zstd` off Image Opt's `name.dds.zstd`). It now also corrects a
  path built from the cache file's whole name (`name.dds.zstd`), so a mod that used the full cache filename
  instead of stripping one extension is rescued as well. The rule stays narrow: only the two Image Opt
  artefact suffixes. A thread-local guard limits correction to one retry even for repeated suffixes.
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
