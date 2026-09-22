namespace ImageOptCompat;

/// The mod's player-facing name, kept in one place.
///
/// Players see it in the mod list, the Mod Settings entry and every log line this patch writes. A
/// log line is only useful if it names a mod the player can find in that list, and the old short
/// tag "[ImageOptCompat]" matched nothing there. Code identifiers keep the short form (the
/// namespace, the assembly, packageId degradingant.imageoptcompat and the Harmony id). Changing the
/// packageId would orphan every saved settings file.
///
/// ModInfoTests pins Name to About.xml's &lt;name&gt;, so the two cannot drift apart.
internal static class ModInfo
{
    internal const string Name = "Image Opt + Faster Game Loading Compatibility Patch";

    /// Prefix for every log line this patch writes.
    internal const string Tag = "[" + Name + "]";
}
