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
        private void Search_Click(object sender, RoutedEventArgs e) => ToggleSearchBar();

        private void ToggleSearchBar()
        {
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                return;
            }
            ShowSearchBar();
        }

        private void ShowSearchBar()
        {
            if (_searchBar is null)
            {
                // Build search bar programmatically and inject into the preview area grid
                _searchBox = new TextBox
                {
                    Width = 200,
                    Height = 26,
                    FontFamily = UiKit.UiFont,
                    FontSize = 13,
                    SelectionBrush = AccentBrush(),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                // Live (DynamicResource-style) brushes so the box recolors on a theme switch while the
                // bar is open, instead of baking colors in at build time. Background uses the dark
                // toolbar/titlebar tone (BgSidebar).
                _searchBox.SetResourceReference(Control.BackgroundProperty, "BgSidebar");
                _searchBox.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
                _searchBox.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextPrimary");
                _searchBox.SetResourceReference(Control.BorderBrushProperty, "BorderDim");
                _searchBox.KeyDown += SearchBox_KeyDown;
                _searchBox.TextChanged += SearchBox_TextChanged;

                // Custom template so the default WPF blue focus/hover border never shows; keep our themed border.
                var tbTemplate = new ControlTemplate(typeof(TextBox));
                var tbBorder = new FrameworkElementFactory(typeof(Border));
                tbBorder.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(Control.BackgroundProperty));
                tbBorder.SetValue(Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(Control.BorderBrushProperty));
                tbBorder.SetValue(Border.BorderThicknessProperty, new System.Windows.TemplateBindingExtension(Control.BorderThicknessProperty));
                tbBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
                var tbHost = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
                tbHost.SetValue(ScrollViewer.PaddingProperty, new System.Windows.TemplateBindingExtension(Control.PaddingProperty));
                tbHost.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
                tbBorder.AppendChild(tbHost);
                tbTemplate.VisualTree = tbBorder;
                _searchBox.Template = tbTemplate;
                _searchBox.FocusVisualStyle = null;

                // Fixed width + centered so the result count never resizes the bar.
                _searchStatus = new TextBlock
                {
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Width = 56,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(2, 0, 2, 0)
                };
                _searchStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                // Small VSCode-style prev / next / close buttons. Hover tooltips carry the shortcuts.
                Button SearchNavBtn(string glyph, string tip, Action onClick, bool danger = false)
                {
                    var b = new Button
                    {
                        Content    = glyph,
                        FontFamily = UiKit.IconFont,
                        FontSize   = 12,
                        Width = 26, Height = 24,
                        Padding    = new Thickness(0),   // ToolbarButton's 10,6 padding clips the glyph in a 26px button
                        // The close X uses the shared danger style so its glyph turns red on hover like
                        // every other close X (window chrome, tabs, overlay headers).
                        Style      = (Style)FindResource(danger ? "DangerCloseButton" : "ToolbarButton"),
                        ToolTip    = tip
                    };
                    b.Click += (_, _) => onClick();
                    return b;
                }
                var prevBtn  = SearchNavBtn("", "Previous Match (Shift+Enter)", SearchPrevResult); // ChevronUp
                var nextBtn  = SearchNavBtn("", "Next Match (Enter)", SearchNextResult);            // ChevronDown
                var closeBtn = SearchNavBtn("", "Close (Esc)", CloseSearchBar, danger: true);       // Cancel

                var searchIcon = new TextBlock
                {
                    Text = "",  // Segoe MDL2 Search / magnifying glass
                    FontFamily = UiKit.IconFont,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    IsHitTestVisible = false
                };
                searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                // Drag grip: two columns of three dots on the left (same look as the sidebar splitter and
                // the annotate bars). Grabbing it moves the whole bar anywhere in the document area.
                var gripBrush = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
                var gripDots = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 6, 0)
                };
                for (int gcol = 0; gcol < 2; gcol++)
                {
                    var colDots = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
                    for (int grow = 0; grow < 3; grow++)
                        colDots.Children.Add(new Ellipse { Width = 3, Height = 3, Margin = new Thickness(1.5), Fill = gripBrush });
                    gripDots.Children.Add(colDots);
                }
                var searchGrip = new Border
                {
                    Background = Brushes.Transparent,   // transparent yet hit-testable, so it can be grabbed
                    Cursor = Cursors.SizeAll,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = gripDots,
                    ToolTip = "Drag to move"
                };

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 6, 8, 6)
                };
                panel.Children.Add(searchGrip);
                panel.Children.Add(searchIcon);
                panel.Children.Add(_searchBox);
                panel.Children.Add(_searchStatus);
                panel.Children.Add(prevBtn);
                panel.Children.Add(nextBtn);
                panel.Children.Add(closeBtn);

                _searchBar = new Border
                {
                    BorderThickness = new Thickness(1),
                    // Free-floating like the Signatures popup: positioned by Left/Top margin (set after
                    // layout from the saved spot) and draggable by the grip, so it can sit anywhere.
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4),
                    Child = GrainWrap(panel),
                    Margin = new Thickness(0),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.55 }
                };
                _searchBar.SetResourceReference(Border.BackgroundProperty, "BgFlyout");
                _searchBar.SetResourceReference(Border.BorderBrushProperty, "AccentBorder");

                // Add to the preview area grid (parent of ScrollViewer)
                var previewGrid = PagePreviewPanel.Parent as Grid;
                if (previewGrid is not null)
                {
                    // Above the annotate settings bars (ZIndex 100) so Ctrl+F is never hidden under
                    // the highlight/draw/text toolbar when both are open.
                    Panel.SetZIndex(_searchBar, 200);
                    previewGrid.Children.Add(_searchBar);
                    // Keep the whole bar on screen: when the preview area is resized, shrink the text box
                    // (not the buttons) so it never overflows, and re-clamp the floating bar back inside.
                    previewGrid.SizeChanged += (_, _) =>
                    {
                        FitSearchBox();
                        if (_searchBar is { Visibility: Visibility.Visible })
                        {
                            double cl = _searchBar.Margin.Left, ct = _searchBar.Margin.Top;
                            ClampPanelToBounds(_searchBar, previewGrid, ref cl, ref ct);
                            _searchBar.Margin = new Thickness(cl, ct, 0, 0);
                        }
                    };
                    // Place it (saved spot, or default near the old top-right position) and wire the grip
                    // for dragging once it's laid out and has a real width - mirrors the Signatures popup.
                    var bar = _searchBar;
                    var grip = searchGrip;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        ApplySavedPanelPosition(bar, previewGrid, "SearchBar", fallbackRightInset: 16, fallbackTop: 6);
                        EnablePanelDrag(grip, bar, previewGrid, "SearchBar");
                    }));
                }
            }

            _searchBar.Visibility = Visibility.Visible;
            _searchBox!.Text = "";
            if (_searchStatus != null) _searchStatus.Text = "";
            FitSearchBox();
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        // Sizes the search text box to whatever width is left after the icon, status, and nav
        // buttons, capped at a comfortable 200px, so the full bar always fits the preview area.
        private void FitSearchBox()
        {
            if (_searchBar is null || _searchBox is null) return;
            double avail = (PagePreviewPanel.Parent as Grid)?.ActualWidth ?? 0;
            const double reserved = 232;   // grip + icon + status + 3 buttons + paddings/margins
            _searchBox.Width = Math.Max(60, Math.Min(200, avail - reserved));
        }

        private void CloseSearchBar()
        {
            if (_searchBar is { Visibility: Visibility.Visible } bar)
            {
                // Fade out rather than blink away. On completion, collapse and restore opacity so the
                // next open shows it cleanly.
                var fade = new DoubleAnimation(bar.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(150)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                fade.Completed += (_, _) =>
                {
                    bar.Visibility = Visibility.Collapsed;
                    bar.BeginAnimation(UIElement.OpacityProperty, null);
                    bar.Opacity = 1;
                };
                bar.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            ClearSearchHighlights();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SearchPrevResult();
                else
                    SearchNextResult();
                e.Handled = true;
            }
        }

        private System.Windows.Threading.DispatcherTimer? _searchDebounce;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = _searchBox?.Text ?? "";
            if (text.Length < 2)
            {
                _searchDebounce?.Stop();
                ClearSearchHighlights();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchMatches.Clear();
                _searchMatchCursor = -1;
                _searchPageCursor = -1;
                if (_searchStatus is not null) _searchStatus.Text = "";
                return;
            }
            // Debounce: wait for a brief pause in typing before searching, so the first keystrokes
            // on a large document don't lock the UI while it searches partial queries.
            if (_searchDebounce is null)
            {
                _searchDebounce = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _searchDebounce.Tick += (_, _) =>
                {
                    _searchDebounce!.Stop();
                    var q = _searchBox?.Text ?? "";
                    if (q.Length >= 2) RunSearch(q);
                };
            }
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private readonly SearchService _searchService = new();

        // Flat, reading-ordered list of every match (page + rect) so Enter steps word-by-word rather
        // than page-by-page; _searchMatchCursor indexes it and that match is drawn with extra emphasis.
        private readonly List<(int page, double left, double bottom, double right, double top)> _searchMatches = [];
        private int _searchMatchCursor = -1;

        private void RunSearch(string query)
        {
            ClearSearchHighlights();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchMatches.Clear();
            _searchMatchCursor = -1;
            _searchPageCursor = -1;

            if (string.IsNullOrWhiteSpace(query) || _currentFile is null)
            {
                if (_searchStatus != null) _searchStatus.Text = "";
                return;
            }

            try
            {
                var sr = _searchService.Search(_currentFile, query);

                foreach (var kvp in sr.PageRects)
                    _allSearchRects[kvp.Key] = kvp.Value;
                _searchResultPages.AddRange(sr.ResultPages);

                // Flatten every match into one reading-ordered list (page asc, then top-to-bottom,
                // then left-to-right) so navigation steps word-by-word across the whole document.
                foreach (var page in _searchResultPages)
                    foreach (var (left, bottom, right, top) in _allSearchRects[page].OrderByDescending(r => r.top).ThenBy(r => r.left))
                        _searchMatches.Add((page, left, bottom, right, top));

                if (_searchMatches.Count == 0)
                {
                    if (_searchStatus != null) _searchStatus.Text = "No matches";
                    return;
                }

                _searchTotalHits = sr.TotalHits;

                // Start at the first match on or after the current page.
                int startPage = PageList.SelectedIndex;
                _searchMatchCursor = _searchMatches.FindIndex(m => m.page >= startPage);
                if (_searchMatchCursor < 0) _searchMatchCursor = 0;

                GoToCurrentMatch();
            }
            catch
            {
                if (_searchStatus != null) _searchStatus.Text = "Search error";
            }
        }

        // Navigates to the current match's page (if needed), updates the counter, and repaints
        // highlights with the current match emphasised. Shared by RunSearch and next/prev.
        private void GoToCurrentMatch()
        {
            if (_searchMatchCursor < 0 || _searchMatchCursor >= _searchMatches.Count) return;
            int targetPage = _searchMatches[_searchMatchCursor].page;
            _searchPageCursor = _searchResultPages.IndexOf(targetPage);   // keep the persisted page-cursor sane
            UpdateSearchStatus();
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            HighlightSearchResultsOnCurrentPage();
        }

        // Paints match highlights onto EVERY page that's currently on screen and has results -
        // the primary tile and all per-page overlays alike. Single Page shows the one page; Two-Page
        // and Grid show every visible tile with hits; Continuous shows them down the whole scroll.
        // Re-paints one page's match highlights onto its overlay using the in-memory page size (no
        // file I/O), so it's cheap enough to call at the tail of every RenderAllAnnotations - which is
        // what keeps highlights alive instead of being wiped by re-renders and continuous scrolling.
        private void ApplySearchHighlights(int page, Canvas canvas)
        {
            if (_searchBar is null || _searchBar.Visibility != Visibility.Visible) return;
            if (_doc is null || page < 0 || page >= _doc.PageCount) return;
            if (!_allSearchRects.TryGetValue(page, out var rects)) return;
            if (!_renderDims.TryGetValue(page, out var rd)) return;

            var (renderW, renderH) = rd;
            double pdfW = _doc.Pages[page].Width.Point;
            double pdfH = _doc.Pages[page].Height.Point;
            if (pdfW <= 0 || pdfH <= 0) return;
            double sx = renderW / pdfW;
            double sy = renderH / pdfH;

            bool hasCur = _searchMatchCursor >= 0 && _searchMatchCursor < _searchMatches.Count;
            var cur = hasCur ? _searchMatches[_searchMatchCursor] : default;

            foreach (var (left, bottom, right, top) in rects)
            {
                bool isCurrent = hasCur && cur.page == page
                    && cur.left == left && cur.bottom == bottom && cur.right == right && cur.top == top;
                AddSearchHighlight(canvas, left, bottom, right, top, sx, sy, renderH, isCurrent);
            }
        }

        // Repaints highlights on every page on screen right now (called when a search runs or the
        // current page changes); per-page re-renders keep them alive via ApplySearchHighlights.
        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            foreach (var kv in _allSearchRects)
            {
                var canvas = VisibleCanvasForPage(kv.Key);
                if (canvas is not null) ApplySearchHighlights(kv.Key, canvas);
            }
        }

        private int _searchTotalHits;

        // Compact count ("12 / 73" = current match / total matches); page breakdown in the tooltip.
        private void UpdateSearchStatus()
        {
            if (_searchStatus is null) return;
            if (_searchMatches.Count == 0)
            {
                _searchStatus.Text = "No matches";
                _searchStatus.ToolTip = null;
                return;
            }
            int pages = _searchResultPages.Count;
            _searchStatus.Text = $"{_searchMatchCursor + 1} / {_searchMatches.Count}";
            _searchStatus.ToolTip = $"{_searchMatches.Count} match{(_searchMatches.Count != 1 ? "es" : "")} on {pages} page{(pages != 1 ? "s" : "")}";
        }

        private void SearchNextResult()
        {
            if (_searchMatches.Count == 0) return;
            _searchMatchCursor = (_searchMatchCursor + 1) % _searchMatches.Count;
            GoToCurrentMatch();
        }

        private void SearchPrevResult()
        {
            if (_searchMatches.Count == 0) return;
            _searchMatchCursor = (_searchMatchCursor - 1 + _searchMatches.Count) % _searchMatches.Count;
            GoToCurrentMatch();
        }

        private void AddSearchHighlight(Canvas canvas, double left, double bottom, double right, double top,
            double sx, double sy, double renderH, bool isCurrent)
        {
            double cw = (right - left) * sx;
            double ch = (top - bottom) * sy;
            // A little breathing room so the box (and the current-match outline) wraps the whole word -
            // PdfPig's glyph bounds sit tight against the letters. Scales with text height so it looks
            // consistent across font sizes.
            double pad = ch * 0.30;
            double cx = left * sx - pad;
            double cy = renderH - (top * sy) - pad;
            var rect = new Rectangle
            {
                // The current match (search cursor) gets a brighter, more opaque fill; the others are dim.
                Fill = new SolidColorBrush(isCurrent
                    ? Color.FromArgb(150, 255, 190, 0)
                    : Color.FromArgb(70, 255, 165, 0)),
                StrokeThickness = isCurrent ? 3.5 : 1,
                RadiusX = pad * 0.6,
                RadiusY = pad * 0.6,
                Width = Math.Max(cw + pad * 2, 4),
                Height = Math.Max(ch + pad * 2, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            if (isCurrent)
                // Bind the current-match outline to the on-page accent resource so it recolors live on a
                // theme switch (matching the selection chrome), rather than baking the color in at paint time.
                rect.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
            else
                rect.Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 165, 0));
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            canvas.Children.Add(rect);
        }

        // Removes only the highlight rectangles from every page overlay. Deliberately does NOT touch
        // the result counter - that's owned by UpdateSearchStatus and the empty-query path - so a
        // repaint (which clears then re-adds highlights) can't wipe the "3 / 14" count.
        private void ClearSearchHighlights()
        {
            foreach (var canvas in AllPageCanvases())
            {
                var toRemove = canvas.Children.OfType<Rectangle>()
                    .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
                foreach (var r in toRemove)
                    canvas.Children.Remove(r);
            }
        }
    }
}
