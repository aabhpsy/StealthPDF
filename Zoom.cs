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
        // Zoom
        // ============================================================

        private void PagePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (_viewMode == ViewMode.Grid) { GridZoomStep(e.Delta < 0); return; }

                // Capture cursor position and scroll offsets BEFORE zoom changes so we can
                // compute the new offsets that keep the point under the cursor stationary.
                Point cursorInViewport = e.GetPosition(PagePreviewPanel);
                double oldZoom = _zoomLevel;
                double oldHOff = PagePreviewPanel.HorizontalOffset;
                double oldVOff = PagePreviewPanel.VerticalOffset;

                SetZoom(e.Delta > 0 ? _zoomLevel + ZoomStep : _zoomLevel - ZoomStep);

                // After layout settles, reposition the scroll so the cursor point stays fixed.
                // Formula: newOffset = (oldOffset + cursorPos) * (newZoom / oldZoom) - cursorPos
                double ratio = _zoomLevel / oldZoom;
                double newHOff = (oldHOff + cursorInViewport.X) * ratio - cursorInViewport.X;
                double newVOff = (oldVOff + cursorInViewport.Y) * ratio - cursorInViewport.Y;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    PagePreviewPanel.ScrollToHorizontalOffset(Math.Max(0, newHOff));
                    PagePreviewPanel.ScrollToVerticalOffset(Math.Max(0, newVOff));
                }));
                return;
            }

            // Regular scroll. Grid and Continuous are a single scroll over the WHOLE document, so the
            // wheel must never be hijacked for page navigation there - just let the ScrollViewer
            // scroll. This is the fix for grid refusing to scroll: right after a zoom/column change
            // the extent can momentarily measure as zero, and the old page-nav fallback below would
            // fire instead of scrolling (and stick until a theme/view-mode switch forced a re-measure).
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.Continuous)
                return;

            // Single / Two-Page: a page often fits the viewport, so at the scroll boundary fall
            // through to page navigation so the user can reach adjacent pages without the sidebar.
            if (PagePreviewPanel.ScrollableHeight <= 0)
            {
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
                return;
            }

            bool atTop = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            if ((atTop && e.Delta > 0) || (atBottom && e.Delta < 0))
            {
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
            }
            // Otherwise let the ScrollViewer scroll naturally.
        }

        // Walks up the visual tree from the press's hit element to see if it landed on the scrollbar
        // (thumb, track, or repeat buttons). Used to exempt scrollbar presses from pane pan/marquee/crop.
        private static bool PressIsOnScrollBar(MouseButtonEventArgs e)
        {
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d is not null)
            {
                if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void PagePreviewPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // A press that lands on the document scrollbar must reach the scrollbar itself (thumb drag,
            // track paging). The pan/crop/marquee handling below otherwise claims the press first and sets
            // e.Handled, so the thumb could never be grabbed. Let scrollbar presses fall through untouched.
            if (PressIsOnScrollBar(e)) return;

            bool spaceDown = Keyboard.IsKeyDown(Key.Space);
            if (e.ChangedButton == MouseButton.Middle ||
                (e.ChangedButton == MouseButton.Left && spaceDown))
            {
                _isPanning = true;
                _panStart = e.GetPosition(PagePreviewPanel);
                _panScrollH = PagePreviewPanel.HorizontalOffset;
                _panScrollV = PagePreviewPanel.VerticalOffset;
                PagePreviewPanel.CaptureMouse();
                PagePreviewPanel.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
            // Crop: allow starting the selection OUTSIDE the page - catch margin clicks, route them to the
            // nearest page overlay, and clamp the start to the page edge so the crop rect stays on the page.
            else if (e.ChangedButton == MouseButton.Left && !spaceDown
                     && _currentTool == EditTool.Crop && _doc is not null)
            {
                Canvas? target = ResolveMarginOverlay(e);
                if (target is not null && target.Width > 0 && target.Height > 0)
                {
                    _activeCanvas = target;
                    // Pin the gesture surface/page so mouse-move/up resolve against this overlay
                    // (a margin crop start doesn't go through Canvas_MouseLeftButtonDown).
                    _gestureCanvas = target;
                    _gesturePage = target.Tag is int gt ? gt : PageList.SelectedIndex;
                    var p = e.GetPosition(target);
                    p.X = Math.Max(0, Math.Min(target.Width, p.X));
                    p.Y = Math.Max(0, Math.Min(target.Height, p.Y));
                    StartCropDraw(p);
                    e.Handled = true;
                }
            }
            // Marquee select: start a selection rectangle in the margin so it can span onto the pages. Same
            // routing as crop, but the start point is NOT clamped, so the box can begin off-page.
            else if (e.ChangedButton == MouseButton.Left && !spaceDown
                     && _currentTool == EditTool.Select && _doc is not null)
            {
                Canvas? target = ResolveMarginOverlay(e);
                if (target is not null && target.Width > 0 && target.Height > 0)
                {
                    StartMarqueeDraw(target, e.GetPosition(target));
                    e.Handled = true;
                }
            }
        }

        // Resolves which page overlay a margin (off-page) click attaches to, or null when the click is
        // actually on a page (left to that page's own surface). Shared by off-page crop and marquee starts.
        private Canvas? ResolveMarginOverlay(MouseButtonEventArgs e)
        {
            if (_viewMode == ViewMode.Continuous)
            {
                if (e.OriginalSource is DependencyObject osc && IsWithinPageOverlay(osc)) return null;
                int pg = PageList.SelectedIndex;
                if (pg < 0 || !_continuousCanvases.ContainsKey(pg))
                    pg = NearestContinuousPage(e.GetPosition(_continuousPanel).Y);
                return pg >= 0 && _continuousCanvases.TryGetValue(pg, out var c) ? c : null;
            }
            bool onPrimary = e.OriginalSource is DependencyObject oss && IsDescendantOf(oss, _annotationCanvas);
            bool onTile = e.OriginalSource is DependencyObject ost && IsWithinPageOverlay(ost);
            return (!onPrimary && !onTile) ? _annotationCanvas : null;
        }

        // Begins a marquee anchored to refCanvas at posInRef (that page's coords, possibly off-page and
        // un-clamped). The box draws on the cross-page MarqueeLayer; the existing move/up handlers finish it.
        private void StartMarqueeDraw(Canvas refCanvas, Point posInRef)
        {
            _activeCanvas = refCanvas;
            _gestureCanvas = refCanvas;
            _gesturePage = refCanvas.Tag is int gt ? gt : PageList.SelectedIndex;
            ClearSelection();
            ClearTextSelection();
            _isSelecting = true;
            _selectStart = posInRef;
            _selectRect = new Rectangle
            {
                Fill = AccentBrush(40),
                Stroke = AccentBrush(150),
                StrokeThickness = 1,
                Width = 0, Height = 0,
                IsHitTestVisible = false
            };
            MarqueeLayer.Children.Add(_selectRect);
            UpdateMarquee(posInRef, posInRef);
            refCanvas.CaptureMouse();
        }

        // Begin a crop selection on the active overlay at pos (render-dim coords).
        private void StartCropDraw(Point pos)
        {
            _cropPageIndex = _activeCanvas.Tag is int cpi ? cpi : (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex);
            ClearSelection();
            _isDrawing = true;
            _drawStart = pos;
            // Draw the NEW box as a separate rect; the existing box, handles, and bar stay put until this
            // draw is committed on mouse-up (so a mouse-down never wipes the current box or bar).
            var cropDrawRect = new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                StrokeDashArray = [5, 3],
                Fill = AccentBrush(55),
                Width = 0,
                Height = 0,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
            };
            Canvas.SetLeft(cropDrawRect, pos.X);
            Canvas.SetTop(cropDrawRect, pos.Y);
            Panel.SetZIndex(cropDrawRect, 2);
            _activeCanvas.Children.Add(cropDrawRect);
            _activePreview = cropDrawRect;
            _activeCanvas.CaptureMouse();
        }

        private bool IsWithinPageOverlay(DependencyObject node)
        {
            var cur = node;
            while (cur != null)
            {
                if (cur is Canvas c && _continuousCanvases.ContainsValue(c)) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private void PagePreviewPanel_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(PagePreviewPanel);
            PagePreviewPanel.ScrollToHorizontalOffset(_panScrollH - (pos.X - _panStart.X));
            PagePreviewPanel.ScrollToVerticalOffset(_panScrollV - (pos.Y - _panStart.Y));
            e.Handled = true;
        }

        private void PagePreviewPanel_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            _isPanning = false;
            PagePreviewPanel.ReleaseMouseCapture();
            PagePreviewPanel.Cursor = _spaceHeld ? Cursors.Hand : Cursors.Arrow;
            e.Handled = true;
        }
    }
}
