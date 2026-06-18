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

        var sig = SignatureService.Verify(app.ExecutablePath);
        DetailText.Text = string.IsNullOrEmpty(sig.Detail) ? "\u2014" : sig.Detail;
        SignatureText.Text = sig.Status switch
        {
            SignatureStatus.Valid    => "\u2713 Verified publisher",
            SignatureStatus.Unsigned => "\u26A0 Unsigned",
            SignatureStatus.Invalid  => "\u2717 Invalid signature",
            _                        => "Signature unknown"
        };
        SignatureText.Foreground = new SolidColorBrush(sig.Status switch
        {
            SignatureStatus.Valid    => Color.FromRgb(0x3F, 0xB8, 0x68),
            SignatureStatus.Unsigned => Color.FromRgb(0xE0, 0xA5, 0x3F),
            SignatureStatus.Invalid  => Color.FromRgb(0xE2, 0x5C, 0x5C),
            _                        => Color.FromRgb(0x7A, 0x82, 0x8C)
        });
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
        try
        {
            if (File.Exists(_app.ExecutablePath))
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{_app.ExecutablePath}\"") { UseShellExecute = true });
        }
        catch { }
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
