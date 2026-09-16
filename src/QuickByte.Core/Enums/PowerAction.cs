namespace QuickByte.Core.Enums;

/// <summary>
/// What to do with the computer — or with QuickByte — once a download or a queue
/// is finished. The point of most of them is the unattended night: a queue
/// scheduled for 01:00 is not much use if the machine then stays on until
/// morning.
///
/// Core only records the choice. Doing it is a Windows matter and belongs to the
/// UI project, which also puts a countdown in front of every one of them: none of
/// these should happen to someone who is sitting at the machine and did not
/// expect it.
/// </summary>
public enum PowerAction
{
    None = 0,
    ExitApp = 1,
    Sleep = 2,
    Hibernate = 3,
    ShutDown = 4
}
