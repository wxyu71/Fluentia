using System.Windows;
using Fluentia.Services;

namespace Fluentia.Views;

public partial class ConfirmConnectionDialog : Window
{
    public bool Approved { get; private set; }

    public ConfirmConnectionDialog(string verifyId, string userAgent)
    {
        InitializeComponent();
        Title = LocalizationService.Get("ConfirmDialogTitle");
        HeaderTitleText.Text = LocalizationService.Get("ConfirmDialogHeaderTitle");
        HeaderBodyText.Text = LocalizationService.Get("ConfirmDialogHeaderBody");
        VerificationTitleText.Text = LocalizationService.Get("ConfirmDialogVerificationTitle");
        VerificationHintText.Text = LocalizationService.Get("ConfirmDialogVerificationHint");
        DeviceLabelText.Text = LocalizationService.Get("ConfirmDialogDeviceLabel");
        RejectBtn.Content = LocalizationService.Get("ButtonReject");
        ApproveBtn.Content = LocalizationService.Get("ButtonApprove");
        VerifyIdText.Text = verifyId;
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
