using Verse;

namespace ImageOptCompat;

public sealed class ImageOptCompatSettings : ModSettings
{
    public bool vehicleReadback = true;
    public bool sweepOrphanZstd = true;
    public bool recompressCopies = true;
    public bool destroyOriginalTexture;
    public bool verbose;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref vehicleReadback, "vehicleReadback", true);
        Scribe_Values.Look(ref sweepOrphanZstd, "sweepOrphanZstd", true);
        Scribe_Values.Look(ref recompressCopies, "recompressCopies", true);
        Scribe_Values.Look(ref destroyOriginalTexture, "destroyOriginalTexture", false);
        Scribe_Values.Look(ref verbose, "verbose", false);
        base.ExposeData();
    }
}
