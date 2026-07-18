using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StealthPDF.Services;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace StealthPDF
{
    // Scanner-to-PDF. Acquires pages from a WIA scanner (ScannerService), builds a PDF via the
    // existing BuildPdfFromImages helper (one page per scanned image, exactly like Import Images),
    // and opens it as a new unsaved tab. Optionally runs OCR afterward to make the scan searchable.
    public partial class MainWindow
    {
        private void Scan_Click(object sender, RoutedEventArgs e) => OpenScanDialog();

        // Presents a small options window (device, DPI, color, duplex, OCR-after) and runs the scan.
        // Kept self-contained so it can be reused from the toolbar button or a future menu entry.
        private void OpenScanDialog()
        {
            if (!ScannerService.HasScanner())
            {
                KillerDialog.Show(this, Loc("Str_Scan_NoneFound"), "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var scanners = ScannerService.ListScanners();
            var dlg = new Window
            {
                Title = Loc("Str_Scan_Title"),
                Width = 360, Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("BgCanvas")
            };

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock { Text = Loc("Str_Scan_Device"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            var deviceBox = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var s in scanners) deviceBox.Items.Add(s.name);
            // Fallback entry: when no WIA scanner enumerated cleanly, let the user defer the pick to
            // Windows native Select-Device dialog (which can show scanners WIA did not list). Also
            // useful to force the native picker even when scanners are listed.
            deviceBox.Items.Add(Loc("Str_Scan_PickAtScanTime"));
            deviceBox.SelectedIndex = 0;   // first real scanner, or the fallback if none enumerated
            panel.Children.Add(deviceBox);

            panel.Children.Add(new TextBlock { Text = Loc("Str_Scan_Dpi"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            var dpiBox = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
            dpiBox.Items.Add("100"); dpiBox.Items.Add("150"); dpiBox.Items.Add("200"); dpiBox.Items.Add("300"); dpiBox.Items.Add("600");
            dpiBox.SelectedIndex = 2;   // 200 dpi default: good for text, modest file size
            panel.Children.Add(dpiBox);

            var colorBox = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
            colorBox.Items.Add(Loc("Str_Scan_Color"));
            colorBox.Items.Add(Loc("Str_Scan_Grayscale"));
            colorBox.Items.Add(Loc("Str_Scan_Text"));
            colorBox.SelectedIndex = 1;
            panel.Children.Add(colorBox);

            var duplexBox = new CheckBox { Content = Loc("Str_Scan_Duplex"), Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(duplexBox);

            var ocrBox = new CheckBox { Content = Loc("Str_Scan_OcrAfter"), Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(ocrBox);

            var scanBtn = new Button { Content = Loc("Str_Scan_Start"), Padding = new Thickness(20, 6, 20, 6), HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(scanBtn);

            dlg.Content = panel;
            scanBtn.Click += (_, _) =>
            {
                // The last dropdown entry is the "pick at scan time" fallback (native WIA dialog);
                // selecting it means "no pre-chosen device" -> Acquire() shows Windows device picker.
                int sel = deviceBox.SelectedIndex;
                bool isFallback = sel < 0 || sel >= scanners.Count;
                string? deviceId = !isFallback ? scanners[sel].id : null;
                int dpi = int.Parse((string)dpiBox.SelectedItem!);
                ScanColor color = (ScanColor)colorBox.SelectedIndex;
                bool duplex = duplexBox.IsChecked == true;
                bool ocrAfter = ocrBox.IsChecked == true;
                dlg.Close();
                DoScan(deviceId, dpi, color, duplex, ocrAfter);
            };
            dlg.ShowDialog();
        }


        // Runs the acquisition and opens the result as a new tab. The scan itself runs on a
        // background thread so the WIA/TWAIN modal dialog can be cancelled cleanly without
        // reentering the WPF message pump on the UI thread (the previous behaviour could hard-crash
        // when the scanner driver was torn down while the UI dispatched inside its modal loop).
        private async void DoScan(string? deviceId, int dpi, ScanColor color, bool duplex, bool ocrAfter)
        {
            SetStatus(Loc("Str_Scan_Waiting"));
            var images = await System.Threading.Tasks.Task.Run(() =>
            {
                try { return ScannerService.Acquire(deviceId, dpi, color, duplex); }
                catch { return new System.Collections.Generic.List<string>(); }
            });
            if (images.Count == 0) { SetStatus(Loc("Str_Scan_Cancelled")); return; }

            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                string tempPath = BuildPdfFromImages(images.ToArray());
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                FinishOpenFile(Loc("Str_Scan_ScannedPdf"), tempPath);
                _originalFile = null;
                MarkDirty(true);
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
                SetStatus(string.Format(Loc("Str_Scan_Done"), images.Count));

                if (ocrAfter && _doc != null)
                    MakeSearchablePdf();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Scan_Failed") + "\n" + ex.Message,
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                foreach (var img in images) try { File.Delete(img); } catch { }
            }
        }
    }
}
