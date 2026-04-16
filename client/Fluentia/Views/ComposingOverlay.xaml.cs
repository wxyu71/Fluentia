using System.Windows;
using System.Windows.Interop;

namespace Fluentia.Views;

public partial class ComposingOverlay : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    public ComposingOverlay()
    {
        InitializeComponent();
    }

    public void ShowComposing(string text)
    {
        ComposingText.Text = text;

        // Position near cursor, slightly above and to the right
        if (GetCursorPos(out var cursor))
        {
            // Convert screen coords to WPF device-independent pixels
            var source = PresentationSource.FromVisual(this);
            double dpi = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            Left = cursor.X * dpi + 12;
            Top = cursor.Y * dpi - 50;

            // Keep inside screen
            var screen = SystemParameters.WorkArea;
            if (Left + ActualWidth > screen.Right)
                Left = screen.Right - ActualWidth - 16;
            if (Top < screen.Top)
                Top = screen.Top + 4;
        }

        if (!IsVisible) Show();
    }

    public void HideComposing()
    {
        Hide();
        ComposingText.Text = "";
    }
}
