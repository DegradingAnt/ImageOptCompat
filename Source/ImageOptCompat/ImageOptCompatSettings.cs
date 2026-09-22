using Verse;

namespace ImageOptCompat;

public sealed class ImageOptCompatSettings : ModSettings
{
    public bool genericPixelReadback = true;
    public bool earlyUiGuards = true;
    public bool vehicleReadback = true;
    public bool sweepOrphanZstd = true;
    public bool nullTextureGuard = true;
    public bool nullTextureShowPlaceholder;
    public bool nullTextureDeepDiagnostic;
    // ON by default: this is a real fix, not a diagnostic. It repairs lookups that Image Opt's
    // own cache files broke, and it only ever runs after a lookup has already failed.
    public bool fixDoubleExtensionPaths = true;

    // OFF by default: recording every failed lookup is a diagnostic to switch on while hunting a
    // fault, not a permanent passenger.
    public bool reportMissingTextures;

    // ON by default: this prevents a hard crash, and it only acts on a clip Unity has already
    // reported as failed to decode. Nothing that plays today stops playing.
    public bool guardFailedAudioClips = true;
    public bool recompressCopies = true;
    public bool destroyOriginalTexture;
    public bool verbose;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref genericPixelReadback, "genericPixelReadback", true);
        Scribe_Values.Look(ref earlyUiGuards, "earlyUiGuards", true);
        Scribe_Values.Look(ref vehicleReadback, "vehicleReadback", true);
        Scribe_Values.Look(ref sweepOrphanZstd, "sweepOrphanZstd", true);
        Scribe_Values.Look(ref nullTextureGuard, "nullTextureGuard", true);
        Scribe_Values.Look(ref nullTextureShowPlaceholder, "nullTextureShowPlaceholder", false);
        Scribe_Values.Look(ref nullTextureDeepDiagnostic, "nullTextureDeepDiagnostic", false);
        Scribe_Values.Look(ref fixDoubleExtensionPaths, "fixDoubleExtensionPaths", true);
        Scribe_Values.Look(ref reportMissingTextures, "reportMissingTextures", false);
        Scribe_Values.Look(ref guardFailedAudioClips, "guardFailedAudioClips", true);
        Scribe_Values.Look(ref recompressCopies, "recompressCopies", true);
        Scribe_Values.Look(ref destroyOriginalTexture, "destroyOriginalTexture", false);
        Scribe_Values.Look(ref verbose, "verbose", false);
        base.ExposeData();
    }
}
