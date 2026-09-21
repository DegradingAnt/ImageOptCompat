# Boot test — 2026-09-21

**Automated checks pass. The user is running the live boot test. Do not publish yet.**

The tested Release DLL is already installed in RimWorld/Mods/ImageOptCompat.
The active mod list was checked and left unchanged. There is no need to run the
installer again for this test.

SHA256: `5B5325EF575667AAC0289F4F66B81A077F03236DD59B2B79D52ECE02B8F8CD60`

- 85 unit/API-contract tests and 24 source-logic tests passed; none skipped.
- Production analyzer rebuild: zero warnings and errors.
- Existing NU1702 warning remains in the cross-framework unit-test project.
- The previous installed DLL is backed up in the 2026-09-21 review artifacts.
- Full findings and integration checks: [REVIEW-2026-09-21.md](REVIEW-2026-09-21.md).

Check in game:

1. Reach the main menu and load the intended save. Look for Harmony installation
   errors, repeating root exceptions and ImageOptCompat warnings.
2. Inspect vehicle paint/livery and turret textures, plus a hologram selection.
3. Open the compatibility mod settings. Scroll to the diagnostic controls and
   clipboard report button. Recording should remain off unless enabled.
4. Compare ordinary and higher game speeds. Check whether the null-texture warning
   flood stops. Do not infer a numerical TPS gain without measuring it.
5. If practical, exercise a language/content reload and repeat the texture checks.

Report what passed and any new errors. Actual GPU output and full-pack interactions
cannot be certified by the offline source stand-ins. Publishing stays on hold until
the user reports a green result for this installed DLL.
