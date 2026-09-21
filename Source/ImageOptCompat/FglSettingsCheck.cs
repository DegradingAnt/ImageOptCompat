using System;
using System.Collections.Generic;
using System.Reflection;

namespace ImageOptCompat;

internal static class FglSettingsCheck
{
    private static readonly (string Member, bool Expected)[] Tested =
    {
        ("earlyModContentLoading", true),
        ("EnableMultiThreading", true),
        ("XPathCaching", true),
        ("DelayGraphicLoading", false),
        ("StaticAtlasesBaking", false),
    };

    internal static string? Differences(Type? type)
    {
        if (type == null) return "FasterGameLoadingSettings unavailable";
        var notes = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (var (member, expected) in Tested)
        {
            try
            {
                var field = type.GetField(member, flags);
                var property = type.GetProperty(member, flags);
                var value = field != null ? field.GetValue(null) : property?.GetValue(null, null);
                if (value is not bool actual)
                    notes.Add(member + " unavailable");
                else if (actual != expected)
                    notes.Add(member + "=" + actual);
            }
            catch (Exception e)
            {
                notes.Add(member + " unreadable: " + (e.InnerException ?? e).Message);
            }
        }
        return notes.Count == 0 ? null : string.Join(", ", notes);
    }
}
