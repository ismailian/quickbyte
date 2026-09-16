using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Enums;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// The last chance to stop a <see cref="PowerAction"/>: a countdown with Cancel on
/// the default button.
///
/// These actions are set up hours ahead — "shut down when the queue finishes" —
/// and the person who chose them may be back at the keyboard by the time they
/// fire, in the middle of something. So nothing turns a machine off without first
/// saying so on screen, for long enough to reach it. Cancel is the default and the
/// Escape key: a stray Enter must not be the thing that shuts the computer down.
/// </summary>
public sealed class PowerActionCountdownForm : Form
{
    private readonly PowerAction _action;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _secondsLeft;
    private Label _messageLabel = null!;

    /// <summary>True once the action should go ahead: the countdown ran out, or the user asked for it now.</summary>
    public bool Confirmed { get; private set; }

    public PowerAction Action => _action;

    /// <param name="reason">What finished — "Nightly finished", "setup.exe finished downloading".</param>
    /// <param name="stillRunning">Downloads still transferring, which the action will pause.</param>
    public PowerActionCountdownForm(PowerAction action, string reason, int stillRunning)
    {
        _action = action;
        _secondsLeft = PowerActions.CountdownSeconds(action);

        BuildUi(reason, stillRunning);

        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) => _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();

        // Last, once every control exists: see Theme.Attach.
        Theme.Attach(this);
    }

    private void BuildUi(string reason, int stillRunning)
    {
        Text = "QuickByte";
        Width = 500;
        Height = 290;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 14, 24, 8), BackColor = Theme.Surface };

        _messageLabel = new Label { Dock = DockStyle.Top, Height = 30, ForeColor = Theme.Text, Font = Theme.Ui };

        if (stillRunning > 0)
        {
            // Docked below the message; added first so it lays out after it.
            body.Controls.Add(new Label
            {
                Text = stillRunning == 1
                    ? "1 other download is still running. It will be paused and can be resumed later."
                    : $"{stillRunning} other downloads are still running. They will be paused and can be resumed later.",
                Dock = DockStyle.Top,
                Height = 40,
                ForeColor = Theme.Warning,
                Font = Theme.UiSmall
            });
        }

        body.Controls.Add(_messageLabel);

        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        var cancel = Theme.StyleButton(new Button { Text = "Cancel", Width = 96 }, primary: true);
        cancel.Click += (_, _) => Close();

        var now = Theme.StyleButton(new Button { Text = PowerActions.NowButton(_action), Width = 140 });
        now.Click += (_, _) => Proceed();

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(now);
        footer.Controls.Add(buttons);

        AcceptButton = cancel;
        CancelButton = cancel;

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(FormChrome.Header(reason, "QuickByte", IconFactory.Exit(40)));

        UpdateMessage();
        ActiveControl = cancel;
    }

    private void Tick()
    {
        _secondsLeft--;
        if (_secondsLeft <= 0)
        {
            Proceed();
            return;
        }
        UpdateMessage();
    }

    private void UpdateMessage() =>
        _messageLabel.Text = $"QuickByte will {PowerActions.Verb(_action)} in {_secondsLeft} second{(_secondsLeft == 1 ? "" : "s")}.";

    private void Proceed()
    {
        _timer.Stop();
        Confirmed = true;
        Close();
    }
}
