using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using GunWall.Models;
using GunWall.Services;

namespace GunWall;

/// <summary>Read-out of one application's identity, trust and policy, with the
/// common per-app actions. Returns DialogResult = true if a rule changed, so the
/// caller can refresh.</summary>
public partial class AppPropertiesWindow : Window
{
    private readonly AppInfo _app;
    private readonly FirewallManager _firewall;

    public AppPropertiesWindow(AppInfo app, FirewallManager firewall)
    {
        InitializeComponent();
        _app = app;
        _firewall = firewall;

        IconImage.Source = app.Icon;
        NameText.Text = app.Name;
        PathText.Text = string.IsNullOrEmpty(app.ExecutablePath) ? "\u2014" : app.ExecutablePath;
        HashText.Text = string.IsNullOrWhiteSpace(app.Hash) ? "\u2014" : app.Hash;
        ConnText.Text = app.ActiveConnections.ToString();
        StatusText.Text = app.Status.ToString();
        PublisherText.Text = string.IsNullOrWhiteSpace(app.Publisher) ? "\u2014" : app.Publisher;
        NoteBox.Text = firewall.GetNote(app.ExecutablePath);
        BypassBlocklistsCheck.IsChecked = firewall.BypassesBlocklists(app.ExecutablePath);

        var sig = SignatureService.Verify(app.ExecutablePath);
        DetailText.Text = string.IsNullOrEmpty(sig.Detail) ? "\u2014" : sig.Detail;
        SignatureText.Text = sig.Status switch
        {
            SignatureStatus.Valid    => "\u2713 Verified publisher",
            SignatureStatus.Unsigned => "\u26A0 Unsigned",
            SignatureStatus.Invalid  => "\u2717 Invalid signature",
            _                        => "Signature unknown"
        };
        // The identical four literals were fixed in AlertWindow in 0.99.44 and
        // this copy was not looked for. Same verdict, same tokens.
        SignatureText.Foreground = (Brush)System.Windows.Application.Current.FindResource(sig.Status switch
        {
            SignatureStatus.Valid    => "AllowText",
            SignatureStatus.Unsigned => "WarnText",
            SignatureStatus.Invalid  => "BlockText",
            _                        => "TextTertiary"
        });

        // Microsoft Store / UWP identity
        if (_app.IsStoreApp)
        {
            TypeText.Text = string.IsNullOrWhiteSpace(_app.StoreName)
                ? "Microsoft Store app"
                : $"Microsoft Store app \u2014 {_app.StoreName}";
            PackageText.Text = string.IsNullOrWhiteSpace(_app.PackageFamily)
                ? "\u2014" : _app.PackageFamily;
        }
        else
        {
            TypeText.Text = "Desktop application";
            PackageLabel.Visibility = Visibility.Collapsed;
            PackageText.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveNote()
    {
        try { _firewall.SetNote(_app.ExecutablePath, NoteBox.Text); } catch { }
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        SaveNote();
        try { _firewall.AllowApp(_app.ExecutablePath, _app.Name); } catch { }
        DialogResult = true;
        Close();
    }

    private void Block_Click(object sender, RoutedEventArgs e)
    {
        SaveNote();
        try { _firewall.BlockApp(_app.ExecutablePath, _app.Name); } catch { }
        DialogResult = true;
        Close();
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!Services.ShellHelper.RevealInExplorer(_app.ExecutablePath))
            MessageBox.Show($"Could not open the location in Explorer.\nThe file is at:\n{_app.ExecutablePath}",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Applies immediately rather than on Close.
    ///
    /// Turning the exemption on removes the domain filters already installed for
    /// this application; deferring that would leave it blocked until the dialog
    /// was dismissed, and a setting that appears not to work is worse than one
    /// that is absent.</summary>
    private void BypassBlocklists_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool on = BypassBlocklistsCheck.IsChecked == true;
            int removed = _firewall.SetBypassBlocklists(_app.ExecutablePath, on);
            if (on && removed > 0)
                MessageBox.Show(
                    $"Blocklists no longer apply to {_app.Name} at the kernel layer, and "
                    + $"{removed} filter(s) already blocking it were removed.\n\n"
                    + "Blocked domains are still refused by GunWall's DNS resolver, which "
                    + "cannot tell which application asked.",
                    "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            GunWall.Services.DiagnosticLog.LogException("BypassBlocklists", ex);
            // Put the control back where the stored state actually is, rather than
            // leaving it showing a change that did not happen.
            BypassBlocklistsCheck.IsChecked = _firewall.BypassesBlocklists(_app.ExecutablePath);
            MessageBox.Show($"Could not change that setting: {ex.Message}", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_app.ExecutablePath ?? ""); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveNote();
        Close();
    }
}
