using System.Runtime.CompilerServices;

// Plays the part of the game's own code: a helper that several mods call, and that throws. The test
// assembly is left unregistered when this is used, so the frame belongs to no mod, as a game
// helper's frame would not.
namespace FixtureGame;

public static class Helper
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Fail() => throw new InvalidOperationException("shared helper failed");
}
