using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PdfSharpCore.Drawing;

namespace StealthPDF
{
    public partial class MainWindow
    {
        // The document's current stamp configuration (one spec drives page numbers and/or a watermark).
        // Reopening the Stamp tool edits this; Apply rebuilds _stamps from it. Stamps live on their own
        // layer, painted BELOW annotations in RenderAllAnnotations.
        private StampSpec? _docStampSpec;
        private Dictionary<int, List<StampInstance>> _stamps = [];
        // Per-page rendered stamp bounds (render-dim/canvas space) so a double-click can hit-test a stamp and
        // reopen the editor. Repopulated every RenderStamps; the stamp visuals themselves stay non-hit-testable.
        private Dictionary<int, List<Rect>> _stampHitRects = [];

        private void ToolStamp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }
            OpenStampTool();
        }

        // Opens a small dialog to pre-configure the default certification stamp settings
        // (name, label, signature, logo, position). Changes persist to CertStampStore and
        // pre-fill every future Stamp window.
        private void StampDefaults_Click(object sender, RoutedEventArgs e)
        {
            new CertDefaultsWindow(this).ShowDialog();
        }

        // Opens the Stamp window seeded with the current spec (so it edits the existing stamps).
        private void OpenStampTool()
        {
            if (_doc is null) return;
            int pageIdx = PageList.SelectedIndex < 0 ? 0 : PageList.SelectedIndex;
            var src = RenderPageBitmap(pageIdx, 1100, BurnPageAnnotationsToTemp(pageIdx));
            if (src is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }

            var page = _doc.Pages[pageIdx];
            var (pwpt, phpt) = EffectivePageSize(page);
            // Seed the spec with the saved certification defaults when opening fresh, so a staff member's
            // one-time setup (name, logo, signature choice, which fields show) is pre-filled every day.
            var seedSpec = _docStampSpec;
            if (seedSpec is null)
            {
                seedSpec = new StampSpec();
                if (new Services.CertStampStore().Load() is { } cd)
                    Services.CertStampStore.ApplyToSpec(cd, seedSpec);
            }
            var win = new StampWindow(this, src, pwpt, phpt, _doc.PageCount, pageIdx, seedSpec,
                idx =>   // page-render callback for the preview stepper
                {
                    var s = RenderPageBitmap(idx, 1100, BurnPageAnnotationsToTemp(idx));
                    var (w, h) = EffectivePageSize(_doc!.Pages[idx]);
                    return (s, w, h);
                });
            win.ShowDialog();
            if (win.Applied) ApplyStampSpec(win.Result);
        }

        private void ApplyStampSpec(StampSpec spec)
        {
            _docStampSpec = (spec.NumbersEnabled || spec.WmEnabled || spec.CertEnabled) ? spec : null;
            // Persist the certification defaults so the next session pre-fills them (the date is not
            // persisted; it resolves to today on each apply).
            if (spec.CertEnabled)
            {
try { new Services.CertStampStore().SaveFromSpec(spec); } catch { }
            }
            UpdateStampIndicator();
            RebuildStamps();
            RerenderAllVisiblePages();
            MarkDirty();
            int pages = 0;
            foreach (var kv in _stamps) if (kv.Value.Count > 0) pages++;
            SetStatus(string.Format(Loc("Str_Stamp_Applied"), pages));
        }

        // Regenerates the per-page stamp instances from the active spec.
        private void RebuildStamps()
        {
            _stamps.Clear();
            if (_docStampSpec is null || _doc is null) return;
            int n = _doc.PageCount;

            if (_docStampSpec.NumbersEnabled)
                foreach (int p in StampPageRange(_docStampSpec.NumRange, n))
                    AddStamp(p, StampKind.PageNumber);

            if (_docStampSpec.WmEnabled)
                foreach (int p in StampPageRange(_docStampSpec.WmRange, n))
                    AddStamp(p, StampKind.Watermark);
            if (_docStampSpec.CertEnabled)
                foreach (int p in StampPageRange(_docStampSpec.CertRange, n))
                    AddStamp(p, StampKind.Certification);
        }

        // Keeps the Stamp toolbar button showing a persistent "hovered" (gray) background while the document
        // has active stamps, as a subtle indicator. Cleared when there are no stamps.
        private void UpdateStampIndicator()
        {
            if (ToolStampBtn is null) return;
            if (_docStampSpec is not null)
                ToolStampBtn.SetResourceReference(Control.BackgroundProperty, "BgHover");
            else
                ToolStampBtn.ClearValue(Control.BackgroundProperty);
        }

        private void AddStamp(int page, StampKind kind)
        {
            if (!_stamps.TryGetValue(page, out var list)) { list = []; _stamps[page] = list; }
            list.Add(new StampInstance { PageIndex = page, Kind = kind, Spec = _docStampSpec! });
        }

        private void RerenderAllVisiblePages()
        {
            if (_doc is null) return;
            // Re-render every currently-mapped page (the primary tile plus all multi-page tiles), so stamps
            // show on every visible page in Grid / Two-Page / Continuous, not just the selected one.
            foreach (int p in new List<int>(_pages.Keys)) RenderAllAnnotations(p);
        }

        // Painted by RenderAllAnnotations onto the page's annotation canvas, BEFORE the annotations, so
        // stamps sit visually beneath them. Coordinates are the same 2048-based render-dim space the page
        // numbers used originally, so placement matches the rest of the annotation layer.
        private void RenderStamps(int pageIndex)
        {
            _stampHitRects[pageIndex] = [];   // reset; repopulated below as stamps render
            if (_docStampSpec is null || _doc is null) return;
            if (!_stamps.TryGetValue(pageIndex, out var list) || list.Count == 0) return;

            var (rdW, rdH, _, phpt) = StampRenderDims(pageIndex);
            if (rdW <= 0 || rdH <= 0) return;
            double mx = rdW * 0.05, my = rdH * 0.04;
            var spec = _docStampSpec;

            // First page that carries a number, so numbering starts at StartNumber there.
            int firstNumPage = -1;
            if (spec.NumbersEnabled)
                foreach (int p in StampPageRange(spec.NumRange, _doc.PageCount)) { firstNumPage = p; break; }

            foreach (var st in list)
            {
                if (st.Kind == StampKind.Watermark) RenderWatermark(spec, pageIndex, rdW, rdH, phpt, mx, my);
                else if (st.Kind == StampKind.Certification) RenderCertification(spec, pageIndex, rdW, rdH, phpt, mx, my);
                else RenderPageNumber(spec, pageIndex, firstNumPage, rdW, rdH, phpt, mx, my);
            }
        }

        private void RenderPageNumber(StampSpec spec, int pageIndex, int firstNumPage, double rdW, double rdH, double phpt, double mx, double my)
        {
            double fontCanvas = spec.NumFontPt * rdH / Math.Max(1, phpt);
            int number = spec.StartNumber + Math.Max(0, pageIndex - Math.Max(0, firstNumPage));
            string text = (string.IsNullOrEmpty(spec.Format) ? "{n}" : spec.Format)
                .Replace("{n}", number.ToString())
                .Replace("{N}", (_doc?.PageCount ?? 1).ToString());
            if (text.Length == 0) return;

            var tb = new TextBlock { Text = text, FontFamily = UiKit.UiFont, FontSize = Math.Max(1, fontCanvas), Foreground = new SolidColorBrush(spec.NumColor), IsHitTestVisible = false };
            var sz = MeasureEl(tb);
            int posH = spec.NumPosH;
            double x, y;
            if (posH < 0)   // custom position (center as a fraction of the page)
            {
                double cx = spec.NumCustomX;
                if (spec.NumMirror && (pageIndex % 2 == 1)) cx = 1 - cx;   // mirror flips the x-fraction
                x = cx * rdW - sz.Width / 2;
                y = spec.NumCustomY * rdH - sz.Height / 2;
            }
            else
            {
                // Mirror: on alternating pages flip left<->right so the number sits on the outer edge of a spread.
                if (spec.NumMirror && posH != 1 && (pageIndex % 2 == 1)) posH = 2 - posH;
                x = posH == 0 ? mx : posH == 2 ? rdW - sz.Width - mx : (rdW - sz.Width) / 2;
                y = spec.NumPosV == 0 ? my : spec.NumPosV == 1 ? (rdH - sz.Height) / 2 : rdH - sz.Height - my;
            }
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            _activeCanvas.Children.Add(tb);
            if (_stampHitRects.TryGetValue(pageIndex, out var rects)) rects.Add(new Rect(x, y, sz.Width, sz.Height));
        }

        private void RenderWatermark(StampSpec spec, int pageIndex, double rdW, double rdH, double phpt, double mx, double my)
        {
            FrameworkElement el;
            double w, h;
            if (spec.WmIsImage && !string.IsNullOrEmpty(spec.WmImagePath) && System.IO.File.Exists(spec.WmImagePath))
            {
                BitmapImage? bmp = LoadImageFile(spec.WmImagePath!);
                if (bmp is null) return;
                w = rdW * 0.5 * spec.WmScale;
                h = w * bmp.PixelHeight / Math.Max(1, bmp.PixelWidth);
                el = new Image { Source = bmp, Width = w, Height = h, Opacity = spec.WmOpacity, Stretch = Stretch.Fill, IsHitTestVisible = false };
            }
            else
            {
                if (string.IsNullOrEmpty(spec.WmText)) return;
                double fontCanvas = spec.WmFontPt * rdH / Math.Max(1, phpt);
                var tb = new TextBlock { Text = spec.WmText, FontFamily = new FontFamily(string.IsNullOrWhiteSpace(spec.WmFont) ? "Segoe UI" : spec.WmFont), FontWeight = FontWeights.Bold, FontSize = Math.Max(1, fontCanvas), Foreground = new SolidColorBrush(spec.WmColor), Opacity = spec.WmOpacity, IsHitTestVisible = false };
                var sz = MeasureEl(tb);
                w = sz.Width; h = sz.Height;
                el = tb;
            }

            double x, y;
            if (spec.WmPosH < 0)   // custom position (center as a fraction of the page)
            {
                x = spec.WmCustomX * rdW - w / 2;
                y = spec.WmCustomY * rdH - h / 2;
            }
            else
            {
                x = spec.WmPosH == 0 ? mx : spec.WmPosH == 2 ? rdW - w - mx : (rdW - w) / 2;
                y = spec.WmPosV == 0 ? my : spec.WmPosV == 1 ? (rdH - h) / 2 : rdH - h - my;
            }
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new RotateTransform(-spec.WmAngle);
            Canvas.SetLeft(el, x);
            _activeCanvas.Children.Add(el);
            if (_stampHitRects.TryGetValue(pageIndex, out var rects)) rects.Add(new Rect(x, y, w, h));
        }

        // ---- Certification stamp: a composite "Document seen by" block, one draggable unit ----


        // Builds the whole certification block as a single themed Border and places it on the page canvas.
        // The block is laid out top-to-bottom: logo+label row, name, signature image, date/time line. Every
        // field honours its Show* toggle so a staff member configures once and reuses. Position is driven by
        // one (x,y) fraction of the page so it lands identically on every page in the range; the block is
        // draggable here in the on-canvas render so the user can nudge it onto white space and reopen it.
        private void RenderCertification(StampSpec spec, int pageIndex, double rdW, double rdH, double phpt, double mx, double my)
        {
            var block = BuildCertBlock(spec, pageIndex, rdH, phpt, sigBitmap: ResolveSignatureBitmap(spec));
            if (block is null) return;
            var sz = MeasureEl(block);
            double w = sz.Width, h = sz.Height;

            double x, y;
            if (spec.CertPosH < 0)   // custom: drag anywhere (white space preferred)
            {
                x = spec.CertCustomX * rdW - w / 2;
                y = spec.CertCustomY * rdH - h / 2;
                _overlayForStamps = _activeCanvas;
                MakeCertDraggable(block, w, h, rdW, rdH, (fx, fy) => { spec.CertCustomX = fx; spec.CertCustomY = fy; });
            }
            else
            {
                x = spec.CertPosH == 0 ? mx : spec.CertPosH == 2 ? rdW - w - mx : (rdW - w) / 2;
                y = spec.CertPosV == 0 ? my : spec.CertPosV == 1 ? (rdH - h) / 2 : rdH - h - my;
            }
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);
            _activeCanvas.Children.Add(block);
            if (_stampHitRects.TryGetValue(pageIndex, out var rects)) rects.Add(new Rect(x, y, w, h));
        }

        // Holds the canvas the certification block is rendered onto, so the drag helper can map mouse coords.
        private Canvas? _overlayForStamps;

        private void MakeCertDraggable(FrameworkElement el, double elW, double elH, double pw, double ph, Action<double, double> onMove)
        {
            el.IsHitTestVisible = true;
            el.Cursor = Cursors.SizeAll;
            bool dragging = false;
            el.MouseLeftButtonDown += (_, e) => { dragging = true; el.CaptureMouse(); e.Handled = true; };
            el.MouseLeftButtonUp += (_, _2) => { dragging = false; el.ReleaseMouseCapture(); };
            el.MouseMove += (_, e) =>
            {
                if (!dragging || _overlayForStamps is null) return;
                var p = e.GetPosition(_overlayForStamps);
                double left = Math.Max(0, Math.Min(pw - elW, p.X - elW / 2));
                double top = Math.Max(0, Math.Min(ph - elH, p.Y - elH / 2));
                Canvas.SetLeft(el, left); Canvas.SetTop(el, top);
                onMove(pw > 0 ? Math.Max(0, Math.Min(1, (left + elW / 2) / pw)) : 0.5,
                       ph > 0 ? Math.Max(0, Math.Min(1, (top + elH / 2) / ph)) : 0.5);
            };
        }

        // Builds the composite block as a WPF Border. On screen this is the live preview/on-canvas render;
        // the PDF burn path (DrawCertificationPdf) mirrors this layout field-for-field. Every field honours
        // its Show* toggle. Returns null when nothing is enabled.
        private Border? BuildCertBlock(StampSpec spec, int pageIndex, double rdH, double phpt, BitmapSource? sigBitmap)
        {
            bool anyText = spec.CertShowName || spec.CertShowDate || spec.CertShowSig || !string.IsNullOrWhiteSpace(spec.CertLabel);
            if (!anyText && !spec.CertShowLogo) return null;

            double ptPerPx = phpt > 0 ? phpt / Math.Max(1, rdH) : 1;
            double basePt = 10 * spec.CertScale;
            double fontPx = basePt / ptPerPx;
            var brush = new SolidColorBrush(spec.CertColor);
            var fill = spec.CertWhiteFill ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) : null;
            var borderBrush = spec.CertBorder == 2 ? null : new SolidColorBrush(spec.CertColor);
            double bw = spec.CertBorder == 2 ? 0 : 1.2 / ptPerPx;
            double corner = spec.CertBorder == 1 ? 5.0 : 0.0;

            var block = new Border
            {
                Background = fill, BorderBrush = borderBrush, BorderThickness = new Thickness(bw),
                CornerRadius = new CornerRadius(corner),
                Padding = new Thickness(10 / ptPerPx, 7 / ptPerPx, 10 / ptPerPx, 7 / ptPerPx),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Direction = 315, Opacity = 0.35 }
            };
            var sp = new StackPanel();
            AddCertTopRow(sp, spec, brush, fontPx, ptPerPx);
            if (spec.CertShowName && !string.IsNullOrWhiteSpace(spec.CertName))
                sp.Children.Add(new TextBlock { Text = spec.CertName, FontFamily = UiKit.UiFont, FontSize = fontPx * 1.05, Foreground = brush, Margin = new Thickness(0, 0, 0, 4 / ptPerPx), IsHitTestVisible = false });
            if (spec.CertShowSig && sigBitmap is not null)
            {
                double sigH = 34 * spec.CertScale / ptPerPx;
                double sigW = sigH * sigBitmap.PixelWidth / Math.Max(1, sigBitmap.PixelHeight);
                sp.Children.Add(new Image { Source = sigBitmap, Width = sigW, Height = sigH, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 4 / ptPerPx), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left });
            }
            if (spec.CertShowDate)
            {
                string date = string.IsNullOrWhiteSpace(spec.CertDate) ? DateTime.Now.ToString("dd-MMM-yyyy") : spec.CertDate;
                string line = spec.CertShowTime ? $"{date}  {DateTime.Now:HH:mm}" : date;
                sp.Children.Add(new TextBlock { Text = line, FontFamily = UiKit.UiFont, FontSize = fontPx * 0.9, Foreground = brush, Opacity = 0.85, IsHitTestVisible = false });
            }
            block.Child = sp;
            return block;
        }

        private void AddCertTopRow(StackPanel sp, StampSpec spec, SolidColorBrush brush, double fontPx, double ptPerPx)
        {
            bool hasLogo = spec.CertShowLogo && !string.IsNullOrEmpty(spec.CertLogoPath) && System.IO.File.Exists(spec.CertLogoPath);
            bool hasLabel = !string.IsNullOrWhiteSpace(spec.CertLabel);
            if (hasLogo)
            {
                var logo = LoadImageFile(spec.CertLogoPath!);
                if (logo is not null)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3 / ptPerPx) };
                    double logoPx = 26 * spec.CertScale * spec.CertLogoScale / ptPerPx;
                    double logoW = logo.PixelWidth > 0 && logo.PixelHeight > 0 ? logoPx * logo.PixelWidth / logo.PixelHeight : logoPx;
                    var img = new Image { Source = logo, Width = logoW, Height = logoPx, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8 / ptPerPx, 0), IsHitTestVisible = false };
                    DockPanel.SetDock(img, Dock.Left); row.Children.Add(img);
                    if (hasLabel) row.Children.Add(new TextBlock { Text = spec.CertLabel, FontFamily = UiKit.UiFont, FontSize = fontPx, FontWeight = FontWeights.SemiBold, Foreground = brush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false });
                    sp.Children.Add(row); return;
                }
            }
            if (hasLabel)
                sp.Children.Add(new TextBlock { Text = spec.CertLabel, FontFamily = UiKit.UiFont, FontSize = fontPx, FontWeight = FontWeights.SemiBold, Foreground = brush, Margin = new Thickness(0, 0, 0, 3 / ptPerPx), TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false });
        }

        // True if a point (render-dim/canvas space) falls on a rendered stamp on this page - used to reopen
        // the Stamp Pages editor on double-click.
        // Resolves the configured signature to a bitmap for on-screen rendering. Saved-signature id first
        // (image sigs decode from base64; drawn sigs rasterise), else a one-off image path. Null if nothing.
        private BitmapSource? ResolveSignatureBitmap(StampSpec spec)
        {
            if (!spec.CertShowSig) return null;
            if (!string.IsNullOrEmpty(spec.CertSignatureId))
            {
                var sig = _signatureStore.Signatures.FirstOrDefault(s => s.Id == spec.CertSignatureId);
                if (sig is null) return null;
                if (!string.IsNullOrEmpty(sig.ImageData))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(sig.ImageData!);
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.StreamSource = new System.IO.MemoryStream(bytes); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
                        return bmp;
                    }
                    catch { return null; }
                }
                return sig.Strokes is { Count: > 0 } ? RasterizeDrawnSignature(sig) : null;
            }
            if (!string.IsNullOrEmpty(spec.CertSigPath) && System.IO.File.Exists(spec.CertSigPath))
                return LoadImageFile(spec.CertSigPath!);
            return null;
        }

        // Rasterises a drawn (stroke) saved signature to a BitmapSource so it can sit in the cert block like
        // an image signature. Mirrors the Signing popup preview rendering at native canvas DPI.
        private static BitmapSource RasterizeDrawnSignature(SavedSignature sig)
        {
            double w = sig.CanvasWidth, h = sig.CanvasHeight;
            var canvas = new Canvas { Width = w, Height = h, Background = Brushes.Transparent };
            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline { Stroke = Brushes.Black, StrokeThickness = Math.Max(0.8, sig.StrokeWidth), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                foreach (var pt in stroke) poly.Points.Add(new Point(pt.X, pt.Y));
                canvas.Children.Add(poly);
            }
            canvas.Measure(new Size(w, h)); canvas.Arrange(new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(canvas); rtb.Freeze();
            return rtb;
        }

        // the Stamp Pages editor on double-click.
        private bool StampHitTest(int pageIndex, Point pos)
        {
            if (_stampHitRects.TryGetValue(pageIndex, out var rects))
                foreach (var r in rects) if (r.Contains(pos)) return true;
            return false;
        }

        // (rdW, rdH) in the 2048-based render-dim space; phpt/pwpt the page size in points (rotation-aware).
        private (double rdW, double rdH, double pwpt, double phpt) StampRenderDims(int pageIndex)
        {
            if (_doc is null) return (0, 0, 0, 0);
            double pw = _doc.Pages[pageIndex].Width.Point;
            double ph = _doc.Pages[pageIndex].Height.Point;
            if (_pageRotations.TryGetValue(pageIndex, out int rot) && (rot == 90 || rot == 270)) (pw, ph) = (ph, pw);
            double maxDim = Math.Max(1, Math.Max(pw, ph));
            return (2048.0 * pw / maxDim, 2048.0 * ph / maxDim, pw, ph);
        }

        private static Size MeasureEl(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize;
        }

        private static BitmapImage? LoadImageFile(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // 0-based page indices for a 1-based "1-3,5" range string ("" = all pages).
        private static IEnumerable<int> StampPageRange(string range, int pageCount)
        {
            var set = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(range))
            {
                for (int i = 0; i < pageCount; i++) set.Add(i);
                return set;
            }
            foreach (var part in range.Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    if (int.TryParse(p[..dash].Trim(), out int a) && int.TryParse(p[(dash + 1)..].Trim(), out int b))
                        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (i >= 1 && i <= pageCount) set.Add(i - 1);
                }
                else if (int.TryParse(p, out int single) && single >= 1 && single <= pageCount) set.Add(single - 1);
            }
            return set;
        }

        // ---- Export: burn the stamp layer into the PDF (below annotations) on save/flatten ----

        // Draws the active stamps into the doc via XGraphics, in PDF-point space. Called BEFORE
        // DrawAnnotationsOnDocument at each save site so stamps sit beneath annotations.
private void DrawStampsOnDocument(int? onlyPage = null) => DrawStampsIntoDoc(_doc, _docStampSpec, _signatureStore, onlyPage);

        // Static so the print flow can run it on a background thread against a throwaway document copy.
        private static void DrawStampsIntoDoc(PdfSharpCore.Pdf.PdfDocument? doc, StampSpec? spec, Services.SignatureStore? sigStore, int? onlyPage = null)
        {
            if (doc is null || spec is null || (!spec.NumbersEnabled && !spec.WmEnabled && !spec.CertEnabled)) return;
            int n = doc.PageCount;

            HashSet<int> numPages  = spec.NumbersEnabled ? [.. StampPageRange(spec.NumRange, n)]  : [];
            HashSet<int> wmPages   = spec.WmEnabled     ? [.. StampPageRange(spec.WmRange, n)]   : [];
            HashSet<int> certPages = spec.CertEnabled   ? [.. StampPageRange(spec.CertRange, n)] : [];
            int firstNumPage = int.MaxValue;
            foreach (int p in numPages) if (p < firstNumPage) firstNumPage = p;
            if (firstNumPage == int.MaxValue) firstNumPage = 0;

            // Pre-fade the watermark image once (reused for all pages).
            XImage? wmImg = null;
            if (spec.WmEnabled && spec.WmIsImage && !string.IsNullOrEmpty(spec.WmImagePath) && System.IO.File.Exists(spec.WmImagePath))
                wmImg = LoadStampImage(spec.WmImagePath!, spec.WmOpacity);

            // Pre-resolve the certification logo + signature XImages once (reused for all cert pages).
            XImage? certLogo = null, certSig = null;
            if (spec.CertEnabled)
            {
                if (spec.CertShowLogo && !string.IsNullOrEmpty(spec.CertLogoPath) && System.IO.File.Exists(spec.CertLogoPath))
                    certLogo = LoadStampImage(spec.CertLogoPath!, 1.0);
                certSig = ResolveCertSignatureXImage(spec, sigStore);
            }

            for (int i = 0; i < n && i < doc.PageCount; i++)
            {
                if (onlyPage.HasValue && i != onlyPage.Value) continue;
                bool doNum = numPages.Contains(i);
                bool doWm = wmPages.Contains(i);
                bool doCert = certPages.Contains(i);
                if (!doNum && !doWm && !doCert) continue;

                var page = doc.Pages[i];
                double pw = page.Width.Point, ph = page.Height.Point;
                double mx = pw * 0.05, my = ph * 0.04;
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                if (doWm)   DrawWatermarkPdf(gfx, spec, pw, ph, mx, my, wmImg);   // watermark first (underneath)
                if (doCert) DrawCertificationPdf(gfx, spec, i, pw, ph, mx, my, certLogo, certSig);
                if (doNum)  DrawNumberPdf(gfx, spec, i, firstNumPage, n, pw, ph, mx, my);
            }
        }

        private static void DrawNumberPdf(XGraphics gfx, StampSpec spec, int pageIndex, int firstNumPage, int total, double pw, double ph, double mx, double my)
        {
            int number = spec.StartNumber + Math.Max(0, pageIndex - firstNumPage);
            string text = (string.IsNullOrEmpty(spec.Format) ? "{n}" : spec.Format)
                .Replace("{n}", number.ToString()).Replace("{N}", total.ToString());
            if (text.Length == 0) return;

            var font = new XFont("Segoe UI", Math.Max(1, spec.NumFontPt), XFontStyle.Regular);
            var c = spec.NumColor;
            var brush = new XSolidBrush(XColor.FromArgb(255, c.R, c.G, c.B));
            var size = gfx.MeasureString(text, font);
            double w = size.Width, h = size.Height;

            int posH = spec.NumPosH;
            double x, y;
            if (posH < 0)   // custom
            {
                double cx = spec.NumCustomX;
                if (spec.NumMirror && (pageIndex % 2 == 1)) cx = 1 - cx;
                x = cx * pw - w / 2; y = spec.NumCustomY * ph - h / 2;
            }
            else
            {
                if (spec.NumMirror && posH != 1 && (pageIndex % 2 == 1)) posH = 2 - posH;
                x = posH == 0 ? mx : posH == 2 ? pw - w - mx : (pw - w) / 2;
                y = spec.NumPosV == 0 ? my : spec.NumPosV == 1 ? (ph - h) / 2 : ph - h - my;
            }
            gfx.DrawString(text, font, brush, new XRect(x, y, w, h), XStringFormats.TopLeft);
        }

        private static void DrawWatermarkPdf(XGraphics gfx, StampSpec spec, double pw, double ph, double mx, double my, XImage? img)
        {
            double w, h;
            XFont? font = null;
            if (spec.WmIsImage)
            {
                if (img is null) return;
                w = pw * 0.5 * spec.WmScale;
                h = w * img.PixelHeight / Math.Max(1, img.PixelWidth);
            }
            else
            {
                if (string.IsNullOrEmpty(spec.WmText)) return;
                try { font = new XFont(string.IsNullOrWhiteSpace(spec.WmFont) ? "Segoe UI" : spec.WmFont, Math.Max(1, spec.WmFontPt), XFontStyle.Bold); }
                catch { font = new XFont("Segoe UI", Math.Max(1, spec.WmFontPt), XFontStyle.Bold); }
                var size = gfx.MeasureString(spec.WmText, font);
                w = size.Width; h = size.Height;
            }

            double cx, cy;
            if (spec.WmPosH < 0) { cx = spec.WmCustomX * pw; cy = spec.WmCustomY * ph; }
            else
            {
                cx = spec.WmPosH == 0 ? mx + w / 2 : spec.WmPosH == 2 ? pw - mx - w / 2 : pw / 2;
                cy = spec.WmPosV == 0 ? my + h / 2 : spec.WmPosV == 1 ? ph / 2 : ph - my - h / 2;
            }

            var state = gfx.Save();
            gfx.TranslateTransform(cx, cy);
            gfx.RotateTransform(-spec.WmAngle);
            if (spec.WmIsImage)
            {
                gfx.DrawImage(img, -w / 2, -h / 2, w, h);
            }
            else
            {
                byte a = (byte)Math.Max(0, Math.Min(255, spec.WmOpacity * 255));
                var c = spec.WmColor;
                gfx.DrawString(spec.WmText, font, new XSolidBrush(XColor.FromArgb(a, c.R, c.G, c.B)), new XRect(-w / 2, -h / 2, w, h), XStringFormats.Center);
            }
            gfx.Restore(state);
        }
        // ---- Certification stamp burn to PDF ----

        // Burns the composite certification block with XGraphics, mirroring BuildCertBlock's layout
        // field-for-field: optional white-fill rounded rectangle, logo + label row, name, signature image,
        // date/time line. Position is the same (x,y) fraction used on screen so the saved PDF matches.
        private static void DrawCertificationPdf(XGraphics gfx, StampSpec spec, int pageIndex, double pw, double ph, double mx, double my, XImage? logo, XImage? sig)
        {
            double basePt = 10 * spec.CertScale;
            var brush = new XSolidBrush(XColor.FromArgb(255, spec.CertColor.R, spec.CertColor.G, spec.CertColor.B));
            var labelFont = new XFont("Segoe UI", basePt, XFontStyle.Bold);
            var nameFont  = new XFont("Segoe UI", basePt * 1.05, XFontStyle.Regular);
            var dateFont  = new XFont("Segoe UI", basePt * 0.9, XFontStyle.Regular);
            double pad = 7 * spec.CertScale, innerPad = 10 * spec.CertScale;

            double contentW = 0, contentH = 0;
            double logoSize = 26 * spec.CertScale * spec.CertLogoScale, sigH = 34 * spec.CertScale;
            double logoDrawW = logo is not null && logo.PointWidth > 0 && logo.PointHeight > 0 ? logoSize * logo.PointWidth / logo.PointHeight : logoSize;
            if (spec.CertShowLogo && logo is not null) contentW = Math.Max(contentW, logoDrawW + 6 + (spec.CertLabel.Length > 0 ? gfx.MeasureString(spec.CertLabel, labelFont).Width : 0));
            else if (spec.CertLabel.Length > 0) contentW = Math.Max(contentW, gfx.MeasureString(spec.CertLabel, labelFont).Width);
            if (spec.CertShowName && spec.CertName.Length > 0) contentW = Math.Max(contentW, gfx.MeasureString(spec.CertName, nameFont).Width);
            if (spec.CertShowSig && sig is not null) contentW = Math.Max(contentW, sigH * sig.PixelWidth / Math.Max(1, sig.PixelHeight));
            if (spec.CertShowDate)
            {
                string d = string.IsNullOrEmpty(spec.CertDate) ? DateTime.Now.ToString("dd-MMM-yyyy") : spec.CertDate;
                contentW = Math.Max(contentW, gfx.MeasureString(spec.CertShowTime ? $"{d}  {DateTime.Now:HH:mm}" : d, dateFont).Width);
            }
            if (spec.CertShowLogo && logo is not null && spec.CertLabel.Length > 0) contentH += Math.Max(logoSize, gfx.MeasureString(spec.CertLabel, labelFont).Height) + 3;
            else if (spec.CertLabel.Length > 0) contentH += gfx.MeasureString(spec.CertLabel, labelFont).Height + 3;
            if (spec.CertShowName && spec.CertName.Length > 0) contentH += gfx.MeasureString(spec.CertName, nameFont).Height + 4;
            if (spec.CertShowSig && sig is not null) contentH += sigH + 4;
            if (spec.CertShowDate) contentH += gfx.MeasureString("Mg", dateFont).Height;
            if (contentW <= 0 || contentH <= 0) return;

            double blockW = contentW + innerPad * 2, blockH = contentH + pad * 2;
            double bx, by;
            if (spec.CertPosH < 0) { bx = spec.CertCustomX * pw - blockW / 2; by = spec.CertCustomY * ph - blockH / 2; }
            else
            {
                bx = spec.CertPosH == 0 ? mx : spec.CertPosH == 2 ? pw - blockW - mx : (pw - blockW) / 2;
                by = spec.CertPosV == 0 ? my : spec.CertPosV == 1 ? (ph - blockH) / 2 : ph - blockH - my;
            }

            if (spec.CertWhiteFill) gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), bx, by, blockW, blockH);
            if (spec.CertBorder != 2)
            {
                var pen = new XPen(XColor.FromArgb(255, spec.CertColor.R, spec.CertColor.G, spec.CertColor.B), 1.2);
                if (spec.CertBorder == 1) gfx.DrawRoundedRectangle(pen, bx, by, blockW, blockH, 5, 5); else gfx.DrawRectangle(pen, bx, by, blockW, blockH);
            }

            double x = bx + innerPad, y = by + pad;
            if (spec.CertShowLogo && logo is not null && spec.CertLabel.Length > 0)
            {
                double logoW = logo.PointWidth > 0 && logo.PointHeight > 0 ? logoSize * logo.PointWidth / logo.PointHeight : logoSize;
                gfx.DrawImage(logo, x, y, logoW, logoSize);
                gfx.DrawString(spec.CertLabel, labelFont, brush, new XRect(x + logoW + 6, y, contentW - logoW - 6, Math.Max(logoSize, gfx.MeasureString(spec.CertLabel, labelFont).Height)), XStringFormats.CenterLeft);
                y += Math.Max(logoSize, gfx.MeasureString(spec.CertLabel, labelFont).Height) + 3;
            }
            else if (spec.CertLabel.Length > 0) { gfx.DrawString(spec.CertLabel, labelFont, brush, new XRect(x, y, contentW, gfx.MeasureString(spec.CertLabel, labelFont).Height), XStringFormats.TopLeft); y += gfx.MeasureString(spec.CertLabel, labelFont).Height + 3; }
            if (spec.CertShowName && spec.CertName.Length > 0) { gfx.DrawString(spec.CertName, nameFont, brush, new XRect(x, y, contentW, gfx.MeasureString(spec.CertName, nameFont).Height), XStringFormats.TopLeft); y += gfx.MeasureString(spec.CertName, nameFont).Height + 4; }
            if (spec.CertShowSig && sig is not null) { double sw = sigH * sig.PixelWidth / Math.Max(1, sig.PixelHeight); gfx.DrawImage(sig, x, y, sw, sigH); y += sigH + 4; }
            if (spec.CertShowDate)
            {
                string d = string.IsNullOrEmpty(spec.CertDate) ? DateTime.Now.ToString("dd-MMM-yyyy") : spec.CertDate;
                gfx.DrawString(spec.CertShowTime ? $"{d}  {DateTime.Now:HH:mm}" : d, dateFont, brush, new XRect(x, y, contentW, gfx.MeasureString("Mg", dateFont).Height), XStringFormats.TopLeft);
            }
        }

        // Resolves the configured signature to an XImage for the PDF burn. Saved-signature id first
        // (image sigs decode; drawn sigs rasterise to PNG), else a one-off image path.
        private static XImage? ResolveCertSignatureXImage(StampSpec spec, Services.SignatureStore? sigStore)
        {
            if (!spec.CertShowSig) return null;
            if (!string.IsNullOrEmpty(spec.CertSignatureId))
            {
                var sig = sigStore?.Signatures.FirstOrDefault(s => s.Id == spec.CertSignatureId);
                if (sig is null) return null;
                if (!string.IsNullOrEmpty(sig.ImageData))
                {
                    try { return XImage.FromStream(() => new System.IO.MemoryStream(Convert.FromBase64String(sig.ImageData!))); } catch { return null; }
                }
                return sig.Strokes is { Count: > 0 } ? RasterizeDrawnSignatureToXImage(sig) : null;
            }
            if (!string.IsNullOrEmpty(spec.CertSigPath) && System.IO.File.Exists(spec.CertSigPath))
            {
                try { return XImage.FromFile(spec.CertSigPath!); } catch { return null; }
            }
            return null;
        }

        // Rasterises a drawn signature to PNG bytes for XImage embedding in the burn path.
        private static XImage? RasterizeDrawnSignatureToXImage(SavedSignature sig)
        {
            double w = sig.CanvasWidth, h = sig.CanvasHeight;
            var canvas = new Canvas { Width = w, Height = h, Background = Brushes.Transparent };
            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline { Stroke = Brushes.Black, StrokeThickness = Math.Max(0.8, sig.StrokeWidth), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                foreach (var pt in stroke) poly.Points.Add(new Point(pt.X, pt.Y));
                canvas.Children.Add(poly);
            }
            canvas.Measure(new Size(w, h)); canvas.Arrange(new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(canvas);
            using var ms = new System.IO.MemoryStream();
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb)); enc.Save(ms);
            var bytes = ms.ToArray();
            return XImage.FromStream(() => new System.IO.MemoryStream(bytes));
        }



        // Loads a watermark image as an XImage, pre-faded to the requested opacity (PdfSharpCore has no
        // per-draw image opacity, so we bake it into the pixels).
        private static XImage? LoadStampImage(string path, double opacity)
        {
            try
            {
                byte[] bytes;
                if (opacity >= 0.999)
                {
                    bytes = System.IO.File.ReadAllBytes(path);
                }
                else
                {
                    using var src = System.Drawing.Image.FromFile(path);
                    using var bmp = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)Math.Max(0, Math.Min(1, opacity)) };
                        using var ia = new System.Drawing.Imaging.ImageAttributes();
                        ia.SetColorMatrix(cm);
                        g.DrawImage(src, new System.Drawing.Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel, ia);
                    }
                    using var ms = new System.IO.MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    bytes = ms.ToArray();
                }
                return XImage.FromStream(() => new System.IO.MemoryStream(bytes));
            }
            catch { return null; }
        }
    }
}
