namespace ImageOptCompat;

/// The mod's player-facing name, kept in one place.
///
/// Players see it in the mod list, the Mod Settings entry and every log line this patch writes. A
/// log line is only useful if it names a mod the player can find in that list, and the old short
/// tag "[ImageOptCompat]" matched nothing there. The namespace and the assembly keep the short form.
///
/// The packageId is DegradingAnt.RimCompat, chosen by Ant before the first Workshop upload; 0.2.0
/// used degradingant.imageoptcompat. Saved settings do not depend on it: RimWorld names the settings
/// file after the mod's folder and Mod class (Mod_{folder}_{class}.xml, in Mod.GetSettings).
///
/// ModMetadataTests pins Name to About.xml's &lt;name&gt; and HarmonyId to its &lt;packageId&gt;, so
/// neither can drift apart from the page.
internal static class ModInfo
{
    internal const string Name = "Image Opt + Faster Game Loading Compatibility Patch";

    /// Prefix for every log line this patch writes.
    internal const string Tag = "[" + Name + "]";

    /// The owner of every patch this mod installs. By convention the packageId: it is the name other
    /// mods and the Harmony mod's stack traces see, and the startup check asks Harmony which patches
    /// are live under it.
    internal const string HarmonyId = "DegradingAnt.RimCompat";
}
