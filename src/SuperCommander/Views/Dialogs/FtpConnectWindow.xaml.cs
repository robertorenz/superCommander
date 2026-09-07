using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SuperCommander.Models;
using SuperCommander.Services.Ftp;

namespace SuperCommander.Views.Dialogs;

/// <summary>
/// Site manager and connect dialog. Edits write straight back to the selected
/// site so the list is always the saved state.
/// </summary>
public partial class FtpConnectWindow : ThemedWindow
{
    private readonly ObservableCollection<FtpSite> _sites;
    private FtpSite? _current;
    private bool _loading;

    public FtpConnectWindow(IList<FtpSite> sites)
    {
        InitializeComponent();

        _sites = new ObservableCollection<FtpSite>(sites);
        SiteList.ItemsSource = _sites;

        SecurityBox.ItemsSource = new[]
        {
            "Plain FTP (no encryption)",
            "FTPS - explicit TLS (recommended)",
            "FTPS - implicit TLS (port 990)"
        };

        if (_sites.Count > 0) SiteList.SelectedIndex = 0;
        else SetForm(null);

        UpdateButtons();
    }

    /// <summary>The site to connect to, set when the dialog returns true.</summary>
    public FtpSite? SelectedSite { get; private set; }

    /// <summary>The full list as edited, for the caller to persist.</summary>
    public IReadOnlyList<FtpSite> Sites => _sites;

    // ------------------------------------------------------------------ form

    private void OnSiteSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = SiteList.SelectedItem as FtpSite;

        // Rewriting the form for the row already being edited would reset the
        // caret on every keystroke.
        if (ReferenceEquals(selected, _current))
        {
            UpdateButtons();
            return;
        }

        _current = selected;
        SetForm(_current);
        UpdateButtons();
    }

    private void SetForm(FtpSite? site)
    {
        _loading = true;

        DetailPanel.IsEnabled = site is not null;

        NameBox.Text = site?.Name ?? string.Empty;
        HostBox.Text = site?.Host ?? string.Empty;
        PortBox.Text = (site?.Port ?? 21).ToString(CultureInfo.InvariantCulture);
        UserBox.Text = site?.Username ?? string.Empty;
        PasswordBox.Password = site?.Password ?? string.Empty;
        PathBox.Text = site?.RemotePath ?? "/";
        SecurityBox.SelectedIndex = (int)(site?.Security ?? FtpSecurity.None);
        AcceptCertCheck.IsChecked = site?.AcceptInvalidCertificate ?? false;
        AnonymousCheck.IsChecked = site?.Anonymous ?? false;
        SavePasswordCheck.IsChecked = site?.SavePassword ?? true;

        ApplyAnonymousState();
        StatusText.Text = string.Empty;

        _loading = false;
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _current is null) return;

        _current.Name = NameBox.Text.Trim();
        _current.Host = HostBox.Text.Trim();
        _current.Port = int.TryParse(PortBox.Text, out int port) && port is > 0 and <= 65535 ? port : 21;
        _current.Username = UserBox.Text.Trim();
        _current.RemotePath = FtpPath.Normalise(PathBox.Text);
        _current.AcceptInvalidCertificate = AcceptCertCheck.IsChecked == true;
        _current.SavePassword = SavePasswordCheck.IsChecked == true;
        _current.ApplyPasswordPolicy();

        Refresh();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _current is null) return;
        _current.Password = PasswordBox.Password;
    }

    private void OnSecurityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _current is null) return;

        var security = (FtpSecurity)Math.Max(0, SecurityBox.SelectedIndex);
        var previous = _current.Security;
        _current.Security = security;

        // Nudge the port to the convention when the user has not customised it.
        if (security == FtpSecurity.ImplicitTls && _current.Port == 21)
        {
            _current.Port = 990;
            PortBox.Text = "990";
        }
        else if (previous == FtpSecurity.ImplicitTls && security != FtpSecurity.ImplicitTls && _current.Port == 990)
        {
            _current.Port = 21;
            PortBox.Text = "21";
        }

        Refresh();
    }

    private void OnAnonymousChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _current is null) return;

        _current.Anonymous = AnonymousCheck.IsChecked == true;
        if (_current.Anonymous)
        {
            _current.Username = "anonymous";
            _current.Password = "anonymous@";
            UserBox.Text = "anonymous";
            PasswordBox.Password = "anonymous@";
        }

        ApplyAnonymousState();
        Refresh();
    }

    private void ApplyAnonymousState()
    {
        bool anonymous = AnonymousCheck.IsChecked == true;
        UserBox.IsEnabled = !anonymous;
        PasswordBox.IsEnabled = !anonymous;
        SavePasswordCheck.IsEnabled = !anonymous;
    }

    private void Refresh() => UpdateButtons();

    private void UpdateButtons() =>
        ConnectButton.IsEnabled = _current is not null && !string.IsNullOrWhiteSpace(_current.Host);

    // --------------------------------------------------------------- commands

    private void OnNewSite(object sender, RoutedEventArgs e)
    {
        var site = new FtpSite
        {
            Name = "New site",
            Host = string.Empty,
            Port = 21,
            Username = "anonymous",
            Anonymous = true,
            RemotePath = "/"
        };
        site.Password = "anonymous@";

        _sites.Add(site);
        SiteList.SelectedItem = site;
        HostBox.Focus();
    }

    private void OnDeleteSite(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        if (!MessageDialog.ShowConfirm(this, "Delete site",
                $"Remove \"{_current.Display}\" from the saved sites?", okText: "Delete"))
            return;

        _sites.Remove(_current);
        _current = null;

        if (_sites.Count > 0) SiteList.SelectedIndex = 0;
        else SetForm(null);

        UpdateButtons();
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox && e is MouseButtonEventArgs && _current is null) return;
        if (_current is null) return;

        if (string.IsNullOrWhiteSpace(_current.Host))
        {
            StatusText.Text = "Enter a host name first.";
            return;
        }

        SelectedSite = _current;
        DialogResult = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
