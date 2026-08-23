using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using StealthPDF.Services;
using System.Windows.Media.Imaging;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace StealthPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // OCR (Tesseract) - extract text from a rendered page
        // ============================================================

        // Longest-side pixel budget for the OCR render. ~300 DPI on a Letter page, which is the sweet
        // spot for Tesseract: high enough for small body text, not so high it wastes time/memory.
        private const int OcrRenderMax = 2600;

        // Non-null only while a cancellable long-running operation (OCR, repair) is in flight. Esc (see
        // KeyboardShortcuts) offers to cancel it instead of closing the app; loops check the token so a long
        // run stops promptly. _busyOpLabel names the op in the cancel prompt.
        private CancellationTokenSource? _busyCts;
        private string _busyOpLabel = "operation";

        // Registers a cancellable long-running operation and returns its token to thread through the work.
        // Disposing any prior source first keeps the strip->repair handoff (fire-and-forget) clean.
        private CancellationToken BeginCancellableOp(string label)
        {
            _busyCts?.Dispose();
            _busyCts = new CancellationTokenSource();
            _busyOpLabel = label;
            return _busyCts.Token;
        }

        private void EndCancellableOp()
        {
            _busyCts?.Dispose();
            _busyCts = null;
        }

        // ============================================================
        // OCR languages (multi-select, on-demand download)
        // ============================================================

        // Tesseract code -> display name. English and Nepali are BUNDLED into the EXE (High Quality
        // models) so they work fully offline; the rest download on demand into
        // OcrNativeBootstrap.TessDataDir on first use.
        private static readonly (string Code, string Name)[] OcrLanguageCatalog =
        [
            ("eng", "English"),
            ("nep", "Nepali (नेपाली)"),
            ("spa", "Spanish"),
            ("fra", "French"),
            ("deu", "German"),
            ("tur", "Turkish"),
            ("ben", "Bengali"),
            ("chi_sim", "Chinese (Simplified)"),
            ("chi_tra", "Chinese (Traditional)"),
        ];

        // Languages whose traineddata ship embedded in the EXE (see csproj OcrTessData embeds +
        // OcrNativeBootstrap.EnsureLanguageData). They are always present, so never offered as a
        // download and marked HQ on extraction.
        private static readonly HashSet<string> BundledOcrLanguages =
            new(StringComparer.Ordinal) { "eng", "nep" };

        // True if <code>.traineddata exists in the tessdata folder. Bundled languages are always
        // considered installed (they self-extract on first OCR); others are a file-presence check.
        private static bool IsLanguageInstalled(string code) =>
            BundledOcrLanguages.Contains(code) ||
            File.Exists(Path.Combine(OcrNativeBootstrap.TessDataDir, code + ".traineddata"));
        // The user's chosen OCR languages, persisted as a '+'-joined setting. Filtered to those actually
        // installed (a deleted pack can't be passed to Tesseract) and never empty - English is the floor.
        private List<string> GetSelectedOcrLanguages()
        {
            var stored = (App.GetSetting("OcrLanguages") ?? "eng")
                .Split(['+'], StringSplitOptions.RemoveEmptyEntries);
            var sel = new List<string>();
            foreach (var c in stored)
                if (IsLanguageInstalled(c) && !sel.Contains(c)) sel.Add(c);
            if (sel.Count == 0) sel.Add("eng");
            return sel;
        }

        private void SetSelectedOcrLanguages(List<string> langs) =>
            App.SetSetting("OcrLanguages", string.Join("+", langs));

        // The language string handed to Tesseract, e.g. "eng" or "eng+spa".
        private string CurrentOcrLanguageString() => string.Join("+", GetSelectedOcrLanguages());

        // High-quality (tessdata_best) vs standard model preference, persisted. When on, downloads pull the
        // larger, more accurate "best" models and new languages keep using them.
        private bool OcrHighQuality => App.GetSetting("OcrHighQuality") == "1";
        private void SetOcrHighQuality(bool on) => App.SetSetting("OcrHighQuality", on ? "1" : "0");

        // Download URL for a language's traineddata, honoring the HQ preference.
        // Standard tier uses tessdata_fast: the same integer LSTM model as the full "tessdata" repo but without
        // the unused legacy-engine data, so it is ~4MB instead of ~22MB with identical LSTM accuracy. HQ uses
        // tessdata_best (float LSTM): larger (~14MB) but the most accurate.
        private string LanguageDataUrl(string code) => OcrHighQuality
            ? $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/{code}.traineddata"
            : $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/{code}.traineddata";

        private static string NameForCode(string code)
        {
            foreach (var (c, n) in OcrLanguageCatalog) if (c == code) return n;
            return code;
        }

        // Tracks which installed languages currently hold the high-quality (best) model, so toggling HQ off
        // then on again doesn't re-download ones that are already HQ.
        private static HashSet<string> GetHqLanguages()
        {
            var set = new HashSet<string>();
            foreach (var c in (App.GetSetting("OcrHqLanguages") ?? "").Split(['+'], StringSplitOptions.RemoveEmptyEntries))
                set.Add(c);
            return set;
        }

        private static void MarkLanguageHq(string code, bool isHq)
        {
            var set = GetHqLanguages();
            if (isHq) set.Add(code); else set.Remove(code);
            App.SetSetting("OcrHqLanguages", string.Join("+", set));
        }

        // Builds the multi-select Language submenu. Installed languages are checkable and stay toggled in the
        // open menu; not-yet-installed ones offer a one-time download. At least one language stays selected.
        private MenuItem BuildLanguageMenu()
        {
            string tessDir = OcrNativeBootstrap.EnsureLanguageData();   // self-extract bundled English + Nepali
            // Bundled langs ship High Quality, so record them once as HQ so the HQ toggle never
            // re-downloads them (and turning HQ off->on is a no-op for these).
            foreach (var b in BundledOcrLanguages) MarkLanguageHq(b, true);
            var selected = GetSelectedOcrLanguages();
            bool hqPref = OcrHighQuality;

            var root = new MenuItem { Header = Loc("Str_Ocr_Language") };

            // Header with the Tesseract language code right-aligned, mirroring the Settings language list.
            FrameworkElement LangHeader(string name, string code, string? suffix = null)
            {
                var dp = new DockPanel { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 170 };
                var codeTb = new TextBlock
                {
                    Text = code, FontFamily = UiKit.MonoFont, FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(codeTb, Dock.Right);
                dp.Children.Add(codeTb);
                dp.Children.Add(new TextBlock { Text = suffix is null ? name : $"{name}  {suffix}", VerticalAlignment = VerticalAlignment.Center });
                return dp;
            }

            foreach (var (code, name) in OcrLanguageCatalog)
            {
                bool installed = File.Exists(Path.Combine(tessDir, code + ".traineddata"));
                if (installed)
                {
                    var item = new MenuItem
                    {
                        Header = LangHeader(name, code),
                        IsCheckable = true,
                        IsChecked = selected.Contains(code),
                        StaysOpenOnClick = true,
                    };
                    item.Click += (s, _) =>
                    {
                        var mi = (MenuItem)s!;
                        var sel = GetSelectedOcrLanguages();
                        if (mi.IsChecked) { if (!sel.Contains(code)) sel.Add(code); }
                        else
                        {
                            if (sel.Count <= 1) { mi.IsChecked = true; return; }   // keep at least one selected
                            sel.Remove(code);
                        }
                        SetSelectedOcrLanguages(sel);
                        SetStatus($"OCR language: {string.Join("+", sel)}");
                    };
                    root.Items.Add(item);
                }
                else
                {
                    var item = new MenuItem { Header = LangHeader(name, code, hqPref ? "(download HQ)" : "(download)") };
                    item.Click += (_, _) => DownloadOcrLanguage(code, name);
                    root.Items.Add(item);
                }
            }

            // High-quality toggle. Enabling it upgrades the languages already selected and makes future
            // downloads pull the "best" models too.
            root.Items.Add(new Separator());
            var hq = new MenuItem
            {
                Header = "Use High Quality Models",
                IsChecked = hqPref,
            };
            // Closes the menu on click (no StaysOpenOnClick) so it reopens with a refreshed checkmark and
            // (download HQ) labels. Flips the persisted preference directly so the setting can't drift from
            // the visual state.
            hq.Click += (_, _) =>
            {
                bool now = !OcrHighQuality;
                SetOcrHighQuality(now);
                if (now) RedownloadSelectedHighQuality();
            };
            root.Items.Add(hq);
            return root;
        }

        // Downloads a single language's traineddata (standard or HQ, per the toggle) and selects it.
        private async void DownloadOcrLanguage(string code, string name)
        {
            var ct = BeginCancellableOp("language download");
            var busy = ShowBusyOverlay($"Downloading {name} language data...");
            string tessDir = OcrNativeBootstrap.EnsureLanguageData();
            string dest = Path.Combine(tessDir, code + ".traineddata");
            try
            {
                using var http = MakeDownloadClient();
                await DownloadTrainedDataAsync(http, LanguageDataUrl(code), dest, $"Downloading {name}...", busy, ct);
                MarkLanguageHq(code, OcrHighQuality);

                var sel = GetSelectedOcrLanguages();
                if (!sel.Contains(code)) { sel.Add(code); SetSelectedOcrLanguages(sel); }
                HideBusyOverlay(busy);
                SetStatus($"{name} installed - OCR language: {string.Join("+", GetSelectedOcrLanguages())}");
            }
            catch (OperationCanceledException)
            {
                HideBusyOverlay(busy);
                TryDeleteFile(dest + ".part");
                if (ct.IsCancellationRequested) SetStatus($"{name} download cancelled");
                else StealthDialog.Show(this, $"Downloading {name} timed out. Check your connection and try again.",
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                TryDeleteFile(dest + ".part");
                StealthDialog.Show(this, $"Could not download {name} language data:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Re-downloads every currently-selected language in high quality (tessdata_best), replacing the
        // standard copies. Triggered when the user enables "Use High Quality Models". Cancellable; a single
        // language's failure is reported but doesn't abort the rest, and a failed file never replaces a
        // working one (temp+move).
        private async void RedownloadSelectedHighQuality()
        {
            // Only UPGRADE languages that are actually installed and not already HQ. A language the user has
            // selected but hasn't downloaded yet (e.g. the default English right after clearing data) must NOT
            // be auto-downloaded here - that would surprise the user with no prompt. It is fetched on the first
            // OCR instead, via EnsureOcrModelsReadyAsync, which shows the heads-up dialog and honors this HQ pref.
            var hq = GetHqLanguages();
            var toDownload = new List<string>();
            foreach (var c in GetSelectedOcrLanguages())
                if (IsLanguageInstalled(c) && !hq.Contains(c)) toDownload.Add(c);

            if (toDownload.Count == 0)
            {
                bool anyInstalled = false;
                foreach (var c in GetSelectedOcrLanguages()) if (IsLanguageInstalled(c)) { anyInstalled = true; break; }
                SetStatus(anyInstalled
                    ? "All selected languages are already high quality"
                    : "High quality models will be used the next time you run OCR");
                return;
            }

            var ct = BeginCancellableOp("language download");
            var busy = ShowBusyOverlay("Downloading high quality language models...");
            string tessDir = OcrNativeBootstrap.EnsureLanguageData();
            var failed = new List<string>();
            try
            {
                using var http = MakeDownloadClient();
                int i = 0;
                foreach (var code in toDownload)
                {
                    if (ct.IsCancellationRequested) break;
                    i++;
                    string name = NameForCode(code);
                    string dest = Path.Combine(tessDir, code + ".traineddata");
                    string url = $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/{code}.traineddata";
                    try
                    {
                        await DownloadTrainedDataAsync(http, url, dest, $"Downloading {name} (HQ) - {i} of {toDownload.Count} -", busy, ct);
                        MarkLanguageHq(code, true);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch { failed.Add(name); TryDeleteFile(dest + ".part"); }
                }
                HideBusyOverlay(busy);
                if (ct.IsCancellationRequested) SetStatus("High quality download cancelled");
                else if (failed.Count > 0) SetStatus($"High quality models installed; failed: {string.Join(", ", failed)}");
                else SetStatus($"High quality models installed for: {string.Join("+", toDownload)}");
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                StealthDialog.Show(this, $"High quality download failed:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        private static System.Net.Http.HttpClient MakeDownloadClient()
        {
            // Timeout covers connect + headers; the body is bounded by the cancellation token instead.
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("StealthPDF-OCR");
            return http;
        }

        // Streams one traineddata file to destFile, showing MB progress and honoring the cancel token; writes
        // via a .part file and atomically moves into place only on full success. Throws on cancel/error.
        private async Task DownloadTrainedDataAsync(System.Net.Http.HttpClient http, string url, string destFile,
            string label, Border busy, CancellationToken ct)
        {
            string part = destFile + ".part";
            using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                // using-var (not a block): these dispose at the end of the resp block, before the File.Move below.
                using var netStream = await resp.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await netStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, n, ct);
                    read += n;
                    double mb = read / 1048576.0;
                    SetBusyMessage(busy, total.HasValue
                        ? $"{label} {mb:F1} / {total.Value / 1048576.0:F1} MB  (Esc to cancel)"
                        : $"{label} {mb:F1} MB  (Esc to cancel)");
                }
            }
            if (File.Exists(destFile)) File.Delete(destFile);
            File.Move(part, destFile);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }

        // Ensures the language models OCR is about to use are present on disk. Nothing is bundled, so on the
        // first OCR (or after the user adds a new language) the model is downloaded here, behind a heads-up
        // dialog. Returns true only when every required model is installed and OCR may proceed.
        private async Task<bool> EnsureOcrModelsReadyAsync()
        {
            // Desired languages from the persisted setting (default English), regardless of install state.
            var desired = new List<string>(
                (App.GetSetting("OcrLanguages") ?? "eng").Split(['+'], StringSplitOptions.RemoveEmptyEntries));
            if (desired.Count == 0) desired.Add("eng");

            var missing = new List<string>();
            foreach (var c in desired) if (!IsLanguageInstalled(c) && !missing.Contains(c)) missing.Add(c);
            if (missing.Count == 0) return true;

            string names = string.Join(", ", missing.ConvertAll(NameForCode));
            var choice = StealthDialog.Show(this,
                $"A language model ({names}) will be downloaded now so OCR can run.\n\n" +
                "You can add more languages or switch to higher quality models any time from the OCR menu.",
                "StealthPDF", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (choice != MessageBoxResult.OK) return false;

            var ct = BeginCancellableOp("language download");
            var busy = ShowBusyOverlay("Downloading language model...");
            try
            {
                string tessDir = OcrNativeBootstrap.EnsureLanguageData();
                using var http = MakeDownloadClient();
                for (int i = 0; i < missing.Count; i++)
                {
                    string code = missing[i];
                    string name = NameForCode(code);
                    string dest = Path.Combine(tessDir, code + ".traineddata");
                    await DownloadTrainedDataAsync(http, LanguageDataUrl(code), dest,
                        missing.Count == 1 ? $"Downloading {name}..." : $"Downloading {name} - {i + 1} of {missing.Count} -",
                        busy, ct);
                    MarkLanguageHq(code, OcrHighQuality);
                    if (ct.IsCancellationRequested) return false;
                }
                foreach (var c in missing) if (!IsLanguageInstalled(c)) return false;
                return true;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Language download cancelled");
                return false;
            }
            catch (Exception ex)
            {
                StealthDialog.Show(this, $"Could not download the language model:\n{ex.Message}",
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                HideBusyOverlay(busy);
                EndCancellableOp();
            }
        }

        // Right-click "OCR Page" action: rasterize the page, recognize text off the UI thread, and drop
        // the result on the clipboard. Render + OCR are both slow, so they run inside Task.Run behind the
        // busy overlay; everything touching the clipboard/UI happens back on the UI thread.
        private async void OcrPageToClipboard(int pageIdx)
        {
            if (_doc is null || _currentFile is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return;
            if (!await EnsureOcrModelsReadyAsync()) return;

            // Capture everything off the live UI state before going async.
            string file = _currentFile;
            int rot = _pageRotations.TryGetValue(pageIdx, out var r) ? r : 0;
            string lang = CurrentOcrLanguageString();

            var ct = BeginCancellableOp("OCR operation");
            var busy = ShowBusyOverlay("Running OCR...");
            try
            {
                OcrResult result = await Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(file, new PageDimensions(OcrRenderMax, OcrRenderMax));
                    using var pageReader = docReader.GetPageReader(pageIdx);

                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    byte[] bgra = pageReader.GetImage();

                    // Temp file has /Rotate stripped, so rotate the pixel buffer to the page's visual orientation.
                    if (rot != 0) (bgra, w, h) = RotateBitmap(bgra, w, h, rot);

                    using var ocr = new OcrService(language: lang);   // engine is not thread-safe: one per operation
                    return ocr.RecognizeBgra(bgra, w, h);
                });

                HideBusyOverlay(busy);
                // Cooperative cancel: a single page can't be interrupted mid-recognition, so we just discard
                // the result if the user cancelled. No exceptions are thrown for cancellation anywhere.
                if (ct.IsCancellationRequested) { SetStatus("OCR cancelled"); return; }

                string text = result.Text.Trim();
                if (text.Length == 0)
                {
                    SetStatus($"OCR: no text found on page {pageIdx + 1}");
                    return;
                }

                Clipboard.SetText(text);
                SetStatus($"OCR: copied {text.Length} chars from page {pageIdx + 1} ({result.MeanConfidence:P0} confidence)");
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                StealthDialog.Show(this, $"OCR failed:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // OCR Region: armed by the menu item; the next box-drag (Select tool) crops that area of the page
        // bitmap and OCRs only it to the clipboard. Works on scans that have no text layer to extract from.
        private bool _ocrRegionMode;

        private void BeginOcrRegion()
        {
            if (_doc is null || _currentFile is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            SetTool(EditTool.Select);
            _ocrRegionMode = true;
            SetStatus("Drag a box over the area to recognize");
        }

        private async void OcrRegion(int pageIdx, Rect canvasBounds)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return;
            if (!_renderDims.TryGetValue(pageIdx, out var rd) || rd.w <= 0 || rd.h <= 0) return;
            if (canvasBounds.Width < 4 || canvasBounds.Height < 4) { SetStatus("OCR region too small"); return; }
            if (!await EnsureOcrModelsReadyAsync()) return;

            string file = _currentFile;
            int rot = _pageRotations.TryGetValue(pageIdx, out var r) ? r : 0;
            string lang = CurrentOcrLanguageString();
            int renderW = rd.w, renderH = rd.h;
            Rect cb = canvasBounds;

            var ct = BeginCancellableOp("OCR region");
            var busy = ShowBusyOverlay("Recognizing region...");
            try
            {
                OcrResult result = await Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(file, new PageDimensions(OcrRenderMax, OcrRenderMax));
                    using var pageReader = docReader.GetPageReader(pageIdx);
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    byte[] bgra = pageReader.GetImage();
                    if (rot != 0) (bgra, w, h) = RotateBitmap(bgra, w, h, rot);

                    double sx = (double)w / renderW, sy = (double)h / renderH;
                    byte[] crop = CropBgra(bgra, w, h,
                        (int)Math.Round(cb.Left * sx), (int)Math.Round(cb.Top * sy),
                        (int)Math.Round(cb.Width * sx), (int)Math.Round(cb.Height * sy),
                        out int cw, out int chh);

                    using var ocr = new OcrService(language: lang);
                    return ocr.RecognizeBgra(crop, cw, chh);
                });

                HideBusyOverlay(busy);
                if (ct.IsCancellationRequested) { SetStatus("OCR cancelled"); return; }

                string text = result.Text.Trim();
                if (text.Length == 0) { SetStatus("OCR: no text found in the selected region"); return; }
                Clipboard.SetText(text);
                SetStatus($"OCR: copied {text.Length} chars from the region ({result.MeanConfidence:P0} confidence)");
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                StealthDialog.Show(this, $"OCR failed:\n{ex.Message}", "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        private static byte[] CropBgra(byte[] src, int srcW, int srcH, int x, int y, int cw, int ch, out int outW, out int outH)
        {
            x = Math.Max(0, Math.Min(x, srcW - 1));
            y = Math.Max(0, Math.Min(y, srcH - 1));
            outW = Math.Max(1, Math.Min(cw, srcW - x));
            outH = Math.Max(1, Math.Min(ch, srcH - y));
            var dst = new byte[outW * outH * 4];
            for (int row = 0; row < outH; row++)
                Array.Copy(src, ((y + row) * srcW + x) * 4, dst, row * outW * 4, outW * 4);
            return dst;
        }

        // Primary OCR toolbar button: the common quick action, OCR the current page to the clipboard.
        private void Ocr_Click(object sender, RoutedEventArgs e) => OcrPageToClipboard(PageList.SelectedIndex);

        // Caret dropdown next to the OCR button - same split-button pattern as Save/Open. Page OCR is live;
        // the remaining entries are stubs until their commands land (Region, Searchable PDF, Extract Text).
        private void OcrMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = MakeThemedMenu();
            if (_doc is null)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Ocr_NoDoc"), IsEnabled = false });
            }
            else
            {
                int pageIdx = PageList.SelectedIndex;
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_OcrPage"), (_, _) => OcrPageToClipboard(pageIdx)));
                menu.Items.Add(MakeMenuItem(Loc("Str_Ocr_Region"), (_, _) => BeginOcrRegion()));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Ocr_SearchablePdf"), (_, _) => MakeSearchablePdf()));
                menu.Items.Add(MakeMenuItem(Loc("Str_Ocr_ExtractText"), (_, _) => ExtractAllText()));
                menu.Items.Add(new Separator());
                menu.Items.Add(BuildLanguageMenu());
            }
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Placeholder for OCR commands that are designed but not yet built; keeps the menu complete.
        private void OcrComingSoon(string name) => SetStatus(string.Format(Loc("Str_Ocr_ComingSoon"), name));

        // ============================================================
        // Make Searchable PDF - OCR every page and write an invisible text
        // layer aligned to the image, so the existing PdfPig search and text
        // selection start working on scans.
        // ============================================================

        private async void MakeSearchablePdf()
        {
            if (_doc is null || _currentFile is null) { StealthDialog.Show(this, Loc("Str_Ocr_NoDoc")); return; }
            if (!await EnsureOcrModelsReadyAsync()) return;
            CommitActiveTextBox();

            var dlg = new SaveFileDialog
            {
                Filter = "PDF files|*.pdf",
                Title = "Save Searchable PDF",
                FileName = SuggestSearchableName(),
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            string outPath = dlg.FileName;

            // Snapshot the current document to a temp; we render and re-open from this so the live _doc
            // is never touched. (Unburned overlay annotations are not included in v1.)
            string src = App.MakeTempFile("ocrsrc");
            try { _doc.Save(src); }
            catch (Exception ex)
            {
                StealthDialog.Show(this, $"Could not prepare the document:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = BeginCancellableOp("OCR operation");
            var busy = ShowBusyOverlay("Making searchable PDF...");
            void report(int i, int n) => Dispatcher.Invoke(() =>
                SetBusyMessage(busy, $"Making searchable PDF... page {i + 1} of {n}  (Esc to cancel)"));
            string lang = CurrentOcrLanguageString();

            try
            {
                var (pages, words) = await Task.Run(() => BuildSearchablePdf(src, outPath, report, ct, lang));
                HideBusyOverlay(busy);
                if (ct.IsCancellationRequested) { SetStatus("Searchable PDF cancelled (no file written)"); return; }
                SetStatus($"Searchable PDF saved: {pages} pages, {words} words recognized");
                StealthDialog.Show(this,
                    $"Saved searchable PDF:\n{outPath}\n\n{pages} pages processed, {words} words recognized.",
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                StealthDialog.Show(this, $"Searchable PDF failed:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Builds a searchable PDF by rebuilding the document from rendered page images + an invisible OCR
        // text layer, instead of modifying the source PDF in place. This bypasses PdfSharpCore's strict
        // xref/stream parser (which fails on many "large" or non-standard files with "Unexpected token
        // 'xref'") and works for any file PDFium can render. The output has (a) the page rendered as an
        // image at OCR resolution and (b) real, alpha-0 text over each recognized word so PdfPig extracts
        // it for search/select without printing.
        //
        // Page point size comes from PdfPig where possible (permissive parser); if that fails, we fall back
        // to a Letter-width page and preserve the source pixel aspect ratio.
        private static (int pages, int words) BuildSearchablePdf(string src, string outPath, Action<int, int> report, CancellationToken ct, string language)
        {
            // Cache one XFont per integer point size so a page of words doesn't allocate thousands of fonts.
            var fontCache = new Dictionary<int, XFont>();
            XFont FontFor(double heightPt)
            {
                int key = Math.Max(4, (int)Math.Round(heightPt));
                if (!fontCache.TryGetValue(key, out var f))
                {
                    try { f = new XFont("Arial", key, XFontStyle.Regular); }
                    catch { f = new XFont("Segoe UI", key, XFontStyle.Regular); }
                    fontCache[key] = f;
                }
                return f;
            }

            // Resolve each page's point size via PdfPig; null on failure => aspect-preserving fallback below.
            List<(double wPt, double hPt)>? pagePts = null;
            try
            {
                using var pig = PdfPigDoc.Open(src);
                pagePts = new List<(double, double)>(pig.NumberOfPages);
                for (int i = 1; i <= pig.NumberOfPages; i++)
                {
                    var pp = pig.GetPage(i);
                    pagePts.Add((pp.Width, pp.Height));
                }
            }
            catch { pagePts = null; }

            int totalWords = 0;
            var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

            using var docReader = DocLib.Instance.GetDocReader(src, new PageDimensions(OcrRenderMax, OcrRenderMax));
            using var ocr = new OcrService(language: language);   // one engine reused across the whole document

            var outDoc = new PdfDocument();
            int pageCount = docReader.GetPageCount();

            for (int i = 0; i < pageCount; i++)
            {
                if (ct.IsCancellationRequested) return (i, totalWords);
                report(i, pageCount);

                using var pr = docReader.GetPageReader(i);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage();
                if (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0) continue;

                // Encode the rendered page as JPEG for PdfSharpCore's XImage. JPEG (not PNG)
                // because every page image is held in the in-memory outDoc until the final
                // Save(); a lossless PNG per page piles up hundreds of MB on a long document and
                // OCR of long files ran out of memory before finishing. q82 at OCR render
                // resolution is the standard quality for a searchable-PDF background layer.
                // Bgra32 -> Bgr24 first: pdfium renders opaque pages, and JpegBitmapEncoder has
                // no alpha channel, so dropping the (opaque) alpha avoids any encoder ambiguity.
                var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
                wb.Freeze();
                var bgr24 = new FormatConvertedBitmap(wb, PixelFormats.Bgr24, null, 0);
                bgr24.Freeze();
                byte[] jpegBytes;
                using (var ms = new MemoryStream())
                {
                    var enc = new JpegBitmapEncoder { QualityLevel = 82 };
                    enc.Frames.Add(BitmapFrame.Create(bgr24));
                    enc.Save(ms);
                    jpegBytes = ms.ToArray();
                }

                // Output page point dimensions: prefer PdfPig's true page size; otherwise cap at Letter width.
                double pageWpt, pageHpt;
                if (pagePts != null && i < pagePts.Count && pagePts[i].wPt > 0 && pagePts[i].hPt > 0)
                { pageWpt = pagePts[i].wPt; pageHpt = pagePts[i].hPt; }
                else
                { const double baseW = 612.0; pageWpt = baseW; pageHpt = baseW * h / w; }

                var page = outDoc.AddPage();
                page.Width  = XUnit.FromPoint(pageWpt);
                page.Height = XUnit.FromPoint(pageHpt);

                using var gfx = XGraphics.FromPdfPage(page);
                using (var xImg = XImage.FromStream(() => new MemoryStream(jpegBytes)))
                    gfx.DrawImage(xImg, 0, 0, pageWpt, pageHpt);

                OcrResult result = ocr.RecognizeBgra(bgra, w, h);
                if (result.Words.Count == 0) continue;

                // OCR word boxes are top-left pixel space; map to top-left point space of the output page.
                double sx = pageWpt / w;
                double sy = pageHpt / h;

                foreach (var word in result.Words)
                {
                    double bx = word.Left * sx;
                    double by = word.Top * sy;
                    double bh = Math.Max(1, (word.Bottom - word.Top) * sy);
                    try
                    {
                        gfx.DrawString(word.Text, FontFor(bh), invisible, bx, by);
                        totalWords++;
                    }
                    catch { /* a single word that won't lay out should not abort the page */ }
                }
            }

            if (outDoc.PageCount == 0) throw new InvalidOperationException("No pages could be rendered from the source PDF.");
            outDoc.Save(outPath);
            outDoc.Close();
            return (pageCount, totalWords);
        }
        // ============================================================
        // Extract All Text - OCR every page and save the plain text to a .txt or .md file.
        // ============================================================

        private async void ExtractAllText()
        {
            if (_doc is null || _currentFile is null) { StealthDialog.Show(this, Loc("Str_Ocr_NoDoc")); return; }
            if (!await EnsureOcrModelsReadyAsync()) return;
            CommitActiveTextBox();

            var dlg = new SaveFileDialog
            {
                Filter = "Text file|*.txt|Markdown|*.md",
                Title = "Extract All Text",
                FileName = Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile ?? "document") + ".txt",
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            string outPath = dlg.FileName;
            bool markdown = Path.GetExtension(outPath).Equals(".md", StringComparison.OrdinalIgnoreCase);

            string src = App.MakeTempFile("ocrtxt");
            int pageCount;
            try { _doc.Save(src); pageCount = _doc.PageCount; }
            catch (Exception ex)
            {
                StealthDialog.Show(this, $"Could not prepare the document:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = BeginCancellableOp("OCR operation");
            var busy = ShowBusyOverlay("Extracting text...");
            void report(int i, int n) => Dispatcher.Invoke(() =>
                SetBusyMessage(busy, $"Extracting text... page {i + 1} of {n}  (Esc to cancel)"));
            string lang = CurrentOcrLanguageString();

            try
            {
                int pages = await Task.Run(() => ExtractText(src, pageCount, outPath, markdown, report, ct, lang));
                HideBusyOverlay(busy);
                if (ct.IsCancellationRequested) { SetStatus("Text extraction cancelled (no file written)"); return; }
                SetStatus($"Text extracted from {pages} pages -> {Path.GetFileName(outPath)}");
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                StealthDialog.Show(this, $"Text extraction failed:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // OCR each page and concatenate the text into one file. Markdown gets a "## Page N" heading per
        // page; plain text uses a simple divider. Cancellable - nothing is written if cancelled.
        private static int ExtractText(string src, int pageCount, string outPath, bool markdown,
            Action<int, int> report, CancellationToken ct, string language)
        {
            string nl = Environment.NewLine;
            var sb = new StringBuilder();
            using var docReader = DocLib.Instance.GetDocReader(src, new PageDimensions(OcrRenderMax, OcrRenderMax));
            using var ocr = new OcrService(language: language);

            for (int i = 0; i < pageCount; i++)
            {
                // Cooperative cancel: stop and write nothing if the user cancelled (caller checks the token).
                if (ct.IsCancellationRequested) return 0;
                report(i, pageCount);

                using var pr = docReader.GetPageReader(i);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage();
                string text = (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0)
                    ? string.Empty
                    : ocr.RecognizeBgra(bgra, w, h).Text.TrimEnd();
                // Normalize Tesseract's LF line breaks to the platform's so .txt opens cleanly everywhere.
                text = text.Replace("\r\n", "\n").Replace("\n", nl);

                if (markdown)
                    sb.Append("## Page ").Append(i + 1).Append(nl).Append(nl).Append(text).Append(nl).Append(nl);
                else
                    sb.Append("----- Page ").Append(i + 1).Append(" -----").Append(nl).Append(text).Append(nl).Append(nl);
            }

            if (ct.IsCancellationRequested) return 0;
            File.WriteAllText(outPath, sb.ToString());
            return pageCount;
        }

        // Suggest "<original name>-searchable.pdf" for the save dialog.
        private string SuggestSearchableName()
        {
            string baseName = Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile ?? "document");
            return baseName + "-searchable.pdf";
        }

        // Updates the busy overlay's message line (its TextBlock) for per-page progress. UI thread only.
        private static void SetBusyMessage(Border overlay, string msg)
        {
            if (overlay.Child is StackPanel sp)
                foreach (var c in sp.Children)
                    if (c is TextBlock tb) { tb.Text = msg; return; }
        }
    }
}
