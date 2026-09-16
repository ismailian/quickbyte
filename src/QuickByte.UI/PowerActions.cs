using System.Diagnostics;
using System.Windows.Forms;
using QuickByte.Core.Enums;

namespace QuickByte.UI;

/// <summary>
/// Carries out a <see cref="PowerAction"/>: sleep, hibernate, shut down. Exiting
/// QuickByte is the one action that is not here — it belongs to
/// <see cref="Forms.MainForm"/>, whose exit path pauses the downloads first.
///
/// Nothing in this file needs native interop. Sleep and hibernate are
/// <see cref="Application.SetSuspendState"/>, which WinForms already wraps, and
/// shutting down is Windows' own <c>shutdown.exe</c> — which also means a
/// shutdown this starts behaves like any other: it is not forced, so an app with
/// unsaved work still gets to say so.
/// </summary>
internal static class PowerActions
{
    /// <summary>
    /// How long the countdown in front of an action lasts. Long enough to reach the
    /// keyboard from across the room for anything that turns the machine off;
    /// shorter to close an app.
    /// </summary>
    public static int CountdownSeconds(PowerAction action) => action == PowerAction.ExitApp ? 15 : 60;

    /// <summary>The choice as a drop-down shows it.</summary>
    public static string Label(PowerAction action) => action switch
    {
        PowerAction.ExitApp => "Exit QuickByte",
        PowerAction.Sleep => "Put the computer to sleep",
        PowerAction.Hibernate => "Hibernate the computer",
        PowerAction.ShutDown => "Shut down the computer",
        _ => "Do nothing"
    };

    /// <summary>The countdown's sentence: "QuickByte will … in 42 seconds."</summary>
    public static string Verb(PowerAction action) => action switch
    {
        PowerAction.ExitApp => "exit",
        PowerAction.Sleep => "put the computer to sleep",
        PowerAction.Hibernate => "hibernate the computer",
        PowerAction.ShutDown => "shut down the computer",
        _ => "do nothing"
    };

    /// <summary>The countdown's "do it now" button.</summary>
    public static string NowButton(PowerAction action) => action switch
    {
        PowerAction.ExitApp => "Exit Now",
        PowerAction.Sleep => "Sleep Now",
        PowerAction.Hibernate => "Hibernate Now",
        PowerAction.ShutDown => "Shut Down Now",
        _ => "OK"
    };

    public static IReadOnlyList<PowerAction> All { get; } = new[]
    {
        PowerAction.None, PowerAction.ExitApp, PowerAction.Sleep, PowerAction.Hibernate, PowerAction.ShutDown
    };

    /// <summary>
    /// <c>shutdown.exe</c> by its full path in the system directory, never by name:
    /// a name is resolved against the working directory and PATH first, and a
    /// download manager's working directory is a very good place to find a file
    /// called shutdown.exe that someone else put there.
    /// </summary>
    internal static ProcessStartInfo ShutdownCommand() =>
        new(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /t 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

    /// <summary>
    /// Sleeps, hibernates or shuts down. Returns whether Windows accepted it — a
    /// machine with hibernation switched off refuses, and saying so beats leaving
    /// the user to find a computer still on.
    /// </summary>
    public static bool Perform(PowerAction action)
    {
        try
        {
            switch (action)
            {
                case PowerAction.Sleep:
                    return Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false);

                case PowerAction.Hibernate:
                    return Application.SetSuspendState(PowerState.Hibernate, force: false, disableWakeEvent: false);

                case PowerAction.ShutDown:
                    using (var process = Process.Start(ShutdownCommand()))
                        return process is not null;

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }
}
