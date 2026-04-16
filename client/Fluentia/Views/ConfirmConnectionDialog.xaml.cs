using System.Windows;

namespace Fluentia.Views;

public partial class ConfirmConnectionDialog : Window
{
    public bool Approved { get; private set; }

    public ConfirmConnectionDialog(string verifyId, string userAgent)
    {
        InitializeComponent();
        VerifyIdText.Text = verifyId;
        // Truncate and sanitize user agent for display
        UserAgentText.Text = userAgent.Length > 120 ? userAgent[..120] + "…" : userAgent;
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        Approved = true;
        DialogResult = true;
        Close();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        Approved = false;
        DialogResult = false;
        Close();
    }
}
