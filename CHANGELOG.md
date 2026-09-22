# Changelog

Every notable change to this mod, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Version numbers match `modVersion` in
`About/About.xml`. Each entry describes the change against the previous published version, not the
steps taken in between.

## [Unreleased]

Nothing yet.

## [0.3.0] - 2026-09-22

First Workshop release. Boot-tested on the full Progression pack (about 1,485 mods) over a session
of about two hours. See "Measured results" in the README.

### Added
- **Report level setting.** Choose how much the mod tells you. "Game-breaking problems only" writes
  just those, and only to the log. "Important problems" is the default: game-breaking problems plus
  problems one of this mod's settings can fix, shown once on screen after loading and written to
  the log. "Everything" also logs what each fix did. This replaces the old "Verbose logging"
  switch; a saved "verbose" setting carries over as Everything.
- **Main-menu status line.** Like the Harmony mod's version line, a faded line in the main-menu
  corner says whether the mod found problems. Hover over it for details. Problems found while
  loading are shown once, together, in one dialog.
- **Startup check.** While the game loads, the mod checks that every enabled fix is actually in
  place, and that Harmony-patched methods resolve so reports name the right mod. It also checks
  that the Faster Game Loading build, its settings and the mod versions are the tested ones. The
  main-menu status line shows the result, for example "all 9 startup checks passed", and hovering
  over it lists each check. The loading screen says what it is doing, the vanilla way, so Loading
  Progress shows it too.
- **Sound file loading repair.** Many audio editors save WAV files with an "extensible" header,
  even for ordinary 16-bit stereo sound. RimWorld's decoder rejects that header, the sound stays
  silent, and the log shows a misleading "Value cannot be null" error. The mod now reads such files
  as the plain PCM they are, in memory only; nothing on disk changes. In the test pack this brings
  back all 8 sounds of the Hamster mod. When a sound truly cannot be decoded, the game now logs the
  real reason instead of that error. It has its own switch.
- **Failed-audio guard.** A sound the game cannot decode now plays as silence, instead of having its
  length read, which can crash Unity's audio code. It checks both Unity's and RimWorld's own decoder
  state, and covers clip, folder and custom sound grains. Works with or without Image Opt, and has
  its own switch.
- **"Scan mods" button.** Searches every active mod's XML and assemblies for the recorded missing
  asset paths and names the mod and file that mention each one. It is slow, so it only runs when
  pressed.
- **Harmony version check.** Harmony is now checked at startup alongside Image Opt and Faster Game
  Loading, and an untested version is reported.
- **Null-texture flood hint.** Once 50 null draws have been intercepted, one log line points at the
  diagnostic report and the placeholder option. Before, a flood was only visible on the settings
  page.
- **Full cache-name repair.** The double-extension repair also fixes paths built from the cache
  file's whole name (`name.dds.zstd`), not only `name.dds`.
- **Readback counter.** The settings page shows how many pixel reads the generic readback served
  this session, cached reads included.

### Changed
- **The mod uses its full name everywhere players see it.** The Mod Settings entry said
  "ImageOptCompat", and every log line began with "[ImageOptCompat]". Neither matched anything in
  the mod list. Both now read "Image Opt + Faster Game Loading Compatibility Patch". The package id
  is unchanged, so saved settings carry over.
- Texture repair now hooks the game's non-generic resource fallback. The missing-texture report
  watches the game's final error lines and never suppresses them.
- The orphan sweep checks texture folders in parallel.
- All log output goes through one place and follows the report level. A problem that can break
  loading is now logged as an error, as the Harmony mod does; other problems remain warnings.

### Fixed
- **Audio lookups were being run as texture lookups.** RimWorld's Mono runtime shares
  `ContentFinder<T>.Get` between asset types, so 0.2.0's texture hook also caught sound and music
  lookups. One session logged 2,625 missing-texture errors, most of them song and sound names. The
  hook is gone, and the boot test logged none.
- **Diagnostics named the wrong mod.** The null-texture and missing-texture reports read the call
  stack, where a Harmony-patched method shows up as generated code. The first live boot blamed
  WanderJoinsPlus for Harmony's own frames, because Harmony's DLL is listed under every mod that
  ships its own copy (104 mod folders in the test install). Reports now look through patched
  methods and never credit a shared DLL to one mod. When no single mod is on the stack, they say
  so.
- The double-extension repair also looks through patched methods, so another mod patching
  `ContentFinder<T>.Get` can no longer switch it off unnoticed.
- The "Scan mods" button no longer tells players to turn on a setting that is already on when
  nothing is missing.
- The early-load guards no longer switch off silently when Vanilla Expanded Framework or
  Worldbuilder changes the method or field they guard. That is now reported.
- Reports keep calling RimWorld's own code "RimWorld (core)" even when mods ship copies of the
  game's assembly. 50 mod folders in the test install do.

### Release checks
- 125 unit and contract tests, 52 logic tests, and the 30-check regression runner, which executes
  real Harmony patches on the installed game's Mono runtime. The Release build has no warnings.

## [0.2.0] - 2026-09-21

Published on GitHub only.

- Added cached CPU-readable copies for Image Opt textures that other mods need to read.
- Added vehicle texture readback support for Vehicle Framework and compatible vehicle packs.
- Added loading guards for Vanilla Expanded Framework and Worldbuilder.
- Added the Image Opt cache path fix and cleanup for orphaned `.dds.zstd` files.
- Added the missing-texture report and null-texture guard settings.
- Added startup checks for the Faster Game Loading Preview build and its tested settings.
- Added load-order hints for the required mods and Sarcho Turtle.
- Added tests for the patch logic and the installed RimWorld/Unity API surface.
- Release checks include 146 automated tests and a warning-free Release package build.

[Unreleased]: https://github.com/DegradingAnt/ImageOptCompat/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/DegradingAnt/ImageOptCompat/compare/f3b267e...v0.3.0
[0.2.0]: https://github.com/DegradingAnt/ImageOptCompat/tree/f3b267e
