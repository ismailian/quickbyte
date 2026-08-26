namespace QuickByte.Core.Enums;

/// <summary>
/// The days of the week a queue's schedule fires on.
///
/// A flags enum rather than a <c>List&lt;DayOfWeek&gt;</c> because this is
/// persisted state read by two processes — the app and the scheduler agent —
/// and a single integer is the one shape that cannot deserialize into a
/// half-populated collection. <see cref="Sunday"/> is deliberately
/// <c>1 &lt;&lt; (int)DayOfWeek.Sunday</c>, so the whole enum can be derived
/// from a <see cref="DayOfWeek"/> by shifting rather than by a switch.
/// </summary>
[Flags]
public enum ScheduleDays
{
    None = 0,

    Sunday = 1 << (int)DayOfWeek.Sunday,
    Monday = 1 << (int)DayOfWeek.Monday,
    Tuesday = 1 << (int)DayOfWeek.Tuesday,
    Wednesday = 1 << (int)DayOfWeek.Wednesday,
    Thursday = 1 << (int)DayOfWeek.Thursday,
    Friday = 1 << (int)DayOfWeek.Friday,
    Saturday = 1 << (int)DayOfWeek.Saturday,

    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    EveryDay = Weekdays | Weekend
}
