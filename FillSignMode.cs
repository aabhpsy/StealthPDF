using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StealthPDF
{
    // Fill & Sign mode routing: applies the selected sub-mode to the underlying editing tool and
    // toggles the highlight on the top bar. Kept separate from the show/hide file to stay small.
    public partial class MainWindow
    {
        // Switches the underlying editing tool based on the selected Fill & Sign mode. Fill Text
        // preserves an 11pt default on FIRST switch, then respects user picks afterward, so
        // re-toggling doesn't clobber choices the user made in the top bar.
        private void ApplyFillSignMode(FillSignMode mode)
        {
            _fillSignMode = mode;
            RefreshFillSignModeButtons();
            switch (mode)
            {
                case FillSignMode.Signature:
                    SetTool(EditTool.Signature);
                    // ToolSignature_Click would recursively toggle Fill & Sign off; open the popup
                    // directly instead.
                    if (_signaturePopup is null) ShowSignaturePopup();
                    SetStatus(Loc("Str_FS_Status_Signature"));
                    break;
                case FillSignMode.Fill:
                    if (!_fillSignSizedForFill) { _textFontSize = 11; _fillSignSizedForFill = true; }
                    SetTool(EditTool.Text);
                    SetStatus(Loc("Str_FS_Status_Fill"));
                    break;
                case FillSignMode.AddText:
                    SetTool(EditTool.Text);
                    SetStatus(Loc("Str_FS_Status_AddText"));
                    break;
            }
        }

        // Highlights the active mode button in the Fill & Sign bar so users can see which sub-mode
        // is engaged. Called on every mode change; harmless if the bar has been torn down.
        private void RefreshFillSignModeButtons()
        {
            if (_fillSignBar?.Child is not StackPanel row) return;
            foreach (var child in row.Children)
            {
                if (child is Button b && b.Tag is FillSignMode m)
                {
                    bool active = m == _fillSignMode;
                    b.Background = active ? (Brush)FindResource("SelectionBg") : (Brush)FindResource("BgPanel");
                    b.Foreground = active ? (Brush)FindResource("SelectionFg") : (Brush)FindResource("TextPrimary");
                }
            }
        }

        // Tears down the Fill & Sign bar and returns to Select mode. Also closes the signature
        // popup if it's still open, so exiting cleans up every side panel the tool spawned.
        private void ExitFillSign()
        {
            if (_fillSignBar is null) return;
            var host = _fillSignBar.Parent as Panel;
            host?.Children.Remove(_fillSignBar);
            _fillSignBar = null;
            _fillSignSizedForFill = false;
            if (_signaturePopup is not null) HideSignaturePopup();
            if (_currentTool is EditTool.Signature or EditTool.Text) SetTool(EditTool.Select);
            SetStatus("");
        }
    }
}
