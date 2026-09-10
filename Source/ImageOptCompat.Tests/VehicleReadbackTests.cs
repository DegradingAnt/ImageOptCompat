using ImageOptCompat;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

[TestFixture]
public class VehicleReadbackTests
{
    [TestCase("smashphil.vehicleframework")]
    [TestCase("oels.vehiclemapframework")]
    [TestCase("oskarpotocki.vanillavehiclesexpanded")]
    [TestCase("SmashPhil.VehicleFramework")]   // ModContentPack.PackageId casing must not matter
    public void KnownVehicleMods_NeedReadback(string id) =>
        Assert.That(VehicleReadback.NeedsCpuReadback(id), Is.True);

    [TestCase("ludeon.rimworld")]
    [TestCase("brrainz.harmony")]
    [TestCase("dev.soeur.imageopt")]
    [TestCase("")]
    public void OtherMods_DoNot(string id) =>
        Assert.That(VehicleReadback.NeedsCpuReadback(id), Is.False);
}
