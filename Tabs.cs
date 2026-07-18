using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfSharpCore.Pdf;

namespace StealthPDF
{
    // Tabbed document support. StealthPDF keeps one window and one live "working set" of
    // per-document fields (in MainWindow.xaml.cs). Each open PDF is a DocumentSession that
    // owns its own copy of those fields. Switching tabs captures the live fields into the
    // outgoing session and applies the incoming session's fields, then re-renders.
    public partial class MainWindow
    {
        // One open document. Holds the per-document state that the rest of MainWindow reads
        // and writes through its instance fields. The collection references here ARE the live
        // collections while this session is active.
        private sealed class DocumentSession
        {
            public PdfDocument? Doc;
            public string? CurrentFile;
            public string? OriginalFile;
            // Set on a restored tab that hasn't been loaded yet (lazy tabs): Doc stays null until the
            // user first switches to it, so startup doesn't render every reopened PDF.
            public string? DeferredPath;

            public double ZoomLevel = 1.0;
            public double LastRenderZoom = 1.0;
            public FitMode Fit = FitMode.None;
            public ViewMode View = ViewMode.Continuous;
            public int GridColumns = 3;                // grid column count; grid zoom is derived from this, so it must be per-tab too
            public EditTool Tool = EditTool.Select;   // active editing tool, remembered per document
            public int PageIndex;
            public bool IsDirty;
            public double ScrollH;
            public double ScrollV;
            public int SearchPageCursor = -1;

            public Dictionary<int, List<PageAnnotation>> Annotations = [];
            public Dictionary<int, (int w, int h)> RenderDims = [];
            // LRU render cache: rasterized page bitmaps keyed by (page, size-bucket, rotation). Lets a
            // switch back to a recent tab reuse the bitmaps instead of re-running pdfium. Concurrent because
            // the continuous/secondary streamers read it from a background thread. Cleared on edits that change
            // a page's pixels or page order, and dropped entirely when the tab falls out of the LRU window.
            public readonly System.Collections.Concurrent.ConcurrentDictionary<(int page, int bucket, int rot), System.Windows.Media.Imaging.BitmapSource> RenderCache = new();
            public Dictionary<int, int> PageRotations = [];
            public Dictionary<int, string> FormTextValues = [];
            public Dictionary<int, bool> FormCheckValues = [];
            public Dictionary<string, string> FormRadioValues = [];
            public Dictionary<int, double> FormFontSizes = [];
            public Stack<UndoEntry> UndoStack = new();
            public Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects = [];
            public List<int> SearchResultPages = [];
            public StampSpec? DocStampSpec;
            public Dictionary<int, List<StampInstance>> Stamps = [];
            public Dictionary<int, List<Rect>> StampHitRects = [];

            public string Title =>
                string.IsNullOrEmpty(OriginalFile)
                    ? "Untitled"
                    : System.IO.Path.GetFileNameWithoutExtension(OriginalFile);
        }

        private readonly List<DocumentSession> _sessions = [];
        private DocumentSession? _active;

        // ============================================================
        // Session state capture / apply
        // ============================================================

        // Copy the live working set INTO the session (call before switching away from it).
        private void CaptureSessionState(DocumentSession s)
        {
            s.Doc            = _doc;
            s.CurrentFile    = _currentFile;
            s.OriginalFile   = _originalFile;
            s.ZoomLevel      = _zoomLevel;
            s.LastRenderZoom = _lastRenderZoom;
            s.Fit            = _fitMode;
            s.View           = _viewMode;
            s.GridColumns    = _gridColumns;
            s.Tool           = _currentTool;
            s.IsDirty        = _isDirty;
            s.SearchPageCursor = _searchPageCursor;
            s.PageIndex      = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : s.PageIndex;
            s.ScrollH        = PagePreviewPanel?.HorizontalOffset ?? 0;
            s.ScrollV        = PagePreviewPanel?.VerticalOffset ?? 0;

            s.Annotations      = _annotations;
            s.RenderDims       = _renderDims;
            s.PageRotations    = _pageRotations;
            s.FormTextValues   = _formTextValues;
            s.FormCheckValues  = _formCheckValues;
            s.FormRadioValues  = _formRadioValues;
            s.FormFontSizes    = _formFontSizes;
            s.UndoStack        = _undoStack;
            s.AllSearchRects   = _allSearchRects;
            s.SearchResultPages = _searchResultPages;
            s.DocStampSpec     = _docStampSpec;
            s.Stamps           = _stamps;
            s.StampHitRects    = _stampHitRects;
            // Persist this document's fit/zoom/view/page so reopening it (even after a restart) restores it.
            SaveDocState(s.OriginalFile, s.Fit, s.ZoomLevel, s.View, s.PageIndex);
        }

        // ── Per-document view state (persisted across restarts, keyed by file path) ──────────────────
        // So reopening a file restores how you left it (fit mode, zoom, view mode, page) instead of the
        // per-view-mode default. Stored as one registry value: lines of "path|fit|zoom|view|page", most
        // recent first, capped. '|' and newline are both illegal in Windows paths, so they're safe delimiters.
        private const int DocStatesMax = 40;

        private void SaveDocState(string? path, FitMode fit, double zoom, ViewMode view, int page)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;   // skip Untitled/imported
            string entry = string.Join("|", path,
                fit.ToString(),
                zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),
                view.ToString(),
                page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var lines = new List<string> { entry };
            var raw = App.GetSetting("DocStates");
            if (!string.IsNullOrEmpty(raw))
                foreach (var line in raw!.Split('\n'))
                {
                    if (line.Length == 0) continue;
                    int bar = line.IndexOf('|');
                    string lpath = bar > 0 ? line[..bar] : line;
                    if (!string.Equals(lpath, path, StringComparison.OrdinalIgnoreCase))
                        lines.Add(line);
                }
            if (lines.Count > DocStatesMax) lines = lines.GetRange(0, DocStatesMax);
            App.SetSetting("DocStates", string.Join("\n", lines));
        }

        private bool TryGetDocState(string? path, out FitMode fit, out double zoom, out ViewMode view, out int page)
        {
            fit = FitMode.None; zoom = 1.0; view = ViewMode.Continuous; page = 0;
            if (string.IsNullOrEmpty(path)) return false;
            var raw = App.GetSetting("DocStates");
            if (string.IsNullOrEmpty(raw)) return false;
            foreach (var line in raw!.Split('\n'))
            {
                var p = line.Split('|');
                if (p.Length < 5 || !string.Equals(p[0], path, StringComparison.OrdinalIgnoreCase)) continue;
                Enum.TryParse(p[1], out fit);
                double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out zoom);
                Enum.TryParse(p[3], out view);
                int.TryParse(p[4], out page);
                if (zoom <= 0) zoom = 1.0;
                return true;
            }
            return false;
        }

        // Point the live working set AT the session's state. Pure field assignment - no UI.
        private void ApplySessionState(DocumentSession s)
        {
            _doc            = s.Doc;
            _currentFile    = s.CurrentFile;
            _originalFile   = s.OriginalFile;
            _zoomLevel      = s.ZoomLevel;
            _lastRenderZoom = s.LastRenderZoom;
            _fitMode        = s.Fit;
            _viewMode       = s.View;
            _gridColumns    = s.GridColumns;
            _currentTool    = s.Tool;
            _isDirty        = s.IsDirty;
            _searchPageCursor = s.SearchPageCursor;

            _annotations      = s.Annotations;
            _renderDims       = s.RenderDims;
            _pageRotations    = s.PageRotations;
            _formTextValues   = s.FormTextValues;
            _formCheckValues  = s.FormCheckValues;
            _formRadioValues  = s.FormRadioValues;
            _formFontSizes    = s.FormFontSizes;
            _undoStack        = s.UndoStack;
            _allSearchRects   = s.AllSearchRects;
            _searchResultPages = s.SearchResultPages;
            _docStampSpec      = s.DocStampSpec;
            _stamps            = s.Stamps;
            _stampHitRects     = s.StampHitRects;
            UpdateStampIndicator();
            TouchRenderLru(s);   // this tab is now active: keep its render cache, evict tabs beyond the window
        }

        // ── LRU render-bitmap cache ───────────────────────────────────────────────────────────────────
        // Keeps the rasterized page bitmaps of the most-recent few tabs so switching back skips pdfium and
        // fills instantly. The render paths (single / secondary tiles / continuous) check the active tab's
        // cache before rasterizing and store the frozen bitmap after building it.
        private readonly List<DocumentSession> _renderLru = [];
        private const int RenderCacheTabCap = 3;

        // Background-thread safe: a cached frozen bitmap for this render, or null (the caller must rasterize).
        private static System.Windows.Media.Imaging.BitmapSource? TryGetCachedRender(DocumentSession? s, int page, int bucket, int rot)
            => (s != null && s.RenderCache.TryGetValue((page, bucket, rot), out var b)) ? b : null;

        private static void CacheRender(DocumentSession? s, int page, int bucket, int rot, System.Windows.Media.Imaging.BitmapSource bmp)
        {
            if (s == null) return;
            if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();
            s.RenderCache[(page, bucket, rot)] = bmp;
        }

        // Mark a tab most-recently-used; drop the bitmap caches of tabs that fall outside the LRU window.
        private void TouchRenderLru(DocumentSession? s)
        {
            if (s == null) return;
            _renderLru.Remove(s);
            _renderLru.Add(s);
            while (_renderLru.Count > RenderCacheTabCap)
            {
                var old = _renderLru[0];
                _renderLru.RemoveAt(0);
                old.RenderCache.Clear();
            }
        }

        // Drop a tab's cached bitmaps after an edit that changes page pixels or page order.
        private void InvalidateRenderCache(DocumentSession? s) => s?.RenderCache.Clear();

        // Make sure there is always at least one session, adopting whatever is currently live.
        private void EnsureInitialSession()
        {
            if (_sessions.Count > 0) return;
            var s = new DocumentSession();
            _sessions.Add(s);
            _active = s;
            CaptureSessionState(s);
        }

        // Commit / cancel any in-progress interaction so it doesn't bleed onto another document.
        private void CancelTransientForSwitch()
        {
            CommitActiveTextBox();
            RemoveTextEditHandles();
            ClearSelection();
            ClearTextSelection();
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
        }

        // ============================================================
        // Rendering the active session
        // ============================================================

        // Re-render whatever document the active session holds (or show the empty drop zone).
        private void RenderActiveSession()
        {
            if (_active == null || _active.Doc == null) { ShowEmptyState(); return; }

            FileNameLabel.Text = System.IO.Path.GetFileName(_active.OriginalFile ?? "");
            _annotationCanvas.Children.Clear();
            MarkDirty(_isDirty);   // sync the Save button color to this tab's dirty state
            BootstrapDocumentView(_active.PageIndex, autoFit: false);
            SetTool(_active.Tool); // restore this document's active editing tool (and its tool bar)

            // Restore the saved scroll position after the Background zoom pass queued inside
            // BootstrapDocumentView has run (ContextIdle is lower priority than Background).
            double sh = _active.ScrollH, sv = _active.ScrollV;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
            {
                try
                {
                    PagePreviewPanel.ScrollToHorizontalOffset(sh);
                    PagePreviewPanel.ScrollToVerticalOffset(sv);
                }
                catch { }
            }));
        }

        // Visual reset to the no-document drop-zone state. Mirrors CloseFile's teardown but
        // does not close the document or touch session bookkeeping (callers handle that).
        private void ShowEmptyState()
        {
            _activeTextBox = null;
            RemoveTextEditHandles();
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            PageImage.Source = null;
            _annotationCanvas.Children.Clear();
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PopulateRecentFilesList();
            PagePreviewPanel.Visibility = Visibility.Collapsed;
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            SetTool(EditTool.Select);
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
            _pageJumpBox.IsEnabled = false;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _pageJumpBox.Text = "";
            _pageTotalLabel.Text = "/ –";
            OutlineTree.Items.Clear();
            SidebarOutlinesTab.IsEnabled = false;
            if (_sidebarShowingOutlines) SwitchSidebarToPagesTab();
            MarkDirty(false);
            SetStatus("Ready");
        }

        // ============================================================
        // Opening / switching / closing tabs
        // ============================================================

        // Prepare a tab to receive a document load: capture the current tab, then either reuse
        // the active tab if it's empty or create a new one, and blank the live working set.
        private DocumentSession BeginTabLoad(out DocumentSession? prev, out bool createdNew)
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            CancelTransientForSwitch();
            prev = _active;
            if (_active != null) CaptureSessionState(_active);

            DocumentSession target;
            if (_active != null && _active.Doc == null && _active.DeferredPath == null)
            {
                target = _active;          // reuse the current empty tab (never a deferred one)
                createdNew = false;
            }
            else
            {
                target = new DocumentSession();
                // Inherit the current view mode so a newly opened PDF doesn't snap back to the
                // default (Continuous) when the user prefers Single / Two-Page / Grid.
                if (prev != null) { target.View = prev.View; target.Fit = prev.Fit; }
                _sessions.Add(target);
                createdNew = true;
            }
            _active = target;
            ApplySessionState(target);     // blank live fields (target has no document yet)
            return target;
        }

        // Roll back a failed / cancelled load started by BeginTabLoad.
        private void AbortTabLoad(DocumentSession target, DocumentSession? prev, bool createdNew)
        {
            if (createdNew) _sessions.Remove(target);
            _active = prev;
            if (prev != null) { ApplySessionState(prev); RenderActiveSession(); }
            else { EnsureInitialSession(); RenderActiveSession(); }
            RebuildTabStrip();
        }

        // Returns an open session for the given file path (case-insensitive full-path match), or null.
        private DocumentSession? FindOpenSession(string path)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
            return _sessions.FirstOrDefault(s =>
                (s.Doc != null || s.DeferredPath != null) &&
                !string.IsNullOrEmpty(s.OriginalFile) &&
                string.Equals(SafeFullPath(s.OriginalFile!), full, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeFullPath(string p)
        {
            try { return System.IO.Path.GetFullPath(p); } catch { return p; }
        }

        // Open a PDF in its own tab (reusing the current tab if it is empty). If the same file is
        // already open in an unedited tab, switch to that tab instead of opening a duplicate.
        private void OpenInNewTab(string path)
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            if (_active != null) CaptureSessionState(_active);   // keep dirty / path current for the check

            var existing = FindOpenSession(path);
            if (existing != null && !existing.IsDirty)
            {
                SwitchToTab(existing);
                SetStatus($"Already open: {System.IO.Path.GetFileName(path)}");
                return;
            }

            var target = BeginTabLoad(out var prev, out bool createdNew);
            OpenFile(path);
            if (_doc == null)
            {
                // A background open (encryption strip / repair) finalizes this tab itself, so the
                // not-yet-loaded _doc isn't a failure - leave the tab in place.
                if (_asyncOpenPending) return;
                // Open failed, was cancelled, or a password prompt was dismissed.
                AbortTabLoad(target, prev, createdNew);
                return;
            }
            CaptureSessionState(_active!);
            SetTool(_currentTool);   // sync the tool UI to this (new) tab's tool
            RebuildTabStrip();
        }

        // Cycle to the next (dir = +1) or previous (dir = -1) open document tab.
        private void CycleTab(int dir)
        {
            var docTabs = _sessions.Where(t => t.Doc != null || t.DeferredPath != null).ToList();
            if (docTabs.Count < 2 || _active == null) return;
            int i = docTabs.IndexOf(_active);
            if (i < 0) return;
            int next = (i + dir + docTabs.Count) % docTabs.Count;
            SwitchToTab(docTabs[next]);
        }

        // Switch the active tab to an already-loaded session.
        private void SwitchToTab(DocumentSession target)
        {
            if (target == _active) return;
            CommitActiveTextBox();
            CancelTransientForSwitch();
            if (_active != null) CaptureSessionState(_active);
            _active = target;
            ApplySessionState(target);
            // Hide the document content while the new tab renders and restores its scroll position, then fade
            // it in. This masks the rebuild and the "loads at the top then snaps to my place" jump - the user
            // only sees the final, correctly-scrolled view fade in. PageContentGrid is the parent of BOTH the
            // single/grid panel and the continuous panel, so one fade covers every view mode.
            PageContentGrid.BeginAnimation(UIElement.OpacityProperty, null);
            PageContentGrid.Opacity = 0;
            if (target.Doc == null && target.DeferredPath != null)
            {
                MaterializeDeferred(target);
                RebuildTabStrip();      // first load of a deferred tab: title/dirty may change, so rebuild
            }
            else
            {
                RenderActiveSession();
                RestyleActiveTab();     // already loaded: restyle in place, no strip rebuild (no flicker)
            }
            FadeInDocContent();
        }

        // Fade the document pane content back in after a switch. Queued at ContextIdle so it runs AFTER the
        // scroll-position restore (also ContextIdle, queued earlier by RenderActiveSession) - the snap to
        // position happens while hidden, so it's never seen. Always lands at full opacity.
        private void FadeInDocContent()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
            {
                var fade = new System.Windows.Media.Animation.DoubleAnimation(
                    0, 1, new Duration(TimeSpan.FromMilliseconds(140)))
                { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
                fade.Completed += (_, _) =>
                {
                    PageContentGrid.BeginAnimation(UIElement.OpacityProperty, null);
                    PageContentGrid.Opacity = 1;
                };
                PageContentGrid.BeginAnimation(UIElement.OpacityProperty, fade);
            }));
        }

        // Load a restored-but-deferred tab's PDF the first time it is viewed (lazy tabs). The session
        // must already be the live working set (ApplySessionState called) before this runs.
        private void MaterializeDeferred(DocumentSession target)
        {
            var path = target.DeferredPath;
            target.DeferredPath = null;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                RenderActiveSession();        // file vanished since last session - show the empty state
                return;
            }
            OpenFile(path!);                  // loads into the live fields and renders the view
            if (_doc == null)
            {
                if (_asyncOpenPending) return;   // background strip/repair finalizes the tab itself
                RenderActiveSession(); return;
            }
            CaptureSessionState(target);      // persist the now-loaded document back into the session
        }

        // Close a tab. Prompts to save if that tab has unsaved changes, then switches to a
        // neighbouring tab (or the empty state when the last tab closes).
        // Closes every open document tab except `keep` (each may prompt to save if dirty, like a manual close).
        private void CloseOtherTabs(DocumentSession keep)
        {
            foreach (var s in _sessions.Where(z => !ReferenceEquals(z, keep) && (z.Doc != null || z.DeferredPath != null)).ToList())
                CloseTab(s);
        }

        private void CloseTab(DocumentSession? s)
        {
            EnsureInitialSession();
            if (s == null) return;

            // Make the target the live working set so its dirty flag / document are current.
            if (s != _active)
            {
                CommitActiveTextBox();
                CancelTransientForSwitch();
                if (_active != null) CaptureSessionState(_active);
                _active = s;
                ApplySessionState(s);
                RenderActiveSession();
            }
            else
            {
                CommitActiveTextBox();
                CaptureSessionState(s);
            }

            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedClose"),
                    "StealthPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) { RebuildTabStrip(); return; }
            }

            try { _doc?.Close(); } catch { }
            _doc = null;

            int idx = _sessions.IndexOf(s);
            _sessions.Remove(s);
            _renderLru.Remove(s);    // don't pin a closed tab's render cache in the LRU list
            s.RenderCache.Clear();

            if (_sessions.Count == 0)
            {
                App.RemoveSetting("LastFile");   // a manually emptied window won't reopen on launch
                var blank = new DocumentSession();
                _sessions.Add(blank);
                _active = blank;
                ApplySessionState(blank);
                ShowEmptyState();
            }
            else
            {
                var next = _sessions[Math.Min(idx, _sessions.Count - 1)];
                _active = next;
                ApplySessionState(next);
                if (next.Doc == null && next.DeferredPath != null) MaterializeDeferred(next);
                else RenderActiveSession();
            }
            RebuildTabStrip();
        }

        // Ctrl+Q: close every open document and reset to a single blank tab, with one combined warning
        // if anything is unsaved (rather than a prompt per tab).
        private void CloseAllTabs()
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            if (_active != null) CaptureSessionState(_active);

            var docTabs = _sessions.Where(t => t.Doc != null || t.DeferredPath != null).ToList();
            if (docTabs.Count == 0) return;

            if (docTabs.Any(t => t.IsDirty))
            {
                var res = KillerDialog.Show(this, Loc("Str_Dlg_UnsavedCloseAll"),
                    "StealthPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) { RebuildTabStrip(); return; }
            }

            foreach (var s in docTabs) { try { s.Doc?.Close(); } catch { } }
            try { _doc?.Close(); } catch { }
            _doc = null;

            _sessions.Clear();
            App.RemoveSetting("LastFile");   // a manually emptied window won't reopen on launch
            var blank = new DocumentSession();
            _sessions.Add(blank);
            _active = blank;
            ApplySessionState(blank);
            ShowEmptyState();
            RebuildTabStrip();
        }

        // External (single-instance) entry points, called from App when a second launch forwards
        // a file path to this already-running instance.
        public void OpenFromExternal(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) OpenInNewTab(path!);
        }

        public void RestoreAndActivate()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Briefly toggle Topmost to pull the window in front without keeping it pinned.
            Topmost = true;
            Topmost = false;
            Focus();
        }

        // ============================================================
        // Tab strip UI (built in code so it follows the active theme)
        // ============================================================

        private readonly List<(DocumentSession s, FrameworkElement btn, double natW)> _tabButtonList = [];
        private readonly Dictionary<DocumentSession, FrameworkElement> _tabButtonCache = [];
        private System.Windows.Threading.DispatcherTimer? _tabReflowTimer;
        private bool _reflowingTabs;

        // Reconcile TabStrip.Children to exactly `desired` (in order) with minimal churn - never a full Clear,
        // so existing tab buttons (and the active tab's drop shadow) are not re-parented / re-rasterized. This
        // is what stops the strip blinking on switch / drag-release / resize.
        private void SetTabStripChildren(IList<FrameworkElement> desired)
        {
            if (TabStrip is null) return;
            for (int i = TabStrip.Children.Count - 1; i >= 0; i--)
                if (TabStrip.Children[i] is FrameworkElement fe && !desired.Contains(fe))
                    TabStrip.Children.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                int cur = TabStrip.Children.IndexOf(desired[i]);
                if (cur < 0) TabStrip.Children.Insert(Math.Min(i, TabStrip.Children.Count), desired[i]);
                else if (cur != i) { TabStrip.Children.RemoveAt(cur); TabStrip.Children.Insert(Math.Min(i, TabStrip.Children.Count), desired[i]); }
            }
        }

        // Refresh a REUSED tab button's title / dirty dot / reserved width and active styling, in place.
        private void RefreshTabButton(FrameworkElement btn, DocumentSession s)
        {
            if (btn is not Border bd) return;
            bd.ToolTip = s.OriginalFile ?? "Untitled";
            var label = TabLabelOf(bd);
            if (label != null)
            {
                string txt = (s.IsDirty ? "• " : "") + s.Title;
                if (!string.Equals(label.Text, txt))
                {
                    label.Text = txt;
                    double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                    var ft = new FormattedText(txt, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(label.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                        label.FontSize, Brushes.Black, dip);
                    label.MinWidth = Math.Min(183, Math.Ceiling(ft.Width) + 1);
                    label.Tag = label.MinWidth;
                }
            }
            ApplyTabButtonState(bd, label, s == _active);
        }

        private void RebuildTabStrip()
        {
            if (TabStrip == null || TabStripBorder == null) return;

            var docTabs = _sessions.Where(t => t.Doc != null || t.DeferredPath != null).ToList();
            // Only show the strip once there's more than one document - a single open PDF doesn't need tabs.
            if (docTabs.Count < 2)
            {
                TabStrip.Children.Clear();
                _tabButtonList.Clear();
                _tabButtonCache.Clear();
                TabStripBorder.Visibility = Visibility.Collapsed;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
                return;
            }

            TabStripBorder.Visibility = Visibility.Visible;

            // Reuse one Border per session (cached) instead of recreating every tab on each rebuild. Recreating
            // re-parented and re-rasterized the whole strip (incl. the active tab's drop shadow), which blinked.
            foreach (var gone in _tabButtonCache.Keys.Where(k => !docTabs.Contains(k)).ToList())
                _tabButtonCache.Remove(gone);

            _tabButtonList.Clear();
            foreach (var t in docTabs)
            {
                if (!_tabButtonCache.TryGetValue(t, out var btn))
                {
                    btn = BuildTabButton(t);
                    _tabButtonCache[t] = btn;
                }
                else RefreshTabButton(btn, t);    // reused: update title / dirty / active styling in place

                btn.RenderTransform = null;        // drop any leftover drag-slide offset
                btn.Width = double.NaN;            // measure at natural width; ReflowTabs sets the final width
                RestoreTabLabelWidth(btn);
                btn.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _tabButtonList.Add((t, btn, btn.DesiredSize.Width));
            }

            // Place buttons on the strip in order without a full clear, then reflow widths / overflow.
            SetTabStripChildren([.. _tabButtonList.Select(t => t.btn)]);
            UpdateTabStripFade();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)ReflowTabs);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
        }

        // Debounced from TabStripBorder.SizeChanged so we don't reflow (two layout passes) every resize tick.
        private void ScheduleTabReflow()
        {
            if (_tabReflowTimer is null)
            {
                _tabReflowTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(30) };   // cheap reflow now (reconcile, no teardown), so track resize closely
                _tabReflowTimer.Tick += (_, _) => { _tabReflowTimer!.Stop(); ReflowTabs(); };
            }
            _tabReflowTimer.Stop();
            _tabReflowTimer.Start();
        }

        // Lays the tabs out to measure them, then keeps as many as fit (left to right) on the strip and
        // moves the rest into a "N more" dropdown button at the end. The active tab is always kept visible.
        // The title TextBlock lives at bd.Child(Grid) -> DockPanel -> TextBlock. Squishing a tab clears its
        // reserved bold width so the title trims with an ellipsis; natural-width tabs restore the reserve.
        private static TextBlock? TabLabelOf(FrameworkElement tabBtn)
            => ((tabBtn as Border)?.Child as Grid)?.Children.OfType<DockPanel>().FirstOrDefault()
                 ?.Children.OfType<TextBlock>().FirstOrDefault();
        private static void TrimTabLabel(FrameworkElement tabBtn)
        { if (TabLabelOf(tabBtn) is { } lb) lb.MinWidth = 0; }
        private static void RestoreTabLabelWidth(FrameworkElement tabBtn)
        { if (TabLabelOf(tabBtn) is { } lb && lb.Tag is double r) lb.MinWidth = r; }

        // Equal-width tabs (Chrome/VS style): every tab shares the available width, clamped between a min and
        // a max, and titles ellipsis-trim. This removes per-tab width juggling and the activation jitter, and
        // pairs with SetTabStripChildren so resize / switch / drag-release reconcile in place without a blink.
        private void ReflowTabs()
        {
            if (_reflowingTabs || TabScroll is null || TabStrip is null) return;
            if (_tabButtonList.Count == 0) return;
            _reflowingTabs = true;
            try
            {
                double viewport = TabScroll.ViewportWidth;
                if (viewport <= 1) return;   // not laid out yet

                int n = _tabButtonList.Count;
                const double minTab = 110;   // narrowest a tab gets before tabs roll into the overflow menu
                const double maxTab = 220;   // widest a tab grows to (matches the Border MaxWidth)
                const double moreW  = 40;    // the "N v" overflow button

                // Everything fits at >= minTab: one shared width for all, capped at maxTab.
                if (n * minTab <= viewport)
                {
                    double w = Math.Min(maxTab, viewport / n);
                    foreach (var (_, btn, _) in _tabButtonList) { btn.Width = w; TrimTabLabel(btn); }
                    SetTabStripChildren([.. _tabButtonList.Select(t => t.btn)]);
                    return;
                }

                // Too many to fit at minTab: show as many equal-width tabs as fit, rest into the dropdown.
                double budget = viewport - moreW;
                int    nFit   = Math.Max(1, (int)(budget / minTab));
                double tabW   = Math.Min(maxTab, budget / nFit);

                var visible  = _tabButtonList.Take(nFit).ToList();
                var overflow = _tabButtonList.Skip(nFit).ToList();
                // Active tab must stay on the strip: if it overflowed, swap it for the last visible tab.
                if (_active != null && overflow.Any(t => t.s == _active))
                {
                    var act = overflow.First(t => t.s == _active);
                    overflow.Remove(act);
                    if (visible.Count > 0)
                    {
                        var last = visible[^1];
                        visible.RemoveAt(visible.Count - 1);
                        overflow.Insert(0, last);
                    }
                    visible.Add(act);
                }

                foreach (var (_, btn, _) in visible) { btn.Width = tabW; TrimTabLabel(btn); }
                var children = visible.Select(t => t.btn).ToList();
                children.Add(BuildTabOverflowButton(overflow));
                SetTabStripChildren(children);
            }
            finally { _reflowingTabs = false; }
        }

        // The "N v" dropdown at the end of the strip. Clicking it lists the overflowed tabs; choosing one
        // switches to it (the next reflow then pulls it onto the strip and pushes another into the menu).
        private FrameworkElement BuildTabOverflowButton(List<(DocumentSession s, FrameworkElement btn, double natW)> overflow)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = overflow.Count.ToString(),
                FontFamily = UiKit.UiFont, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 5, 0),
            });
            content.Children.Add(new TextBlock
            {
                Text = "",   // Segoe MDL2 chevron-down
                FontFamily = UiKit.IconFont, FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var btn = new Button
            {
                Content = content,
                Height = 22,
                Margin = new Thickness(2, 3, 0, 0),
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = $"{overflow.Count} more tab(s)",
                Style = (Style)FindResource("TabNewButton"),
            };

            var menu = new ContextMenu();
            foreach (var (s, _, _) in overflow)
            {
                var sess = s;
                var item = new MenuItem { Header = (s.IsDirty ? "• " : "") + s.Title };
                item.Click += (_, _) => SwitchToTab(sess);
                menu.Items.Add(item);
            }
            btn.Click += (_, _) => { menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; };
            return btn;
        }

        // Active vs inactive tab visuals in one place, so a tab switch can restyle the existing buttons in
        // place (RestyleActiveTab) rather than tearing down and rebuilding the whole strip, which flickered.
        // Both branches set every property the other touches so the state fully toggles on a live button.
        private void ApplyTabButtonState(Border bd, TextBlock? label, bool active)
        {
            if (active)
            {
                // Front tab: takes the document PANE background (BgCanvas) with a themed accent stripe along
                // the TOP edge and a soft drop shadow, so it reads as raised and blends into the pane below.
                bd.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
                bd.BorderThickness = new Thickness(0, 3, 0, 0);
                bd.SetResourceReference(Border.BorderBrushProperty, "SelectionBg");
                bd.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.45 };
                Panel.SetZIndex(bd, 2);
            }
            else
            {
                // Opaque (matches the strip) with a single beveled trailing divider, no shadow.
                bd.SetResourceReference(Border.BackgroundProperty, "BgSidebar");
                bd.BorderThickness = new Thickness(0, 0, 1, 0);
                bd.BorderBrush = MakeTabDividerBrush();
                bd.Effect = null;
                Panel.SetZIndex(bd, 0);
            }
            if (label is null) return;
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextPrimary" : "TextSecondary");
            if (active)
            {
                label.FontWeight = FontWeights.Bold;
                label.RenderTransform = new TranslateTransform(0, -2);   // active title otherwise sits a hair low
                // Title shadow strength from the theme's HeaderShadowOpacity (heavier on Blood/Greed/Cyanotic,
                // off in Light where a dark shadow muddies the pale tab). Re-resolved on every restyle/theme change.
                double op = TryFindResource("HeaderShadowOpacity") is double hso ? hso : 0.5;
                label.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = op };
            }
            else
            {
                label.FontWeight = FontWeights.Normal;
                label.RenderTransform = null;
                label.Effect = null;
            }
        }

        // Repaint active/inactive styling on the EXISTING tab buttons (no teardown), then pull the active tab
        // onto the strip if it was parked in the overflow dropdown. Used on a plain switch to avoid the flicker.
        private void RestyleActiveTab()
        {
            foreach (var (s, btn, _) in _tabButtonList)
                if (btn is Border b) ApplyTabButtonState(b, TabLabelOf(b), s == _active);
            var activeBtn = _tabButtonList.FirstOrDefault(t => t.s == _active).btn;
            if (activeBtn != null && TabStrip != null && !TabStrip.Children.Contains(activeBtn))
                ReflowTabs();
        }

        private FrameworkElement BuildTabButton(DocumentSession s)
        {
            bool active = s == _active;

            var bd = new Border
            {
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Margin = new Thickness(0, 3, 1, 0),
                Padding = new Thickness(12, 4, 5, 5),
                MinWidth = 0,         // ReflowTabs sets every tab's width explicitly (equal-width); don't fight it
                MaxWidth = 220,
                Cursor = Cursors.Hand,
                Tag = s,
                ToolTip = s.OriginalFile ?? "Untitled",
            };
            // Active vs inactive visuals (background, top accent stripe, shadow, title weight) are applied by
            // ApplyTabButtonState at the end, so a plain tab switch can restyle the existing buttons in place
            // instead of rebuilding the whole strip (the rebuild flickered).

            // DockPanel (not StackPanel): the close button is docked right so it stays pinned and fully
            // visible, while the label fills the remaining space and trims with an ellipsis. With a
            // StackPanel the close button got pushed off the edge and clipped when tabs were squished.
            var sp = new DockPanel { LastChildFill = true };

            var label = new TextBlock
            {
                Text = (s.IsDirty ? "• " : "") + s.Title,
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            // (foreground / weight / title shadow are set by ApplyTabButtonState at the end.)
            // Reserve the BOLD width on EVERY tab so activating one (which bolds its title) never widens
            // the tab and shoves its neighbours. Inactive tabs render normal weight but still claim the
            // bold width via MinWidth; capped so squished tabs still ellipsis-trim instead of clipping.
            double tabDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var boldFt = new FormattedText(label.Text,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                label.FontSize, Brushes.Black, tabDip);
            label.MinWidth = Math.Min(183, Math.Ceiling(boldFt.Width) + 1);
            label.Tag = label.MinWidth;   // reserved bold width; ReflowTabs clears it when a tab is squished so the title ellipsis-trims instead of clipping

            var close = new Button
            {
                Content = "",
                FontFamily = UiKit.IconFont,
                FontSize = 9,
                Width = 16,
                Height = 16,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Close tab",
                Style = (Style)FindResource("TabCloseButton"),
            };
            close.Click += (_, e) => { CloseTab(s); };

            DockPanel.SetDock(close, Dock.Right);
            sp.Children.Add(close);   // docked right first, stays pinned
            sp.Children.Add(label);   // fills the rest, trims with ellipsis

            // Every tab is opaque, so paint the strip's grain back onto it to keep the texture, while
            // ensuring the shadow gradient behind the strip never shows ON a tab (only in empty space).
            var content = new Grid();
            var grain = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = FindResource("GrainBrushShared") as Brush,
                // Bleed over the tab padding (10,4,5,5) so the texture reaches the tab edges.
                Margin = new Thickness(-10, -4, -5, -5),
            };
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            content.Children.Add(grain);
            content.Children.Add(sp);
            bd.Child = content;
            ApplyTabButtonState(bd, label, active);

            // Middle-click closes the tab (common tabbed-UI convention). Left-click switches on mouse-UP
            // (see the drag handlers below) so a press can begin a drag without first rebuilding the strip.
            bd.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CloseTab(s); } };

            // Right-click: tab actions.
            bd.MouseRightButtonUp += (_, ev) =>
            {
                var menu = MakeThemedMenu();
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CloseTab"), (_, _) => CloseTab(s)));
                var others = MakeMenuItem(Loc("Str_Ctx_CloseOthers"), (_, _) => CloseOtherTabs(s));
                others.IsEnabled = _sessions.Count(z => z.Doc != null || z.DeferredPath != null) > 1;
                menu.Items.Add(others);
                menu.PlacementTarget = bd;
                menu.IsOpen = true;
                ev.Handled = true;
            };

            // Live drag-reorder: arm on press, and once past the threshold slide this tab between its
            // neighbours in real time as the cursor crosses their midpoints (a plain click still switches).
            // The Border is moved within TabStrip.Children directly - no rebuild mid-drag - so the mouse
            // capture survives. A clean RebuildTabStrip on release re-applies widths / overflow / styling.
            bd.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (close.IsMouseOver) return;   // let the close button handle its own click
                _tabDragSession = s;
                _tabDragStart = e.GetPosition(TabStrip);
                _tabGrabDX = e.GetPosition(bd).X;   // cursor offset within the tab, so it tracks under the grab point
                _tabDragging = false;
                bd.CaptureMouse();
                // Own the press entirely so it can't bubble to the title-bar's window-drag handler, and so
                // the mouse capture (not the caption hit-test) drives the drag - making it Y-independent.
                e.Handled = true;
            };
            bd.PreviewMouseMove += (_, e) =>
            {
                if (!bd.IsMouseCaptured || _tabDragSession is null) return;
                double x = e.GetPosition(TabStrip).X;
                if (!_tabDragging && Math.Abs(x - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
                _tabDragging = true;
                // Keep the grabbed tab UNDER the active tab but OVER other inactive tabs, the same in both drag
                // directions (raw child-index stacking would otherwise flip it front/back as it's reordered).
                Panel.SetZIndex(bd, s == _active ? 3 : 1);
                int curIdx = TabStrip.Children.IndexOf(bd);
                if (curIdx < 0) return;
                double slide = bd.ActualWidth + bd.Margin.Left + bd.Margin.Right;
                double rawLeft = x - _tabGrabDX;                 // grabbed tab's content-left, following the cursor
                // Detection uses the dragged tab's ADVANCING edge (unclamped), so even a wide tab reaches a narrow
                // neighbour's slot at the edge. The RENDER position is clamped so it never visually leaves the strip.
                double leftEdge  = rawLeft;                      // leading edge when dragging left
                double rightEdge = rawLeft + bd.ActualWidth;     // leading edge when dragging right
                double maxLeft = Math.Max(0, TabStrip.ActualWidth - slide);
                double renderLeft = Math.Min(Math.Max(0, rawLeft), maxLeft);

                // Swap when the ADVANCING edge crosses a neighbour's LAYOUT-slot midpoint: dragging right, the tab's
                // RIGHT edge past the right neighbour's midpoint; dragging left, its LEFT edge past the left one's.
                // Works for any width and the edge-vs-edge gap gives natural hysteresis (no bounce). Layout slots
                // ignore any in-flight slide transform.
                bool swapped = false;
                if (curIdx + 1 < TabStrip.Children.Count && TabStrip.Children[curIdx + 1] is FrameworkElement right
                    && rightEdge > LayoutMidX(right))
                {
                    TabStrip.Children.RemoveAt(curIdx + 1);
                    TabStrip.Children.Insert(curIdx, right);
                    ResyncSessionsFromTabStrip();
                    AnimateTabSlide(right, slide);    // it jumped left over the dragged tab; glide it in from the right
                    swapped = true;
                }
                else if (curIdx - 1 >= 0 && TabStrip.Children[curIdx - 1] is FrameworkElement left
                    && leftEdge < LayoutMidX(left))
                {
                    TabStrip.Children.RemoveAt(curIdx - 1);
                    TabStrip.Children.Insert(curIdx, left);
                    ResyncSessionsFromTabStrip();
                    AnimateTabSlide(left, -slide);    // it jumped right over the dragged tab; glide it in from the left
                    swapped = true;
                }

                // Pin the grabbed tab at its clamped render position. After a swap its slot moves by a neighbour's
                // width; refresh layout first so the new slot is current, then offset bd back to renderLeft.
                if (swapped) TabStrip.UpdateLayout();
                var bslot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(bd);
                SetTabOffsetX(bd, renderLeft - (bslot.X + bd.Margin.Left));
            };
            bd.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (!bd.IsMouseCaptured) return;   // close-button press (we never captured) - leave it alone
                bd.ReleaseMouseCapture();
                bool wasDragging = _tabDragging;
                _tabDragSession = null;
                _tabDragging = false;
                if (wasDragging)
                {
                    // Settle the grabbed tab from its dragged offset into its final slot, then rebuild cleanly
                    // (RebuildTabStrip re-applies widths / overflow / active styling and clears the transform).
                    if (bd.RenderTransform is TranslateTransform stt && Math.Abs(stt.X) > 0.5)
                    {
                        var settle = new System.Windows.Media.Animation.DoubleAnimation(0,
                            new Duration(TimeSpan.FromMilliseconds(120)))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase
                            {
                                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                            },
                        };
                        settle.Completed += (_, _) => RebuildTabStrip();
                        stt.BeginAnimation(TranslateTransform.XProperty, settle);
                    }
                    else RebuildTabStrip();
                }
                else SwitchToTab(s);                  // it was a click, not a drag
            };

            return bd;
        }

        // Live drag-reorder state.
        private Point _tabDragStart;
        private double _tabGrabDX;          // cursor offset within the grabbed tab, to keep it under the grab point
        private DocumentSession? _tabDragSession;
        private bool _tabDragging;

        // Set a tab's horizontal offset immediately (no animation) - used to glue the grabbed tab to the cursor.
        private static void SetTabOffsetX(FrameworkElement tab, double x)
        {
            if (tab.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty, null);   // drop any prior animation so the set sticks
            tt.X = x;
        }

        // Midpoint X of a tab's LAYOUT slot in TabStrip coordinates (ignores any in-flight slide transform).
        private static double LayoutMidX(FrameworkElement fe)
        {
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            return slot.X + slot.Width / 2;
        }

        // Slide a just-reordered tab from where it was into its new slot, so a swap reads as a smooth glide
        // instead of an instant jump. Z-order is left to how the strip was built: the active tab stays raised
        // and glides in front of its neighbours; an inactive dragged tab slides under them. A clean
        // RebuildTabStrip on drop clears these transient transforms.
        private static void AnimateTabSlide(FrameworkElement tab, double fromX)
        {
            if (tab.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            var anim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0,
                new Duration(TimeSpan.FromMilliseconds(140)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            };
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        // Rebuild the session order from the tab strip's current visual order (each tab Border's Tag is its
        // session); sessions not currently shown (overflow) keep their order after the visible ones.
        private void ResyncSessionsFromTabStrip()
        {
            var vis = new List<DocumentSession>();
            foreach (var child in TabStrip.Children)
                if (child is FrameworkElement fe && fe.Tag is DocumentSession ds) vis.Add(ds);
            if (vis.Count == 0) return;
            var rest = _sessions.Where(z => !vis.Contains(z)).ToList();
            _sessions.Clear();
            _sessions.AddRange(vis);
            _sessions.AddRange(rest);
        }

        // Tab divider: the document-pane border color (PaneBorder) at the bottom, fading to fully
        // transparent at the top - a subtle separator that matches the doc-pane line on every theme.
        // Rebuilt on theme change (OnThemeChanged).
        private Brush MakeTabDividerBrush()
        {
            Color pane = (FindResource("PaneBorder") as SolidColorBrush)?.Color ?? Color.FromRgb(0x2e, 0x2e, 0x2e);
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, pane.R, pane.G, pane.B), 0.0));    // transparent, top
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, pane.R, pane.G, pane.B), 0.45));   // still transparent through the upper half
            brush.GradientStops.Add(new GradientStop(pane, 1.0));                                         // PaneBorder, bottom
            return brush;
        }

        private FrameworkElement BuildNewTabButton()
        {
            var b = new Button
            {
                Content = "",
                FontFamily = UiKit.IconFont,
                FontSize = 11,
                Width = 24,
                Height = 22,
                Margin = new Thickness(2, 3, 0, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Open another PDF in a new tab",
                Style = (Style)FindResource("TabNewButton"),
            };
            b.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF" };
                if (dlg.ShowDialog(this) == true) OpenInNewTab(dlg.FileName);
            };
            return b;
        }
    }
}
