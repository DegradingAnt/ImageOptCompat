using System;
using System.Reflection;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

public class FglSettingsCheckTests
{
    private static class Settings
    {
        public static bool earlyModContentLoading = true;
        public static bool EnableMultiThreading { get; set; } = true;
        public static bool XPathCaching { get; set; } = true;
        public static bool DelayGraphicLoading { get; set; }
        public static bool StaticAtlasesBaking { get; set; }
    }

    [SetUp]
    public void Reset()
    {
        Settings.earlyModContentLoading = Settings.EnableMultiThreading = Settings.XPathCaching = true;
        Settings.DelayGraphicLoading = Settings.StaticAtlasesBaking = false;
    }

    [Test]
    public void DefaultsAreRecognized() => Assert.That(FglSettingsCheck.Differences(typeof(Settings)), Is.Null);

    [TestCase("earlyModContentLoading", false)]
    [TestCase("EnableMultiThreading", false)]
    [TestCase("XPathCaching", false)]
    [TestCase("DelayGraphicLoading", true)]
    [TestCase("StaticAtlasesBaking", true)]
    public void ChangedFieldOrPropertyIsReported(string member, bool value)
    {
        if (member == "earlyModContentLoading") Settings.earlyModContentLoading = value;
        else typeof(Settings).GetProperty(member, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, value);
        Assert.That(FglSettingsCheck.Differences(typeof(Settings)), Does.Contain(member + "=" + value));
    }

    [Test]
    public void MissingTypeAndMembersAreReported()
    {
        Assert.That(FglSettingsCheck.Differences(null), Does.Contain("unavailable"));
        Assert.That(FglSettingsCheck.Differences(typeof(object)), Does.Contain("EnableMultiThreading unavailable"));
    }

    private static class BrokenSettings
    {
        public static bool EnableMultiThreading => throw new InvalidOperationException("fixture failure");
    }

    [Test]
    public void GetterFailureIsReportedWithoutThrowing() =>
        Assert.That(FglSettingsCheck.Differences(typeof(BrokenSettings)), Does.Contain("EnableMultiThreading unreadable: fixture failure"));
}
