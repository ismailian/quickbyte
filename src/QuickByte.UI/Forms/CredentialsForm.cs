using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// Asks for the user name and password a server has just refused a download
/// for. Modal, small, and shown only in answer to an
/// <see cref="AuthenticationRequiredException"/> — never as part of adding a
/// download.
///
/// It used to be three controls on the Add Download window: a "Sign in"
/// checkbox and two fields, present on every download whether or not anything
/// was ever going to ask for a login. Almost nothing does, so what those fields
/// mostly did was make the common case look like it had a decision in it. The
/// login is a *reply to a challenge*, and the moment the challenge arrives is
/// the only moment it can be asked for with the one thing that makes it
/// answerable on screen: which server is asking, and what it said.
///
/// The dialog carries no "remember me". Whether the credentials are kept is not
/// the user's decision to make twice — a download that is paused for three days
/// has to present the same login when it resumes, so
/// <see cref="DownloadItem.Credentials"/> keeps them either way, with the
/// password DPAPI-encrypted rather than written to <c>downloads.json</c> in the
/// clear.
/// </summary>
public sealed class CredentialsForm : Form
{
    private readonly string _message;
    private readonly string _host;

    private TextBox _userNameTextBox = null!;
    private TextBox _passwordTextBox = null!;
    private Label _messageLabel = null!;

    /// <summary>
    /// The login the user typed, or null when they cancelled. Its presence is
    /// the confirmation, exactly as <see cref="NewDownloadForm.Result"/> is —
    /// and an empty user name and password is a cancel by another name, so it
    /// comes back null too rather than sending a blank Basic header.
    /// </summary>
    public DownloadCredentials? Result { get; private set; }

    /// <param name="url">The address being refused; only its host is shown.</param>
    /// <param name="message">
    /// The server's own account of the refusal, from
    /// <see cref="AuthenticationRequiredException"/>. It is the difference
    /// between "this file needs a password" and "that password was wrong", and
    /// the second one is the whole reason a user tries again rather than giving
    /// up on the link.
    /// </param>
    /// <param name="existing">
    /// What was already tried, so a rejected password reopens on a filled-in
    /// user name with only the field that was wrong waiting for input.
    /// </param>
    public CredentialsForm(string url, string message, DownloadCredentials? existing = null)
    {
        _message = message;
        _host = HostOf(url);

        BuildUi();

        if (existing is not null)
        {
            _userNameTextBox.Text = existing.UserName;
            _passwordTextBox.Text = existing.Password;
        }
    }

    private static string HostOf(string url)
    {
        // A URL that will not parse is still worth heading the dialog with:
        // the user pasted it, and seeing it back is what tells them which of
        // several open Add windows is asking.
        try { return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url; }
        catch { return url; }
    }

    private void BuildUi()
    {
        Text = "Sign In";
        Width = 460;
        // Header (74) + footer (60) + the body's own padding (22) + three rows
        // of 44/38/38, and then the caption bar and border on top of all of it.
        // Come up short and the password field is the row that goes, because it
        // is the last one before the filler — which is not a subtle bug to ship
        // on a window whose only purpose is collecting a password.
        Height = 336;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(FormChrome.Header("Sign In Required", _host, IconFactory.SignIn(40)));
    }

    private Panel BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(24, 14, 24, 8) };

        // Rows declared up front, with a percent filler last: without it the
        // password row absorbs the leftover height and its label floats out of
        // line with its field.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Theme.Surface
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); // message
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); // user name
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); // password
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // filler

        int row = 0;

        _messageLabel = new Label
        {
            Text = _message,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Warning,
            Font = Theme.UiSmall,
            Margin = new Padding(0, 0, 0, 6)
        };
        layout.Controls.Add(_messageLabel, 0, row);
        layout.SetColumnSpan(_messageLabel, 2);
        row++;

        layout.Controls.Add(FormChrome.FieldLabel("User name"), 0, row);
        _userNameTextBox = FormChrome.Field();
        layout.Controls.Add(_userNameTextBox, 1, row++);

        layout.Controls.Add(FormChrome.FieldLabel("Password"), 0, row);
        _passwordTextBox = FormChrome.Field();
        _passwordTextBox.UseSystemPasswordChar = true;
        layout.Controls.Add(_passwordTextBox, 1, row);

        body.Controls.Add(layout);
        return body;
    }

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        var okButton = Theme.StyleButton(new Button { Text = "Sign In", Width = 96 }, primary: true);
        okButton.Click += (_, _) => OnOkClicked();

        var cancelButton = Theme.StyleButton(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel });

        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        return footer;
    }

    /// <summary>
    /// Focus goes to the field that is actually missing something — the user
    /// name on a first challenge, the password when the user name survived a
    /// rejection. Done on Shown rather than in the constructor: a control cannot
    /// take focus before the form has a handle, and the call is silently ignored
    /// if it tries.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        var target = _userNameTextBox.TextLength > 0 ? _passwordTextBox : _userNameTextBox;
        target.Focus();
        target.SelectAll();
    }

    private void OnOkClicked()
    {
        var credentials = new DownloadCredentials
        {
            UserName = _userNameTextBox.Text.Trim(),
            Password = _passwordTextBox.Text
        };

        if (credentials.IsEmpty)
        {
            // Nothing typed. Saying so beats closing on an empty login and
            // letting the fetch come back with the same 401 a moment later.
            _messageLabel.Text = "Enter the user name and password the server is asking for.";
            _messageLabel.ForeColor = Theme.Danger;
            _userNameTextBox.Focus();
            return;
        }

        Result = credentials;
        DialogResult = DialogResult.OK;
        Close();
    }
}
