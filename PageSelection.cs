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
        // Page selection handler
        // ============================================================

        // Lazy accessor - resolves PageList's internal ScrollViewer on first use.
        private ScrollViewer? _sidebarSv;
        private ScrollViewer? SidebarScrollViewer
            => _sidebarSv ??= FindDescendant<ScrollViewer>(PageList);

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;
                var result = FindDescendant<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void PageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            SidebarScrollViewer?.ScrollToVerticalOffset(
                SidebarScrollViewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        private void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _doc is null) return;
            e.Handled = true;
            if (int.TryParse(_pageJumpBox.Text, out int pg))
            {
                int idx = Math.Max(0, Math.Min(_doc.PageCount - 1, pg - 1));
                PageList.SelectedIndex = idx;
            }
            else
            {
                // Restore current page number if input was invalid
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
            }
            Keyboard.ClearFocus();
        }

        private void PageJumpBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _pageJumpBox.SelectAll();
        }

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageList.SelectedIndex >= 0)
            {
                CommitActiveTextBox();
                ClearSelection();
                ClearTextSelection();
                PageList.ScrollIntoView(PageList.SelectedItem);   // keep the sidebar thumbnail in view
                if (_viewMode == ViewMode.Continuous)
                {
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    ScrollContinuousToPage(PageList.SelectedIndex);
                    return;
                }
                if (_viewMode == ViewMode.Grid)
                {
                    // Grid is a stable overview: selecting a page highlights it but must NOT
                    // re-anchor the grid. It still needs an initial render (open / first display)
                    // when no tiles exist yet; later selections only update the highlight.
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    if (_pageContentPanel.Children.Count <= 1)
                    {
                        PagePreviewPanel.ScrollToTop();
                        PagePreviewPanel.ScrollToHorizontalOffset(0);
                        RenderPage(0);   // grid primary is always page 0
                        // Default the grid to a clean 3-columns-across fit. Deferred to Loaded so the
                        // viewport width is valid (it can still be 0 mid-open, which would fall back
                        // to a carried-over zoom and show a single large page).
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                            (Action)(() => SetZoom(GridZoomForN(Math.Min(_doc?.PageCount ?? 1, 3)))));
                    }
                    else if (PageList.SelectedIndex < _pageContentPanel.Children.Count
                             && _pageContentPanel.Children[PageList.SelectedIndex] is FrameworkElement gridTile)
                    {
                        // Scroll the chosen page's tile into view (BringIntoView accounts for the zoom transform).
                        gridTile.BringIntoView();
                    }
                    return;
                }
                // Two-page spreads pair (0,1),(2,3),...; clicking either page of the spread that's
                // already shown (or re-selecting the current single page) renders the exact same pixels,
                // so skip the re-render and its flash - just move the page number.
                int targetPrimary = PageList.SelectedIndex;
                if (_viewMode == ViewMode.TwoPage) targetPrimary -= targetPrimary % 2;
                if (targetPrimary == _renderedPrimaryPage && Math.Abs(_zoomLevel - _lastRenderZoom) < 0.0001)
                {
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    return;
                }
                PagePreviewPanel.ScrollToTop();
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                RenderPage(PageList.SelectedIndex);
                ApplyZoom();
                // Update page jump box
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                // Re-highlight search results on this page if a search is active
                if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                    && _allSearchRects.Count > 0)
                    HighlightSearchResultsOnCurrentPage();
            }
        }

        private void ShortcutHelp_Click(object sender, RoutedEventArgs e)
        {
            if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
            else FadeOverlayIn(ShortcutOverlay);
        }

        private void ShortcutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click on the dim backdrop closes the overlay.
            FadeOverlayOut(ShortcutOverlay);
        }

        private void ShortcutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Stop the click from bubbling up to the backdrop handler.
            e.Handled = true;
        }

        private void ShortcutOverlayClose_Click(object sender, RoutedEventArgs e)
        {
            FadeOverlayOut(ShortcutOverlay);
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

    }

}
