namespace QuickByte.Core.Enums;

/// <summary>
/// The small things a download can ask for when it finishes, beside a
/// <see cref="PowerAction"/>. Flags, because they combine.
/// </summary>
[Flags]
public enum CompletionActions
{
    None = 0,

    /// <summary>Show the finished file in Explorer.</summary>
    OpenFolder = 1,

    /// <summary>Play the system notification sound — for a download someone is waiting on from across the room.</summary>
    PlaySound = 2
}
