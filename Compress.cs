using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using StealthPDF.Services;
using Microsoft.Win32;

namespace StealthPDF
{
    // Compress PDF: shrinks the file by replacing embedded raster images with downsampled JPEGs
    // via an out-of-process PyMuPDF helper. This is IMAGE-LEVEL compression (not page-rasterize):
    //   * rotation, page size, text and vector art are never touched -> orientation & selectable
    //     text are preserved exactly,
    //   * each image is judged at its REAL on-page DPI (from its painted transform) and only
    //     downsampled if above target (never upsamples, never grows an image's stream),
    //   * dedup + garbage-collect + deflate for the smallest valid file (linearized when small).
    // The helper is a bundled portable Python + PyMuPDF, self-extracted on first use (see
    // PdfCompressHelper). It runs hidden; the UI shows a cancellable busy overlay.
    public partial class MainWindow
    {
        private void Compress_Click(object sender, RoutedEventArgs e) => OpenCompressDialog();

        // A small options window: JPEG quality slider, target DPI, grayscale toggle.
        private void OpenCompressDialog()
        {
            if (_doc is null || _currentFile is null)
            {
                StealthDialog.Show(this, Loc("Str_Msg_OpenFirst"), "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Window
            {
                Title = Loc("Str_Compress_Title"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("BgCanvas")
            };

            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            panel.Children.Add(MakeLabel(Loc("Str_Compress_Quality")));
            var qualityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var qualitySlider = new Slider { Minimum = 10, Maximum = 100, Value = 70, Width = 240, TickFrequency = 10, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight };
            var qualityLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            qualitySlider.ValueChanged += (_, _) => qualityLabel.Text = $"{(int)qualitySlider.Value}%";
            qualityLabel.Text = "70%";
            qualityRow.Children.Add(qualitySlider);
            qualityRow.Children.Add(qualityLabel);
            panel.Children.Add(qualityRow);

            panel.Children.Add(MakeLabel(Loc("Str_Compress_TargetDpi")));
            var dpiRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var dpiSlider = new Slider { Minimum = 72, Maximum = 300, Value = 150, Width = 240, TickFrequency = 18, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight };
            var dpiLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            dpiSlider.ValueChanged += (_, _) => dpiLabel.Text = $"{(int)dpiSlider.Value} DPI";
            dpiLabel.Text = "150 DPI";
            dpiRow.Children.Add(dpiSlider);
            dpiRow.Children.Add(dpiLabel);
            panel.Children.Add(dpiRow);

            var grayscaleBox = new CheckBox { Content = Loc("Str_Compress_Grayscale"), Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(grayscaleBox);

            var note = new TextBlock
            {
                Text = Loc("Str_Compress_Note"),
                FontSize = 11,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(note);

            var goBtn = new Button { Content = Loc("Str_Compress_Go"), Padding = new Thickness(20, 6, 20, 6), HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(goBtn);

            dlg.Content = panel;
            goBtn.Click += (_, _) =>
            {
                int quality = (int)qualitySlider.Value;
                int targetDpi = (int)dpiSlider.Value;
                bool grayscale = grayscaleBox.IsChecked == true;
                dlg.Close();
                CompressAndSave(quality, targetDpi, grayscale);
            };
            dlg.ShowDialog();
        }

        private static TextBlock MakeLabel(string text) =>
            new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };

        // Compresses the current document off-thread, then writes the result to a user-chosen path.
        // Large-file notes (the old flow looked frozen on big PDFs):
        //   * A CLEAN, un-annotated document whose backing file is the user's own file is compressed
        //     straight from disk - no UI-thread PdfSharp snapshot save at all (that save was the
        //     first freeze on large files). Dirty / annotated / temp-backed documents still snapshot,
        //     but the busy overlay is already on screen (with a render yield) so the window never
        //     looks dead.
        //   * The helper streams per-page progress, pumped into the overlay here.
        private async void CompressAndSave(int quality, int targetDpi, bool grayscale)
        {
            if (_doc is null) return;

            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = Loc("Str_Compress_Title"),
                                           CheckFileExists = false, CheckPathExists = true,
                                           FileName = Path.GetFileNameWithoutExtension(_currentFile ?? "document") + "-compressed.pdf" };
            if (dlg.ShowDialog(this) != true) return;
            string outputPath = dlg.FileName;

            // Writing over the open document's own backing file can't work (PdfSharp holds it open,
            // and in the fast path the helper would be reading and writing the same path). Say so
            // up front instead of failing with a helper error later.
            if (!string.IsNullOrEmpty(_currentFile) &&
                string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(_currentFile),
                              StringComparison.OrdinalIgnoreCase))
            {
                StealthDialog.Show(this,
                    "Pick a different file name - the compressed copy can't replace the document that is currently open.",
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int pageCount = _doc.PageCount;
            var overlay = ShowFlattenProgress(pageCount, verb: Loc("Str_Compress_Working"));

            string? snapshotPath = null;   // temp file we own and delete (null = compressing the on-disk file)
            try
            {
                var ct = BeginCancellableOp("compress");

                string compressSrc;
                // Fast path is only used when the on-disk file PROVABLY matches what is on screen:
                // unmodified, no annotation overlays (they live outside the PDF until a save burns
                // them in), and _currentFile == _originalFile (they diverge exactly in the risky
                // cases: decrypted/repaired opens and every Save As). Anything else snapshots.
                if (!_isDirty
                    && !_annotations.Values.Any(list => list.Count > 0)
                    && !string.IsNullOrEmpty(_currentFile)
                    && string.Equals(_currentFile, _originalFile, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(_currentFile)
                    && CanOpenSharedRead(_currentFile!))
                {
                    compressSrc = _currentFile!;
                }
                else
                {
                    // Snapshot through PdfSharp. Kept on the UI thread like the OCR/flatten flows
                    // (PdfSharp isn't thread-safe); the yield lets the overlay paint first.
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                    snapshotPath = App.MakeTempFile("cmpsrc");
                    _doc.Save(snapshotPath);
                    compressSrc = snapshotPath;
                }
                long beforeBytes = new FileInfo(compressSrc).Length;

                long afterBytes = 0;
                await Task.Run(() =>
                {
                    PdfCompressHelper.RunCompress(compressSrc, outputPath, targetDpi, quality, grayscale, ct,
                        (cur, tot) => Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(overlay, cur, tot))));
                    afterBytes = new FileInfo(outputPath).Length;
                });

                if (ct.IsCancellationRequested) { SetStatus(Loc("Str_Compress_Cancelled")); return; }
                SetStatus(string.Format(Loc("Str_Compress_Done"),
                    FormatSize(beforeBytes), FormatSize(afterBytes),
                    (100.0 * (beforeBytes - afterBytes) / Math.Max(1, beforeBytes)).ToString("0.#")));
            }
            catch (Exception ex)
            {
                try { StealthDialog.Show(this, Loc("Str_Compress_Failed") + "\n" + ex.Message, "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }
            finally
            {
                try { HideFlattenProgress(overlay); } catch { }
                EndCancellableOp();
                try { if (snapshotPath != null) File.Delete(snapshotPath); } catch { }
            }
        }

        // The helper runs in its own process: verify PdfSharp's hold on the file still lets
        // another reader in before handing the path over (falls back to a snapshot if not).
        private static bool CanOpenSharedRead(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } return true; }
            catch { return false; }
        }
    }
}