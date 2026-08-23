using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using StealthPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace StealthPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Don't intercept keys when typing in an editable TextBox (typewriter tool or form field).
            // The zoom ComboBox is editable-but-read-only; after using it, focus parks on its inner
            // TextBox and would otherwise swallow every shortcut (e.g. Ctrl+F) until the user clicked away.
            if (e.OriginalSource is TextBox tbSrc && !tbSrc.IsReadOnly) return;
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;

            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // An annotation selection copies the annotation(s); otherwise copy page text.
                if (_selectedAnnotation is not null || _selectedSet.Count > 0) CopySelectedAnnotations();
                else CopySelectedText();
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Internal annotation clipboard takes priority over an OS-clipboard image paste.
                if (_annotationClipboard.Count > 0) PasteAnnotations(PageList.SelectedIndex);
                else PasteFromClipboard();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Prefer selecting all annotations (shows where everything is, makes stacked annotations
                // editable); fall back to selecting page text when there are none on screen.
                if (!SelectAllAnnotations()) SelectAllText();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                ApplyCrop([PageList.SelectedIndex]);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                HideCropConfirmBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ShortcutOverlay.Visibility == Visibility.Visible)
            {
                FadeOverlayOut(ShortcutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && AboutOverlay.Visibility == Visibility.Visible)
            {
                FadeOverlayOut(AboutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
            {
                SlideSettingsClosed();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _busyCts is not null)
            {
                // A cancellable long operation (OCR, repair) is running behind the busy overlay - offer to
                // cancel it instead of letting Escape fall through to the app-exit handler below.
                if (StealthDialog.Show(this, $"Cancel the current {_busyOpLabel}?", "StealthPDF",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _busyCts?.Cancel();
                e.Handled = true;
            }
            else if (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                else FadeOverlayIn(ShortcutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                // Toggle the shortcuts overlay (conventional Help key, alongside Ctrl+?).
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                else FadeOverlayIn(ShortcutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                // Toggle the About dialog.
                if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
                else ShowAboutOverlay();
                e.Handled = true;
            }
            else if (e.Key == Key.F12)
            {
                OpenDocumentInfo();
                e.Handled = true;
            }
            else if (e.Key == Key.F5) { SetViewMode(ViewMode.Continuous); e.Handled = true; }
            else if (e.Key == Key.F6) { SetViewMode(ViewMode.Single);     e.Handled = true; }
            else if (e.Key == Key.F7) { SetViewMode(ViewMode.TwoPage);    e.Handled = true; }
            else if (e.Key == Key.F8) { SetViewMode(ViewMode.Grid);       e.Handled = true; }
            else if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
            else if (e.Key == Key.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
            else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Print_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && (_selectedAnnotation is not null || _selectedSet.Count > 0))
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) Undo_Click(this, e);   // ignore key auto-repeat so one press = one undo
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                SaveAs_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveInPlace();
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CloseTab(_active);
                e.Handled = true;
            }
            else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CloseAllTabs();
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CycleTab(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CycleTab(1);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Open_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                OcrPageToClipboard(PageList.SelectedIndex);
                e.Handled = true;
            }
            else if (e.Key == Key.I && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                BeginOcrRegion();
                e.Handled = true;
            }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                NewDocument();
                e.Handled = true;
            }
            else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SidebarToggle_Click(this, e);   // collapse / restore the sidebar
                e.Handled = true;
            }
            // Bare-key tool switches. Only when a document is open, no modifier is held, and no
            // overlay is up (and not while typing - guarded at the top of this handler).
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && ShortcutOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible
                     && SettingsOverlay.Visibility != Visibility.Visible
                     && TryToolShortcut(e.Key))
            {
                e.Handled = true;
            }
            else if ((e.Key == Key.Left || e.Key == Key.Up) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_doc is not null && PageList.SelectedIndex > 0)
                {
                    PageList.SelectedIndex--;
                    e.Handled = true;
                }
            }
            else if ((e.Key == Key.Right || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_doc is not null && PageList.SelectedIndex < _doc.PageCount - 1)
                {
                    PageList.SelectedIndex++;
                    e.Handled = true;
                }
            }
            else if ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep);
                e.Handled = true;
            }
            else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(true); else SetZoom(_zoomLevel - ZoomStep);
                e.Handled = true;
            }
            else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SetZoom(1.0);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // No overlay active - ESC exits the app
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Space && !_spaceHeld)
            {
                _spaceHeld = true;
                PagePreviewPanel.Cursor = Cursors.Hand;
                e.Handled = true;
            }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (e.Key == Key.Space && _spaceHeld)
            {
                _spaceHeld = false;
                if (!_isPanning)
                    PagePreviewPanel.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        // Maps a bare key to an editing tool. Returns false for any other key so the caller's
        // shortcut chain continues. Mirrors the toolbar tool buttons exactly - Signature routes
        // through its button handler so the signature picker opens (a bare SetTool would only arm
        // the tool without showing the menu).
        private bool TryToolShortcut(Key key)
        {
            switch (key)
            {
                // Tools are reachable by their toolbar position (1-8, left to right); the original letter
                // keys stay as fallbacks. Both the number-row and numpad digits map.
                case Key.V: case Key.D1: case Key.NumPad1: SetTool(EditTool.Select); return true;
                case Key.T: case Key.D2: case Key.NumPad2: SetTool(EditTool.Text); return true;
                case Key.E: SetTool(EditTool.Edit); return true;
                case Key.L: case Key.U: case Key.D3: case Key.NumPad3: SetTool(EditTool.Line); return true;
                case Key.H: case Key.D4: case Key.NumPad4: SetTool(EditTool.Highlight); return true;
                case Key.D: case Key.D5: case Key.NumPad5: SetTool(EditTool.Draw); return true;
                case Key.I: case Key.D6: case Key.NumPad6: SetTool(EditTool.Image); return true;
                case Key.G: case Key.D7: case Key.NumPad7: ToolSignature_Click(this, new RoutedEventArgs()); return true;
                case Key.C: case Key.D8: case Key.NumPad8: SetTool(EditTool.Crop); return true;
                case Key.R: case Key.D9: case Key.NumPad9: ToolRotate_Click(this, new RoutedEventArgs()); return true;
                case Key.S: case Key.D0: case Key.NumPad0: ToolStamp_Click(this, new RoutedEventArgs()); return true;
                default: return false;
            }
        }

        // Appends each tool's toolbar position (1-8, left to right) to its tooltip, e.g. "Highlight (4)".
        // Re-resolves the localized base text so a language switch keeps the right wording (from SelectLocale).
        private void ApplyToolNumberTooltips()
        {
            void Set(System.Windows.Controls.Button btn, string key, int n)
            {
                if (btn != null && TryFindResource(key) is string s) btn.ToolTip = $"{s} ({n})";
            }
            Set(ToolSelectBtn, "Str_TT_SelectTool", 1);
            Set(ToolTextBtn, "Str_TT_TextTool", 2);
            Set(ToolEditBtn, "Str_TT_EditTool", 2);
            Set(ToolUnderlineBtn, "Str_TT_LineTool", 3);   // repurposed to the Line tool
            Set(ToolHighlightBtn, "Str_TT_HighlightTool", 4);
            Set(ToolDrawBtn, "Str_TT_DrawTool", 5);
            Set(ToolImageBtn, "Str_TT_ImageTool", 6);
            Set(ToolSignatureBtn, "Str_TT_SignatureTool", 7);
            Set(ToolCropBtn, "Str_TT_CropTool", 8);
            Set(_toolRotateBtn, "Str_TT_RotateTool", 9);
			Set(ToolStampBtn, "Str_TT_StampTool", 0);
        }

        // Opens the online help / how-to page in the user's default browser.
        private void OnlineHelp_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://stealthpdf.com/") { UseShellExecute = true }); }
            catch { }
        }
    }
}
