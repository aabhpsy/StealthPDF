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
    // Page viewport: builds the page tiles and annotation overlays for all four view modes (single,
    // continuous, two-page, grid) and handles preview scrolling.
    public partial class MainWindow
    {
        private void ScrollContinuousToPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _continuousTops.Count) return;
            double target = _continuousTops[pageIndex] * _zoomLevel;
            PagePreviewPanel.ScrollToVerticalOffset(target);
        }

        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // The vertical scrollbar can appear/disappear without a window resize (zoom, page count
            // changes). When it does, re-anchor the annotate bars so a right-docked bar tracks the
            // scrollbar's edge instead of getting covered (or stranded once it's gone).
            bool vis = PagePreviewPanel.ComputedVerticalScrollBarVisibility == Visibility.Visible;
            if (vis != _vScrollVisible)
            {
                _vScrollVisible = vis;
                RepositionAnnotationBars();
            }

            if (_viewMode != ViewMode.Continuous || _continuousTops.Count == 0) return;

            double viewportCenter = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight * 0.5)
                                    / Math.Max(0.01, _zoomLevel);
            int nearest = 0;
            double minDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count; i++)
            {
                if (i >= _continuousPanel.Children.Count) break;
                var slot = (FrameworkElement)_continuousPanel.Children[i];
                double center = _continuousTops[i] + slot.Height * 0.5;
                double dist   = Math.Abs(center - viewportCenter);
                if (dist < minDist) { minDist = dist; nearest = i; }
            }

            if (PageList.SelectedIndex != nearest)
            {
                _pageJumpBox.Text = (nearest + 1).ToString();
                // Update sidebar thumbnail without triggering a full page render
                PageList.SelectionChanged -= PageList_SelectionChanged;
                PageList.SelectedIndex = nearest;
                PageList.SelectionChanged += PageList_SelectionChanged;
            }
        }

        // Common overlay wiring shared by the continuous and secondary-tile builders: the move/up
        // gesture handlers, the shared right-click context menu (per-page overlays don't inherit the
        // primary's ContextMenu), and registration in both page maps. The mouse-DOWN handler and the
        // overlay's size/layout are caller-specific, so those stay in the callers.
        private void WirePageOverlay(Canvas overlay, int page)
        {
            overlay.MouseMove                += Canvas_MouseMove;
            overlay.MouseLeave               += Canvas_MouseLeave;
            overlay.PreviewMouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            overlay.PreviewMouseRightButtonUp += (s, ev) =>
            {
                if (_viewMode != ViewMode.TwoPage) PageList.SelectedIndex = page;
                if (_annotationCanvas.ContextMenu is ContextMenu cm)
                {
                    // Selection chrome draws on _activeCanvas, so point it at this tile before populating.
                    _activeCanvas = (Canvas)s;
                    PopulateContextMenu(ev.GetPosition((Canvas)s), page);
                    cm.PlacementTarget = (UIElement)s;
                    cm.IsOpen = true;
                    ev.Handled = true;
                }
            };
            _continuousCanvases[page] = overlay;
            _pages[page] = overlay;
        }

        // Builds a page's annotation overlay. Size/transform differ by mode (continuous = render-dim + scale;
        // grid/two-page = DIP 1:1); everything else - background, clip, tag, input handler - is identical.
        private Canvas BuildPageOverlay(int page, double width, double height, System.Windows.Media.Transform? layoutTransform)
        {
            var overlay = new Canvas
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent,
                ClipToBounds = true,
                Tag = page
            };
            if (layoutTransform != null) overlay.LayoutTransform = layoutTransform;
            overlay.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            WirePageOverlay(overlay, page);
            return overlay;
        }

        // Build tile-0 (the primary page) in code and insert it at the head of the page panel, replacing the
        // former hardcoded XAML PageImage + AnnotationCanvas singleton. Wiring mirrors the old XAML attributes
        // exactly: left-down/move/leave/left-up, plus the attached ContextMenu set later in BuildContextMenu.
        // It deliberately does NOT use WirePageOverlay - the primary must stay OUT of _continuousCanvases, and
        // RenderPage remains the sole registrar of _pages[primary], preserving ClearSecondaryPages' "keep the
        // index-0 tile" contract. Runs once from the constructor after _pageContentPanel is resolved.
        private void BuildPrimaryTile()
        {
            var img = new Image { Stretch = Stretch.None };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var overlay = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            overlay.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            overlay.MouseMove                  += Canvas_MouseMove;
            overlay.MouseLeave                 += Canvas_MouseLeave;
            overlay.PreviewMouseLeftButtonUp   += Canvas_MouseLeftButtonUp;

            var grid = new Grid();
            grid.Children.Add(img);
            grid.Children.Add(overlay);

            var tile = new Border
            {
                Background        = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(0, 0, 12, 12),
                Child             = grid,
            };
            _pageContentPanel.Children.Insert(0, tile);

            PageImage         = img;
            _annotationCanvas = overlay;
        }

        private void SetupContinuousView(int initialPage, bool fitDefault = true)
        {
            if (_doc is null) return;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _continuousCanvases.Clear();
            _pages.Clear();

            // Use the PDF's natural page width in WPF DIPs (96 DIP/inch, 72 pt/inch).
            // This is zoom-independent, which is critical: FitToWidth computes
            //   zoom = viewportW / _continuousPageW
            // and if _continuousPageW were derived from the current zoom level the two
            // would cancel and FitToWidth would always return approximately the old zoom.
            var refPage = _doc.Pages[0];
            _continuousPageW = Math.Max(200.0, refPage.Width.Point * (96.0 / 72.0));

            double y = 0;
            for (int i = 0; i < _doc.PageCount; i++)
            {
                _continuousTops.Add(y);
                var pdfPage = _doc.Pages[i];
                double pw = pdfPage.Width.Point, ph = pdfPage.Height.Point;
                if (_pageRotations.TryGetValue(i, out int prot) && (prot == 90 || prot == 270))
                    (pw, ph) = (ph, pw);
                // Scaffold: reuse this tab's cached render dimensions (from a prior render of this page) so
                // the frame is built at its REAL size up front. On a tab switch the page slots are already the
                // right shape - no dark estimate-sized box that resizes when the bitmap finally streams in.
                // Fall back to the page-box estimate only the first time a page is laid out. Both are the same
                // canonical render-dim space (longest side -> 2048), so annotation coordinates stay identical.
                int rdW, rdH;
                if (_renderDims.TryGetValue(i, out var cachedDims) && cachedDims.w > 0 && cachedDims.h > 0)
                {
                    rdW = cachedDims.w;
                    rdH = cachedDims.h;
                }
                else
                {
                    double maxDim = Math.Max(pw, ph);
                    rdW = Math.Max(1, (int)Math.Round(2048.0 * pw / maxDim));
                    rdH = Math.Max(1, (int)Math.Round(2048.0 * ph / maxDim));
                    _renderDims[i] = (rdW, rdH);
                }
                double slotH = _continuousPageW * rdH / (double)rdW;
                double slotScale = _continuousPageW / rdW;
                var overlay = BuildPageOverlay(i, rdW, rdH, new System.Windows.Media.ScaleTransform(slotScale, slotScale));

                var pageImg = new Image { Stretch = Stretch.None, Width = _continuousPageW, Height = slotH };
                RenderOptions.SetBitmapScalingMode(pageImg, BitmapScalingMode.HighQuality);

                var slotGrid = new Grid();
                slotGrid.Children.Add(pageImg);
                slotGrid.Children.Add(overlay);

                var placeholder = new Border
                {
                    Width      = _continuousPageW,
                    Height     = slotH,
                    Margin     = new Thickness(0, 0, 0, 12),
                    Background = Brushes.White,   // empty-page scaffold while the bitmap streams in (not a dark box)
                    Tag = i,
                    Child = slotGrid
                };
                int capturedI = i;
                placeholder.PreviewMouseLeftButtonDown += (_, _) => PageList.SelectedIndex = capturedI;
                _continuousPanel.Children.Add(placeholder);
                y += slotH + 12;
            }

            // Paint existing annotations onto the freshly built per-page overlays so they show
            // immediately. Without this they stayed invisible until the next tool/page change
            // happened to trigger a render for that page.
            foreach (var annotPage in _annotations.Keys.ToList())
                if (_continuousCanvases.ContainsKey(annotPage))
                    RenderAllAnnotations(annotPage);

            // Re-apply the view's zoom now that _continuousPageW is known. Honor the saved fit mode; for a
            // custom (None) zoom, keep the exact level on a tab restore (fitDefault=false) instead of snapping
            // to fit-page - otherwise switching tabs in continuous mode loses the user's zoom. Fresh opens and
            // view-mode switches pass fitDefault=true and still default to fit-page.
            if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
            else if (fitDefault) FitToPage();
            else SetZoom(_zoomLevel);

            _continuousScrollTarget = initialPage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScrollContinuousToPage(initialPage));

            _ = RenderContinuousPages();
        }

        private async System.Threading.Tasks.Task RenderContinuousPages()
        {
            if (_doc is null || _currentFile is null) return;
            _continuousRenderCts?.Cancel();
            _continuousRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _continuousRenderCts;

            string currentFile = _currentFile;
            int pageCount      = _doc.PageCount;
            double targetW     = _continuousPageW;
            int renderW        = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));

            // Capture per-page rotations on the UI thread before going async
            var rotations = new Dictionary<int, int>(_pageRotations);

            var session = _active;
            await System.Threading.Tasks.Task.Run(() =>
            {
                Docnet.Core.Readers.IDocReader? docReader = null;
                try
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (cts.IsCancellationRequested) return;
                        int rot = rotations.TryGetValue(i, out int rr) ? rr : 0;

                        // Cache hit: skip pdfium, just attach the cached bitmap to its slot.
                        var cb = TryGetCachedRender(session, i, renderW, rot);
                        if (cb != null)
                        {
                            int fic = i;
                            Dispatcher.Invoke(() =>
                            {
                                if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                                SetContinuousSlot(fic, cb);
                            });
                            continue;
                        }

                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(renderW, renderW * 2));
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        var raw = pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        if (rot != 0)
                            (raw, w, h) = RotateBitmap(raw, w, h, rot);

                        int fi = i, fw = w, fh = h, prot = rot;
                        byte[] bytes = raw;
                        if (cts.IsCancellationRequested) return;
                        // Use the window's own dispatcher, not Application.Current.Dispatcher: during app
                        // shutdown Application.Current goes null and this background render would NRE.
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            if (fi >= _continuousPanel.Children.Count) return;
                            // Render at the natural continuous width (zoom is a LayoutTransform, so the bitmap
                            // is zoom-independent and reusable). Square pixels via the matching dipH.
                            int ddw = Math.Max(1, (int)Math.Round(targetW));
                            int ddh = Math.Max(1, (int)Math.Round(targetW * fh / fw));
                            var bmp = BuildScaledBitmap(fw, fh, bytes, ddw, ddh);
                            CacheRender(session, fi, renderW, prot, bmp);
                            SetContinuousSlot(fi, bmp);
                        });
                    }
                }
                catch { /* render cancelled or doc closed */ }
                finally { docReader?.Dispose(); }
            }, cts.Token);
        }

        // Attaches a (cached or freshly built) page bitmap to its continuous slot and finalizes the slot /
        // overlay sizes + scroll offsets. UI thread. Shared by the cache-hit and freshly-rendered paths.
        private void SetContinuousSlot(int fi, System.Windows.Media.Imaging.BitmapSource bmp)
        {
            if (fi < 0 || fi >= _continuousPanel.Children.Count) return;
            var slot = (Border)_continuousPanel.Children[fi];
            int fw = bmp.PixelWidth, fh = bmp.PixelHeight;
            double dipW = slot.Width;
            double dipH = dipW * fh / fw;
            if (slot.Child is not Grid slotGrid || slotGrid.Children.Count == 0 || slotGrid.Children[0] is not Image pageImg)
                return;

            pageImg.Source  = bmp;
            pageImg.Width   = dipW;
            pageImg.Height  = dipH;
            slot.Background = Brushes.White;

            // Size the slot and overlay from the ACTUAL rendered page so a cropped page (which renders
            // shorter than its MediaBox estimate) fills its slot with no white bars.
            slot.Height = dipH;
            double maxF = Math.Max(fw, fh);
            int rdW = Math.Max(1, (int)Math.Round(2048.0 * fw / maxF));
            int rdH = Math.Max(1, (int)Math.Round(2048.0 * fh / maxF));
            _renderDims[fi] = (rdW, rdH);
            if (slotGrid.Children.Count > 1 && slotGrid.Children[1] is Canvas ov)
            {
                ov.Width  = rdW;
                ov.Height = rdH;
                ov.LayoutTransform = new System.Windows.Media.ScaleTransform(dipW / rdW, dipW / rdW);
            }

            // Slot heights are now exact; recompute scroll offsets from them.
            double yy = 0;
            for (int k = 0; k < _continuousPanel.Children.Count && k < _continuousTops.Count; k++)
            {
                _continuousTops[k] = yy;
                double hk = ((FrameworkElement)_continuousPanel.Children[k]).Height;
                if (double.IsNaN(hk)) hk = 0;
                yy += hk + 12;
            }

            // Pages render in order, so when the target page is reached every page above it has its final
            // height; re-scroll so a crop lands you back on the same page instead of drifting.
            if (_continuousScrollTarget >= 0 && fi >= _continuousScrollTarget)
            {
                int tgt = _continuousScrollTarget;
                _continuousScrollTarget = -1;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => ScrollContinuousToPage(tgt)));
            }

            RenderAllAnnotations(fi);
        }

        private void RenderPage(int pageIndex)
        {
            if (_currentFile is null || _doc is null) return;
            // Continuous has its own pipeline (SetupContinuousView + RenderContinuousPages into
            // _continuousPanel) and owns the _pages map for every page. RenderPage targets the hidden
            // single/grid primary (_annotationCanvas in the collapsed _pageContentPanel) and calls
            // ClearSecondaryPages, which would WIPE the continuous _pages map and repoint the current
            // page at the invisible primary - so any annotation added afterwards renders off-screen
            // until a mode switch rebuilds the overlays. Stray callers (the zoom re-sharpen timer, the
            // DPI-change handler) can fire RenderPage while continuous is active; ignore them. The mode
            // switch sets _viewMode to the new (non-continuous) mode BEFORE calling RenderPage, so this
            // guard never blocks a legitimate switch-into-single/grid render.
            if (_viewMode == ViewMode.Continuous) return;
            // Two-page spreads pair (0,1),(2,3),...; render the pair's left (even) page as primary so
            // selecting the right page of a pair still shows the whole spread, not a lone page.
            if (_viewMode == ViewMode.TwoPage) pageIndex -= pageIndex % 2;
            try
            {
                // Scale render resolution to match display DPI AND current zoom so the
                // bitmap stays sharp when zoomed in.  Base 2048 means Fit Width on a
                // wide monitor stays crisp; zoom factor ensures 1:1 pixels at 2× zoom.
                // Capped at 6144 to keep memory manageable.
                var dpiInfo = VisualTreeHelper.GetDpi(this);
                double dpiScaleX = dpiInfo.DpiScaleX;
                double dpiScaleY = dpiInfo.DpiScaleY;
                int scaledMax = (int)Math.Min(6144,
                    2048 * Math.Max(dpiScaleX, dpiScaleY) * Math.Max(1.0, _zoomLevel));
                _lastRenderZoom = _zoomLevel;

                int pgRot = _pageRotations.TryGetValue(pageIndex, out int pr0) ? pr0 : 0;

                // Reuse this tab's cached bitmap for (page, resolution, rotation) if present; otherwise
                // rasterize once and cache it. On a switch back to a recent tab this skips pdfium entirely.
                int width, height;
                System.Windows.Media.Imaging.BitmapSource bitmap;
                var cached = TryGetCachedRender(_active, pageIndex, scaledMax, pgRot);
                if (cached != null)
                {
                    bitmap = cached;
                    width  = cached.PixelWidth;
                    height = cached.PixelHeight;
                }
                else
                {
                    using var docReader = DocLib.Instance.GetDocReader(_currentFile, new PageDimensions(scaledMax, scaledMax));
                    using var pageReader = docReader.GetPageReader(pageIndex);
                    width  = pageReader.GetPageWidth();
                    height = pageReader.GetPageHeight();
                    var rawBytes = pageReader.GetImage();
                    // The temp file has /Rotate stripped so Docnet renders unrotated (no clipping); rotate
                    // the pixel buffer to match the visual.
                    if (pgRot != 0)
                        (rawBytes, width, height) = RotateBitmap(rawBytes, width, height, pgRot);
                    if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                    {
                        PageImage.Source = null;
                        SetStatus(string.Format(Loc("Str_PageRenderError"), pageIndex + 1));
                        return;
                    }
                    // Bake the bitmap DPI from the canonical render-dim size (longest side -> 2048 DIP, the
                    // SAME zoom-independent basis continuous and the secondary tiles use) so the extra pixels
                    // display within a fixed DIP area regardless of zoom. LayoutTransform supplies the zoom.
                    double bLongest = Math.Max(1, Math.Max(width, height));
                    int bw = Math.Max(1, (int)Math.Round(2048.0 * width  / bLongest));
                    int bh = Math.Max(1, (int)Math.Round(2048.0 * height / bLongest));
                    var wb = new WriteableBitmap(width, height, 96.0 * width / bw, 96.0 * height / bh, PixelFormats.Bgra32, null);
                    wb.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);
                    wb.Freeze();
                    bitmap = wb;
                    CacheRender(_active, pageIndex, scaledMax, pgRot, bitmap);
                }

                // Canonical render-dim space: longest side -> 2048 DIP, identical to the continuous and
                // secondary-tile paths. This is zoom-independent (aspect only), so the canvas/overlay size
                // is byte-for-byte stable across every zoom re-render - no left-shift when re-sharpening,
                // and annotation coordinates match across all view modes. LayoutTransform handles the zoom.
                double longest = Math.Max(1, Math.Max(width, height));
                int dipW = Math.Max(1, (int)Math.Round(2048.0 * width  / longest));
                int dipH = Math.Max(1, (int)Math.Round(2048.0 * height / longest));
                _renderDims[pageIndex] = (dipW, dipH);

                PageImage.Source = bitmap;
                _annotationCanvas.Width  = dipW;
                _annotationCanvas.Height = dipH;
                _annotationCanvas.Tag    = pageIndex;   // so clicks on the primary page resolve to the
                                                        // page actually shown (page 0 in grid), not the
                                                        // selected index - otherwise annotations on it
                                                        // are unhittable and clicks "do nothing".
                ClearSelection();
                ClearSecondaryPages();
                _pages[pageIndex] = _annotationCanvas;   // the primary is a normal entry in the unified map
                RenderAllAnnotations(pageIndex);
                SetStatus(string.Format(Loc("Str_PageOf"), pageIndex + 1, _doc!.PageCount));
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    // Only Grid/Two-Page lay out neighbour tiles. In Single mode RenderAdditionalPages would
                    // snap the panel width to pageW + 12 (the inter-tile gap), nudging the lone page ~6px left
                    // of centre - the shift seen a beat after a zoom-in, when the re-sharpen timer re-renders.
                    // Match RefreshPageView's single-mode handling instead: no extra tiles, auto panel width.
                    if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                        RenderAdditionalPages(pageIndex);
                    else
                    {
                        ClearSecondaryPages();
                        if (_pageContentPanel is not null) _pageContentPanel.Width = double.NaN;
                    }
                    RenderPageLinks(pageIndex, dipW, dipH);
                });
                _renderedPrimaryPage = pageIndex;
            }
            catch (Exception ex)
            {
                PageImage.Source = null;
                SetStatus(string.Format(Loc("Str_RenderError"), ex.Message));
            }
        }

        /// <summary>
        /// Clears all dynamically-added secondary page borders from the panel,
        /// leaving only the first child (the primary page border).
        /// </summary>
        // Removes secondary tiles whose page is no longer shown (keeps the primary at index 0 and any
        // tile still in range so it can be reused in place). Keeps the tile map in sync.
        private void RemoveSecondaryTilesNotIn(HashSet<int> keep)
        {
            if (_pageContentPanel is null) return;
            var stale = new List<int>();
            foreach (var k in _continuousCanvases.Keys)
                if (!keep.Contains(k)) stale.Add(k);
            foreach (var pg in stale)
            {
                if (_continuousCanvases.TryGetValue(pg, out var ov) && ov.Parent is Grid g && g.Parent is Border tile)
                {
                    foreach (var gc in g.Children) if (gc is Image im) im.Source = null;
                    _pageContentPanel.Children.Remove(tile);
                }
                _continuousCanvases.Remove(pg);
                _pages.Remove(pg);
            }
        }

        private void ClearSecondaryPages()
        {
            if (_pageContentPanel is null) return;
            // Explicitly null out Image sources before removing so the GC can
            // reclaim the WriteableBitmap backing arrays promptly.
            while (_pageContentPanel.Children.Count > 1)
            {
                var child = _pageContentPanel.Children[^1];
                if (child is Border b && b.Child is Grid g)
                {
                    foreach (var gc in g.Children)
                        if (gc is Image img) img.Source = null;
                }
                _pageContentPanel.Children.RemoveAt(_pageContentPanel.Children.Count - 1);
            }
            // NOTE: do NOT reset _pageContentPanel.Width here.  Width is managed exclusively
            // by RenderAdditionalPages (which runs only via Dispatcher) so that no synchronous
            // call to ClearSecondaryPages triggers an intermediate layout pass that would cause
            // the primary page to flash centered and then jerk back to left-aligned.
            // Clear any link overlays from the annotation canvas.
            foreach (var lo in _linkOverlays)
                _annotationCanvas.Children.Remove(lo);
            _linkOverlays.Clear();
            _continuousCanvases.Clear();   // keep the page->tile map in sync with the visible tiles
            // Unified map: keep only the CURRENT primary entry (key == _annotationCanvas.Tag) and drop
            // everything else - the secondary overlays and any stale primary entry from a prior page.
            int primPage = _annotationCanvas.Tag is int tp ? tp : -1;
            foreach (var pg in _pages.Keys.Where(k => k != primPage).ToList())
                _pages.Remove(pg);
        }

        /// <summary>
        /// Renders secondary pages as a grid. Panel-width setup is synchronous so layout
        /// is correct immediately; Docnet pixel rendering runs on a background thread so
        /// the UI stays responsive. WPF element creation returns to the UI thread.
        /// </summary>
        private async void RenderAdditionalPages(int primaryPageIdx)
        {
            if (_currentFile is null || _doc is null) return;
            // Grid is a stable overview anchored at page 0 (independent of the selected page), so it
            // always shows the whole document instead of only the selected page onward.
            if (_viewMode == ViewMode.Grid) primaryPageIdx = 0;

            double viewportW = PagePreviewPanel.ActualWidth;
            if (viewportW <= 0 || _doc.PageCount <= 1)
            {
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                return;
            }

            // Snap the WrapPanel width to a whole number of page-width slots.
            double primaryPageW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;
            double pageSlotW = primaryPageW + 12;
            double availablePreZoom = (viewportW - 24) / _zoomLevel;
            // +1e-6: same floating-point underflow guard as GridZoomStep, so a zoom set for n columns
            // actually lays out n (not n-1) when the division lands a hair under the integer.
            // Grid lays out its AUTHORITATIVE column count (set on grid zoom, restored per tab); it no longer
            // derives columns from the zoom here - that zoom->columns->zoom round-trip lost the grid zoom on
            // tab switches. Other modes still fit to the current zoom.
            int pagesPerRow = _viewMode == ViewMode.TwoPage ? 2
                            : _viewMode == ViewMode.Grid    ? Math.Max(1, _gridColumns)
                            : Math.Max(1, (int)(availablePreZoom / pageSlotW + 1e-6));
            double panelW = pagesPerRow * pageSlotW;
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // Cancel any previously running secondary render.
            _secondaryRenderCts?.Cancel();
            _secondaryRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _secondaryRenderCts;

            // Secondary pages: 1536 px base, scaled up for high-DPI displays so grid / two-page text
            // stays crisp on 150%/200% screens (capped at 3072 to keep memory in check). Stays 1536
            // at 100% DPI, so standard displays are unaffected.
            int SecondaryMax = (int)Math.Min(3072, 1536 * Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX));
            // Grid shows the whole document; Two-Page shows one secondary; other modes peek ahead.
            int limit = _viewMode == ViewMode.Grid
                ? _doc.PageCount
                : Math.Min(_doc.PageCount, primaryPageIdx + 1 + (_viewMode == ViewMode.TwoPage ? 1 : 25));
            if (limit <= primaryPageIdx + 1) { ClearSecondaryPages(); return; }

            // Per-tile reuse: drop tiles for pages that left the view, keep the rest. Pages that already
            // have a tile get their bitmap swapped in place (AddSecondaryTile); only genuinely new pages
            // are built. Stays smooth even mid-stream on a large doc, where the tile set is only partly
            // built. (Navigation clears everything via RenderPage first, so it rebuilds.)
            var keepPages = new HashSet<int>();
            for (int i = primaryPageIdx + 1; i < limit; i++) keepPages.Add(i);
            RemoveSecondaryTilesNotIn(keepPages);

            string currentFile = _currentFile;

            // Collect rotations on the UI thread before the background task.
            var secRotations = new Dictionary<int, int>();
            for (int i = primaryPageIdx + 1; i < limit; i++)
                if (_pageRotations.TryGetValue(i, out int r) && r != 0)
                    secRotations[i] = r;

            // Capture the primary page width and reset the tile map on the UI thread before
            // streaming tiles in from the background render.
            double primaryDipW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;

            // Render pixels on a background thread and attach each page tile to the UI as soon
            // as it is ready, so large documents fill in progressively instead of blocking
            // until every page has been rendered.
            try
            {
                var session = _active;
                int tileBucket = (int)Math.Round(primaryDipW);   // tiles are sized to the primary width; key the cache by it
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Docnet.Core.Readers.IDocReader? docReader = null;
                    try
                    {
                        for (int i = primaryPageIdx + 1; i < limit; i++)
                        {
                            if (cts.IsCancellationRequested) break;
                            int rot = secRotations.TryGetValue(i, out int rr) ? rr : 0;

                            // Cache hit: skip pdfium entirely, just attach the cached tile bitmap.
                            var cachedTile = TryGetCachedRender(session, i, tileBucket, rot);
                            if (cachedTile != null)
                            {
                                int pic = i;
                                try
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (cts.IsCancellationRequested || _doc is null) return;
                                        if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                                        AddSecondaryTile(pic, cachedTile, primaryDipW);
                                    });
                                }
                                catch (System.Threading.Tasks.TaskCanceledException) { break; }
                                catch (OperationCanceledException) { break; }
                                continue;
                            }

                            docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(SecondaryMax, SecondaryMax));
                            using var pageReader = docReader.GetPageReader(i);
                            int w = pageReader.GetPageWidth();
                            int h = pageReader.GetPageHeight();
                            var rawBytes = pageReader.GetImage();
                            if (w <= 0 || h <= 0 || rawBytes is null) continue;
                            if (rot != 0)
                                (rawBytes, w, h) = RotateBitmap(rawBytes, w, h, rot);

                            int pi = i, pw = w, ph = h, prot = rot;
                            byte[] bytes = rawBytes;
                            try
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (cts.IsCancellationRequested || _doc is null) return;
                                    if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                                    int ddw = Math.Max(1, (int)Math.Round(primaryDipW));
                                    int ddh = Math.Max(1, (int)Math.Round(primaryDipW * ph / pw));
                                    var bmp = BuildScaledBitmap(pw, ph, bytes, ddw, ddh);
                                    CacheRender(session, pi, tileBucket, prot, bmp);
                                    AddSecondaryTile(pi, bmp, primaryDipW);
                                });
                            }
                            // Dispatcher.Invoke throws when the dispatcher is shutting down (app closing) or
                            // the render was cancelled; stop rendering cleanly instead of crashing.
                            catch (System.Threading.Tasks.TaskCanceledException) { break; }
                            catch (OperationCanceledException) { break; }
                        }
                    }
                    finally { docReader?.Dispose(); }
                }, cts.Token);
            }
            catch { return; }
        }

        /// <summary>
        /// Builds one secondary-page tile (image + annotation overlay + links) and appends it
        /// to the page content panel. Must run on the UI thread.
        /// </summary>
        // Builds a frozen bitmap sized so its baked DPI displays it at (dipW x dipH) DIPs. Shared by the
        // tile and the render cache so a cached tile bitmap reuses the exact same geometry.
        private static System.Windows.Media.Imaging.BitmapSource BuildScaledBitmap(int w, int h, byte[] rawBytes, int dipW, int dipH)
        {
            var wb = new WriteableBitmap(w, h, 96.0 * w / Math.Max(1, dipW), 96.0 * h / Math.Max(1, dipH), PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);
            wb.Freeze();
            return wb;
        }

        private void AddSecondaryTile(int pi, System.Windows.Media.Imaging.BitmapSource bitmap, double primaryDipW)
        {
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            int pageDipW = (int)Math.Round(primaryDipW);
            int pageDipH = (int)Math.Round(primaryDipW * h / w);

            // This page already has a tile: swap just the bitmap (same logical size, crisper pixels).
            // No clear, no reflow - so the grid/spread never jumps or blinks.
            if (_continuousCanvases.TryGetValue(pi, out var exOverlay)
                && exOverlay.Parent is Grid exGrid && exGrid.Children.Count > 0 && exGrid.Children[0] is Image exImg)
            {
                exImg.Source = bitmap;
                return;
            }

            // Do NOT overwrite _renderDims if the page was already rendered as primary -
            // its annotation coordinate mapping must stay intact.
            if (!_renderDims.ContainsKey(pi))
                _renderDims[pi] = (pageDipW, pageDipH);

            var img = new Image { Source = bitmap, Stretch = Stretch.None };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var overlay = BuildPageOverlay(pi, pageDipW, pageDipH, null);
            overlay.Cursor = CursorForTool(_currentTool);
            overlay.ToolTip = $"Page {pi + 1}";

            var pageGrid = new Grid();
            pageGrid.Children.Add(img);
            pageGrid.Children.Add(overlay);
            AddSecondaryPageLinks(pi, pageGrid, pageDipW, pageDipH);

            var tile = new Border
            {
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 12),
                Child = pageGrid
            };
            _pageContentPanel.Children.Add(tile);
            RenderAllAnnotations(pi);

            // Grid tiles render asynchronously, so a "scroll to page N" requested when entering grid
            // can't run until page N's tile exists. Do it the moment that tile streams in.
            if (pi == _gridScrollToPage)
            {
                _gridScrollToPage = -1;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() =>
                    {
                        if (_viewMode != ViewMode.Grid) return;
                        try
                        {
                            // Top-align the page's row in the viewport (accounts for the zoom transform).
                            if (PagePreviewPanel.Content is FrameworkElement content)
                                PagePreviewPanel.ScrollToVerticalOffset(
                                    tile.TransformToVisual(content).Transform(new Point(0, 0)).Y);
                            else
                                tile.BringIntoView();
                        }
                        catch { tile.BringIntoView(); }
                    }));
            }
        }

        private void BootstrapDocumentView(int initialPage, bool autoFit, bool restoreFitMode = false)
        {
            // The document is (re)displaying - usually a different one (tab switch/close/open). The
            // skip-render guard in PageList_SelectionChanged compares the target page to the last
            // rasterised page (_renderedPrimaryPage) but not to WHICH document, so a switch to another
            // doc at the same page index + zoom would skip the render and leave the previous doc on
            // screen. Invalidate it here so the new document always renders.
            _renderedPrimaryPage = -1;
            ClearSecondaryPages();
            ClearSelection();
            RefreshPageList();
            LoadOutlines();
            DropZone.Visibility = Visibility.Collapsed;
            PagePreviewPanel.Visibility = Visibility.Visible;
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
            _pageJumpBox.IsEnabled = true;
            _pageTotalLabel.Text = $"/ {_doc!.PageCount}";
            if (_doc!.PageCount > 0)
            {
                int page = Math.Max(0, Math.Min(initialPage, _doc.PageCount - 1));
                // Show the panel that matches THIS tab's view mode. This must run for every mode, not
                // only Continuous: switching from a Continuous tab to a Single/Two-Page/Grid tab has to
                // collapse the continuous panel, otherwise the previous tab's continuous render stays on
                // screen over the new document.
                bool isContinuous = _viewMode == ViewMode.Continuous;
                _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
                _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;
                PageList.SelectedIndex = page;
                // Continuous's SelectionChanged returns early (no RenderPage call), so build its panel here.
                if (isContinuous)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () => SetupContinuousView(page, fitDefault: autoFit));
                // Fit / zoom once the first page has rendered and layout has settled.
                // DispatcherPriority.Background is lower than Loaded, so this fires after
                // all pending RenderPage / RefreshPageView callbacks have completed.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() =>
                    {
                        if (autoFit)
                        {
                            // Grid opens to its 3-across default; other modes fit to width.
                            if (_viewMode == ViewMode.Grid)
                            {
                                _gridColumns = Math.Min(_doc?.PageCount ?? 1, 3);
                                SetZoom(GridZoomForN(_gridColumns));
                            }
                            else
                                FitToPage();
                        }
                        else if (restoreFitMode)
                        {
                            // Reopened document: re-fit to the current window if it was in a fit mode,
                            // else apply its exact saved zoom. (Grid's zoom encodes its column count.)
                            if (_viewMode == ViewMode.Grid)       SetZoom(_zoomLevel);
                            else if (_fitMode == FitMode.Width)   FitToWidth();
                            else if (_fitMode == FitMode.Page)    FitToPage();
                            else                                  SetZoom(_zoomLevel);
                        }
                        else
                        {
                            // Tab restore: keep the document's saved zoom. Grid's zoom is really "how many
                            // columns" for the CURRENT window width, and its SizeChanged/settle handlers
                            // recompute it as GridZoomForN(_gridColumns); replay the saved column count the
                            // same way so the two agree instead of fighting (a raw saved zoom from a different
                            // width loses). _gridColumns is restored per tab in ApplySessionState.
                            if (_viewMode == ViewMode.Grid) SetZoom(GridZoomForN(_gridColumns));
                            else SetZoom(_zoomLevel);
                        }
                    }));
            }
        }

        private void RefreshPageView(int pageIndex)
        {
            if (_viewMode == ViewMode.Continuous)
                return; // continuous mode manages its own rendering
            if (_viewMode == ViewMode.TwoPage) pageIndex -= pageIndex % 2;   // snap to the spread's left page

            // Grid fits its columns to the viewport, so it never needs a horizontal scrollbar.
            // Leaving it on Auto shows a stray (green) thumb across the bottom when the tile panel
            // overflows by the vertical scrollbar's width. Disable it for grid, Auto elsewhere.
            PagePreviewPanel.HorizontalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            // Reserve the vertical scrollbar in Grid so its appearing/disappearing can't change the
            // viewport width mid-resize and feed a width change back into the layout (the loop the grid
            // used to guard against). A stable width lets the column-holding resize stay stable too.
            PagePreviewPanel.VerticalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto;
            // Single page is centered; drop the right/bottom tile-gap margin that grid/two-page
            // need for spacing (it would otherwise push the lone page a few px left of center).
            if (_pageContentPanel is not null && _pageContentPanel.Children.Count > 0
                && _pageContentPanel.Children[0] is Border primaryBorder)
                primaryBorder.Margin = _viewMode == ViewMode.Single
                    ? new Thickness(0) : new Thickness(0, 0, 12, 12);
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                RenderAdditionalPages(pageIndex);
            else
            {
                ClearSecondaryPages();
                if (_pageContentPanel is not null)
                    _pageContentPanel.Width = double.NaN;
            }
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderPageLinks(pageIndex, dims.w, dims.h);
        }

        private void ApplyZoom(bool lite = false)
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = _zoomLevel;
                st.ScaleY = _zoomLevel;
            }
            SyncZoomBox();   // keep the toolbar box in step (FitToWidth/FitToPage don't call SetZoom)
            // Live-resize path: the ScaleTransform above already grew/shrank the existing render to
            // match the new size - smooth and flicker-free. Skip the bitmap re-render and tile rebuild;
            // PagePreviewPanel_SizeChanged debounces one crisp re-render once the drag settles, instead
            // of thrashing it on every size tick (which is what made the page blink during a resize).
            if (lite) return;
            // Recalculate how many pages fit after zoom changes.
            // Use RefreshPageView so link overlays are re-added after RenderAdditionalPages
            // calls ClearSecondaryPages (which wipes them).
            int applyIdx = PageList.SelectedIndex;
            if (applyIdx >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(applyIdx));

            // If the user has zoomed in past ~10% of the last render, queue a deferred re-render at
            // higher resolution so text re-sharpens quickly (especially on high-DPI displays, where
            // the upscaled bitmap shows blur sooner). The timer debounces rapid Ctrl+scroll.
            // Skipped in Grid: this re-renders via the selected page (not page 0) and, once the render
            // hits its pixel cap when zoomed in, shifts page 0's render width - which is the basis for
            // the grid's column math. That desync locks Ctrl+scroll to a 1<->2 column toggle. The grid
            // is an overview and doesn't need the re-sharpen.
            if (applyIdx >= 0 && _zoomLevel > _lastRenderZoom * 1.10 && _doc is not null
                && _viewMode != ViewMode.Grid)
            {
                if (_rerenderTimer is null)
                {
                    _rerenderTimer = new System.Windows.Threading.DispatcherTimer
                        { Interval = TimeSpan.FromMilliseconds(250) };
                    _rerenderTimer.Tick += (_, _) =>
                    {
                        _rerenderTimer!.Stop();
                        // Never re-render the primary in Grid (it would shift page 0's width basis and
                        // desync the column math); guards a timer started just before a switch into grid.
                        if (_doc is not null && _viewMode != ViewMode.Grid && PageList.SelectedIndex >= 0)
                            RenderPage(PageList.SelectedIndex);
                    };
                }
                _rerenderTimer.Stop();
                _rerenderTimer.Start();
            }
        }

        private void ResetZoom() => SetZoom(1.0);

        // Grid zoom snaps to "fit N pages across the viewport", so zooming steps through clean
        // columns (1, 2, 3, ... per row) instead of arbitrary percentages. N rises as you zoom out
        // and keeps going for larger documents until the page size hits the zoom floor.
        private double GridZoomForN(int n)
        {
            if (n < 1) n = 1;
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            double vw  = PagePreviewPanel.ActualWidth;   // SAME width + slot the layout uses
            if (vw <= 0 || rdW <= 0) return _zoomLevel;
            // RenderAdditionalPages lays out pages in slots of (rdW + 12) within (ActualWidth - 24);
            // invert that so "fit n" produces exactly n columns with no gap.
            return (vw - 24.0) / (n * (rdW + 12.0));
        }

        private void GridZoomStep(bool zoomOut)
        {
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            double vw  = PagePreviewPanel.ActualWidth;
            if (vw <= 0 || rdW <= 0) { SetZoom(zoomOut ? _zoomLevel - ZoomStep : _zoomLevel + ZoomStep); return; }
            // _gridColumns is the authoritative current column count (set on every grid zoom and restored
            // per tab); step from it rather than re-deriving it from the zoom + geometry.
            int curN = Math.Max(1, _gridColumns);
            int newN = Math.Max(1, zoomOut ? curN + 1 : curN - 1);
            // If the column count is already at the limit the clamped zoom is unchanged, so
            // skip the re-render entirely - otherwise every Ctrl+Scroll reloads all tiles
            // without changing anything.
            double target = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(newN)));
            if (Math.Abs(target - _zoomLevel) < 1e-4) return;
            _gridColumns = newN;
            SetZoom(target);   // already clamped to [ZoomMin, ZoomMax]
        }

        /// <summary>
        /// Central zoom-change entry point for buttons, keyboard shortcuts, and the dropdown.
        /// Clamps to [ZoomMin, ZoomMax], applies the scale, syncs the combo box, and updates
        /// the status bar. Does NOT apply a fit mode — call FitToWidth / FitToPage for that.
        /// </summary>
        // The internal _zoomLevel scales each page's layout box. In Continuous mode that box is
        // the page's natural DIP width, so _zoomLevel already reads as true zoom (1.0 = 100%).
        // In Single/Two-Page/Grid the box is the render-dimension bitmap (~2x natural width), so
        // the raw _zoomLevel reads about half the real size. DisplayZoomFactor converts to true
        // zoom for everything shown to (or typed by) the user; the internal value is unchanged.
        private double DisplayZoomFactor()
        {
            if (_viewMode == ViewMode.Continuous || _doc is null) return 1.0;
            int idx = _viewMode == ViewMode.Grid ? 0 : Math.Max(0, PageList.SelectedIndex);
            if (idx < 0 || idx >= _doc.PageCount) return 1.0;
            if (!_renderDims.TryGetValue(idx, out var d) || d.w <= 0) return 1.0;
            double wpt = _doc.Pages[idx].Width.Point, hpt = _doc.Pages[idx].Height.Point;
            if (_pageRotations.TryGetValue(idx, out int r) && (r == 90 || r == 270)) wpt = hpt;
            double naturalW = wpt * 96.0 / 72.0;
            if (naturalW <= 0) return 1.0;
            return d.w / naturalW;
        }
        private double DisplayZoomPct() => _zoomLevel * DisplayZoomFactor() * 100.0;

        private void SetZoom(double level)
        {
            _fitMode   = FitMode.None;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            ApplyZoom();
            SyncZoomBox();
            if (_doc != null && PageList.SelectedIndex >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount) + $" - {DisplayZoomPct():F0}%");
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)  { if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_viewMode == ViewMode.Grid) GridZoomStep(true);  else SetZoom(_zoomLevel - ZoomStep); }

        private void SyncZoomBox()
        {
            if (_zoomBox is null) return;
            _zoomBox.SelectionChanged -= ZoomBox_SelectionChanged;

            // When a fit mode is active, show the "Fit Width"/"Fit Page" entry rather than a raw
            // percentage so the box matches the status bar.
            string? fitTag = _fitMode == FitMode.Width ? "fitwidth"
                           : _fitMode == FitMode.Page  ? "fitpage"
                           : null;
            if (fitTag != null)
            {
                foreach (ComboBoxItem item in _zoomBox.Items)
                {
                    if (item.Tag?.ToString() == fitTag)
                    {
                        _zoomBox.SelectedItem = item;
                        _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
                        return;
                    }
                }
            }

            string target = $"{DisplayZoomPct():F0}%";
            foreach (ComboBoxItem item in _zoomBox.Items)
            {
                if (item.Content?.ToString() == target)
                {
                    _zoomBox.SelectedItem = item;
                    _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
                    return;
                }
            }
            // No preset match - clear dropdown selection and show free-form percentage
            _zoomBox.SelectedItem = null;
            _zoomBox.Text = target;
            _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
        }

        private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_zoomBox?.SelectedItem is not ComboBoxItem item) return;
            // Editable combos highlight the shown value after a pick (looks like selected text);
            // collapse that selection to just the caret once the value settles.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
            {
                if (_zoomBox.Template?.FindName("PART_EditableTextBox", _zoomBox) is TextBox etb)
                    etb.Select(etb.Text.Length, 0);
            }));
            string? tag = item.Tag?.ToString();
            if (tag is null) return;

            if (tag == "fitwidth") { FitToWidth(); return; }
            if (tag == "fitpage")  { FitToPage();  return; }

            if (double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double z))
            {
                _fitMode = FitMode.None;
                // Preset tags are true zoom (1.0 = 100%); convert to the internal render-dim scale.
                double zf = DisplayZoomFactor(); if (zf <= 0) zf = 1.0;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z / zf));
                ApplyZoom();
                if (PageList.SelectedIndex >= 0 && _doc != null)
                    SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount) + $" - {DisplayZoomPct():F0}%");
            }
        }

        // The selected page's DIP size for fit/zoom math (Single + Two-Page). Prefer _renderDims - it's set
        // synchronously in RenderPage so it always matches the current page and is zoom-stable (scaledMax
        // scales with zoom while RenderPage divides it back out, so the two cancel). Fall back to PageImage's
        // live layout size only when _renderDims has no entry yet, and to 1 to avoid divide-by-zero. Single
        // source so FitToWidth/FitToPage don't each re-derive it. (Continuous/Grid use their own page metrics.)
        private (double w, double h) GetPageDipSize(int idx)
        {
            if (idx >= 0 && _renderDims.TryGetValue(idx, out var d))
                return (d.w, d.h);
            return (PageImage.ActualWidth  > 0 ? PageImage.ActualWidth  : 1,
                    PageImage.ActualHeight > 0 ? PageImage.ActualHeight : 1);
        }

        private void FitToWidth(bool lite = false)
        {
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;

            // Continuous mode: pages are laid out at _continuousPageW (natural DIPs width)
            // and scaled by the ScaleTransform on PageContentGrid. PageImage is hidden, so
            // we cannot use its Source as a guard; use _continuousPageW directly instead.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0) return;
                _fitMode   = FitMode.Width;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, viewW / _continuousPageW));
                ApplyZoom(lite);
                int ci = PageList.SelectedIndex;
                if (ci >= 0 && _doc != null)
                    SetStatus(string.Format(Loc("Str_FitWidth"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            int idx = PageList.SelectedIndex;
            double dipW = GetPageDipSize(idx).w;
            if (dipW <= 0) return;
            // Two Page mode shows two pages side by side — each page gets roughly half
            // the viewport width (minus a small gap between pages).
            double slotW = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Width;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, slotW / dipW));
            ApplyZoom(lite);
            if (idx >= 0 && _doc != null)
                SetStatus(string.Format(Loc("Str_FitWidth"), idx + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
        }

        private void FitToPage(bool lite = false)
        {
            double viewW = PagePreviewPanel.ActualWidth  - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;

            // Continuous mode: derive the current page's natural height from its PDF aspect
            // ratio and _continuousPageW, then fit both axes.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0 || _doc is null) return;
                int ci = PageList.SelectedIndex;
                if (ci < 0) return;
                var pdfPage = _doc.Pages[ci];
                double ratio = Math.Max(0.1, pdfPage.Height.Point / Math.Max(1.0, pdfPage.Width.Point));
                double dipH  = _continuousPageW * ratio;
                _fitMode   = FitMode.Page;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                    Math.Min(viewW / _continuousPageW, viewH / dipH)));
                ApplyZoom(lite);
                SetStatus(string.Format(Loc("Str_FitPage"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            int idx = PageList.SelectedIndex;
            var (dipW, dipH2) = GetPageDipSize(idx);
            if (dipW <= 0 || dipH2 <= 0) return;
            double slotW2 = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Page;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                Math.Min(slotW2 / dipW, viewH / dipH2)));
            ApplyZoom(lite);
            SetStatus(string.Format(Loc("Str_FitPage"), idx + 1, _doc!.PageCount, $"{DisplayZoomPct():F0}"));
        }

        // Re-fit the main view after a reload. Grid keeps its column-fit (FitToWidth alone would
        // yank it out into a single-page Fit Width view); other modes honor the fit mode.
        private void ReapplyGridOrFit()
        {
            if (_viewMode == ViewMode.Grid)
            {
                double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                double vw  = PagePreviewPanel.ActualWidth;
                if (vw > 0 && rdW > 0)
                    SetZoom(GridZoomForN(Math.Max(1, _gridColumns)));   // authoritative column count
                else ApplyZoom();
                return;
            }
            if (_fitMode == FitMode.Page) FitToPage();
            else FitToWidth();
        }

        private void NavigatePageByWheel(int delta)
        {
            if (_doc is null) return;
            int cur = PageList.SelectedIndex;
            if (delta > 0 && cur > 0)
                PageList.SelectedIndex = cur - 1;
            else if (delta < 0 && cur < _doc.PageCount - 1)
                PageList.SelectedIndex = cur + 1;
        }

        private System.Windows.Threading.DispatcherTimer? _resizeRefitTimer;
        private int _gridColumns = 1;   // columns the grid is currently laid out in; held across resizes

        private void PagePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RepositionAnnotationBars();   // cheap; keep the draw/text bar tracking its anchored edge
            if (_cropPreviewRect is not null || _cropConfirmBar is not null) return;

            if (_viewMode == ViewMode.Grid)
            {
                // Grid columns depend only on width, so a height-only resize (e.g. dragging the bottom
                // edge) changes nothing - skip it so it doesn't needlessly re-render/blink.
                if (!e.WidthChanged) return;
                // Hold the column count through a non-modal resize: scale the already-laid-out tiles via
                // the transform so the same number of columns fills the new width (lite, no re-render).
                if (_doc is null || _gridColumns < 1) return;
                double rdWg = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                if (PagePreviewPanel.ActualWidth <= 0 || rdWg <= 0) return;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns)));
                ApplyZoom(lite: true);
                StartResizeSettleTimer();
                return;
            }

            // Non-modal resize (maximize/restore, splitter, programmatic): rescale lite + settle.
            if (_fitMode == FitMode.Width) FitToWidth(lite: true);
            else if (_fitMode == FitMode.Page) FitToPage(lite: true);
            StartResizeSettleTimer();
        }

        // Coalesces resize ticks: the crisp re-render runs once, a beat after the last size change.
        private void StartResizeSettleTimer()
        {
            if (_resizeRefitTimer is null)
            {
                _resizeRefitTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(110) };
                _resizeRefitTimer.Tick += (_, _) => { _resizeRefitTimer!.Stop(); OnResizeSettled(); };
            }
            _resizeRefitTimer.Stop();
            _resizeRefitTimer.Start();
        }

        private void OnResizeSettled()
        {
            if (_viewMode == ViewMode.Grid)
            {
                // Crisp re-render at the held column count for the final size (the drag only transform-
                // scaled the tiles). The grid's width is stable (vertical scrollbar reserved), so this
                // settles in one pass instead of looping.
                if (_doc is not null && _gridColumns >= 1)
                    SetZoom(Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns))));
                RepositionAnnotationBars();   // settle the bar against the final pane size
                return;
            }
            if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
            RepositionAnnotationBars();   // settle the bar against the final pane size (scrollbar may have toggled)
        }

        private int NearestContinuousPage(double yInPanel)
        {
            int best = -1; double bestDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                double bottom = top + h;
                double dist = yInPanel < top ? top - yInPanel : (yInPanel > bottom ? yInPanel - bottom : 0);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        private void SelectViewMode(ViewMode mode)
        {
            SetViewMode(mode);
            if (ViewCurrentLabel is not null) ViewCurrentLabel.Text = ViewModeDisplayName(mode);
            // Leave the flyout and Settings panel open so the user can try view modes back to back.
        }

        private string ViewModeDisplayName(ViewMode mode) => mode switch
        {
            ViewMode.Single  => Loc("Str_View_Single"),
            ViewMode.TwoPage => Loc("Str_View_TwoPage"),
            ViewMode.Grid    => Loc("Str_View_Grid"),
            _                => Loc("Str_View_Continuous"),
        };

        private void SetViewMode(ViewMode mode)
        {
            if (_viewMode == mode) return;
            _viewMode = mode;
            _renderedPrimaryPage = -1;   // spread/layout changes with the mode; force the next render
            _gridScrollToPage = -1;
            App.SetSetting("ViewMode", mode.ToString());

            bool isContinuous = mode == ViewMode.Continuous;
            _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;

            if (!isContinuous)
            {
                _continuousRenderCts?.Cancel();
                _continuousPanel.Children.Clear();
                _continuousTops.Clear();
                _continuousCanvases.Clear();
                _pages.Clear();
            }

            if (_doc is null) return;
            int idx = PageList.SelectedIndex;
            if (mode == ViewMode.Continuous)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => SetupContinuousView(idx));
            }
            else
            {
                _secondaryRenderCts?.Cancel();
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                // Drop any scroll offset carried over from the previous mode (especially Continuous,
                // whose large vertical offset would otherwise land the grid mid-document).
                PagePreviewPanel.ScrollToVerticalOffset(0);
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    RenderPage(mode == ViewMode.Grid ? 0 : idx);
                    // Grid: apply a clean column-fit zoom (continuous's zoom is far too large for a
                    // grid, and a non-column zoom leaves a gap). SetZoom -> ApplyZoom defers the
                    // single tile render, so return here instead of calling RefreshPageView again
                    // (a second render would duplicate tiles).
                    if (mode == ViewMode.Grid)
                    {
                        _gridColumns = Math.Min(_doc!.PageCount, 3);
                        SetZoom(GridZoomForN(_gridColumns));
                        // The first fit can run before the viewport width has settled (leaving the
                        // grid off-center / at the wrong zoom); re-fit once more after layout settles,
                        // and pin to the top so nothing carries over from the previous mode.
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                            (Action)(() =>
                            {
                                ReapplyGridOrFit();
                                // Selection is preserved across the switch; scroll to that page once
                                // its tile streams in (grid tiles render async). Page 0 stays at top.
                                if (idx > 0) _gridScrollToPage = idx;
                                else
                                {
                                    PagePreviewPanel.ScrollToVerticalOffset(0);
                                    PagePreviewPanel.ScrollToHorizontalOffset(0);
                                }
                            }));
                        return;
                    }
                    // Switching into Single or Two-Page fits the whole page so it isn't left at an
                    // awkward carried-over zoom from another mode.
                    if      (mode == ViewMode.Single || mode == ViewMode.TwoPage) FitToPage();
                    else if (_fitMode == FitMode.Width) FitToWidth();
                    else if (_fitMode == FitMode.Page)  FitToPage();
                    else                                ApplyZoom();
                    RefreshPageView(idx);
                });
            }
        }
    }
}
