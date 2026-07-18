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
        // Sidebar outline/bookmark panel
        // ============================================================

        private void SidebarPagesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToPagesTab();
        private void SidebarOutlinesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToOutlinesTab();

        private const double SidebarMaxPages = 234;   // stops when the 200px-capped thumbnail fills (200 + margins + scrollbar)
        private const double SidebarMaxOutlines = 480;
        private const double SidebarMinOpen = 120;   // narrowest readable width before labels/header clip

        private void SwitchSidebarToPagesTab()
        {
            _sidebarShowingOutlines = false;
            PageList.Visibility = Visibility.Visible;
            OutlineScrollViewer.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = Visibility.Visible;
            SidebarPagesTab.Foreground = (Brush)FindResource("Accent");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("TextSecondary");
            // Save current outlines width before snapping back to pages.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxOutlines);

            SidebarSplitter.IsEnabled = true;   // pages are resizable too now (drag the splitter)
            _sidebarCol.MaxWidth = SidebarMaxPages;
            if (!_sidebarCollapsed)
            {
                double target = _savedPagesWidth;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    (Action)(() => _sidebarCol.Width = new GridLength(target)));
            }
        }

        private void SwitchSidebarToOutlinesTab()
        {
            // Save current pages width, then restore (or auto-fit) the outlines width.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxPages);

            _sidebarShowingOutlines = true;
            PageList.Visibility = Visibility.Collapsed;
            OutlineScrollViewer.Visibility = Visibility.Visible;
            PageControlsRow.Visibility = Visibility.Collapsed;
            SidebarPagesTab.Foreground = (Brush)FindResource("TextSecondary");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("Accent");
            SidebarSplitter.IsEnabled = true;
            _sidebarCol.MaxWidth = SidebarMaxOutlines;
            if (!_sidebarCollapsed)
            {
                if (!_outlinesFitted)
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)AutoFitOutlineWidth);
                else
                {
                    double target = _savedOutlinesWidth;
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)(() => _sidebarCol.Width = new GridLength(target)));
                }
            }
        }

        /// <summary>
        /// Sizes the sidebar to fit the widest outline item by measuring each item's
        /// text width via FormattedText plus its indentation depth.
        /// </summary>
        private void AutoFitOutlineWidth()
        {
            if (_sidebarCollapsed) return;

            var typeface = new Typeface(
                OutlineTree.FontFamily, OutlineTree.FontStyle,
                OutlineTree.FontWeight, OutlineTree.FontStretch);
            double em = OutlineTree.FontSize;
            double max = 0;

            void Walk(ItemCollection items, int depth)
            {
                foreach (TreeViewItem node in items)
                {
                    var ft = new System.Windows.Media.FormattedText(
                        node.Header?.ToString() ?? string.Empty,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, em, Brushes.White,
                        /*pixelsPerDip*/ 1.0);
                    // 19 px indent per level + 19 px toggle + text + 12 px item padding
                    double w = depth * 19.0 + 19.0 + ft.Width + 12.0;
                    if (w > max) max = w;
                    if (node.Items.Count > 0)
                        Walk(node.Items, depth + 1);
                }
            }

            Walk(OutlineTree.Items, 0);

            // TreeView outer padding (8 px) + sidebar margins + scrollbar gutter (~36 px)
            double target = Math.Max(160.0, Math.Min(max + 44.0, SidebarMaxOutlines));
            _savedOutlinesWidth = target;
            _outlinesFitted = true;
            _sidebarCol.Width = new GridLength(target);
        }

        private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item && item.Tag is int pageIdx && pageIdx >= 0 && _doc is not null)
            {
                if (pageIdx < _doc.PageCount)
                    PageList.SelectedIndex = pageIdx;
            }
        }

        // The TreeView's own scroll viewer swallows the wheel before the outer one sees it, so the
        // Outlines list wouldn't scroll. Forward the wheel to the outer scroll viewer.
        private void OutlineScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            OutlineScrollViewer.ScrollToVerticalOffset(OutlineScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private void LoadOutlines()
        {
            _outlinesFitted = false;   // triggers auto-fit on next tab switch
            OutlineTree.Items.Clear();
            try
            {
                var outlines = _doc?.Outlines;
                if (outlines is null || outlines.Count == 0)
                {
                    SidebarOutlinesTab.IsEnabled = false;
                    return;
                }
                SidebarOutlinesTab.IsEnabled = true;
                AddOutlineItems(OutlineTree.Items, outlines);
            }
            catch
            {
                // Malformed outline - show a placeholder and don't crash
                SidebarOutlinesTab.IsEnabled = false;
            }
        }

        private void AddOutlineItems(ItemCollection target, PdfSharpCore.Pdf.PdfOutlineCollection outlines)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline outline in outlines)
            {
                int pageIdx = GetOutlinePageIndex(outline);
                var item = new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(outline.Title) ? "(untitled)" : outline.Title,
                    IsExpanded = true,
                    Tag = pageIdx,
                    ToolTip = pageIdx >= 0 ? $"Page {pageIdx + 1}" : null,
                    Style = (Style)FindResource("OutlineItemStyle")
                };
                if (outline.Outlines is not null && outline.Outlines.Count > 0)
                    AddOutlineItems(item.Items, outline.Outlines);
                target.Add(item);
            }
        }

        private int GetOutlinePageIndex(PdfSharpCore.Pdf.PdfOutline outline)
        {
            if (outline.DestinationPage is PdfSharpCore.Pdf.PdfPage destPage)
            {
                for (int i = 0; i < _doc!.PageCount; i++)
                    if (ReferenceEquals(_doc.Pages[i], destPage)) return i;
            }
            return -1;
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolEdit_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Edit);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolLine_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Line);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Image);
        private void ToolCrop_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Crop);
        private void ToolSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_signaturePopup is not null)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
                return;
            }
            SetTool(EditTool.Signature);
            ShowSignaturePopup();
        }
    }
}
