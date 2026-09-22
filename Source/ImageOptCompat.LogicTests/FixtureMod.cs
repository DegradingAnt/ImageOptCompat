using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

// Plays the part of another mod's code. Its namespace is not plumbing, and tests register this
// assembly under a fixture ModContentPack, so attribution has a real mod frame to find. Every
// method is NoInlining: attribution reads stack frames, and an inlined method has none.
namespace FixtureMod;

public static class Window
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void DrawNull(Action draw) => draw();
}

public static class Loader
{
    /// Throws a NullReferenceException from this mod's own code, as the boot-2 flood did.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Crash(string? value) => value!.Length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Texture2D? LoadMissingIcon() => ContentFinder<Texture2D>.Get("FixtureMod/MissingIcon");
}
