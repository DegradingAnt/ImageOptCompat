namespace ImageOptCompat;

/// How much this mod tells the player. One setting, chosen on the settings page.
public enum ReportLevel
{
    /// Only game-breaking problems, and only in the log.
    Quiet = 0,

    /// The default. Game-breaking problems and problems a setting here can fix, on screen once
    /// after loading and in the log. Handled problems and hints go to the log only.
    Important = 1,

    /// Everything above, plus what each fix did. For checking that a fix is running at all.
    Everything = 2,
}
