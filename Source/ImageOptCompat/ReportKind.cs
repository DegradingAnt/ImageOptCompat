namespace ImageOptCompat;

/// What a message is about. The kind, not the call site, decides where it goes.
public enum ReportKind
{
    /// The game may black-screen or crash. Logged even at Quiet.
    Breaking,

    /// Part of this mod is not working, or one of its settings would fix what is happening. The
    /// text says what to do.
    Problem,

    /// Something went wrong elsewhere and was handled: an untested version, a broken file in
    /// another mod, the mod behind a missing texture. Worth a line in the log, not a popup.
    Notice,

    /// A pointer to a feature, such as the flood hint. Logged as a plain message, not a warning.
    Hint,

    /// What a fix did: counts, sweep results, check results. Only at Everything.
    Info,
}
