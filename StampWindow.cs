using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace StealthPDF
{
    /// <summary>
    /// Combined "Stamp" tool, modeled on the Transform window: a live page preview on the left and an
    /// options sidebar on the right with two independent, toggleable sections - Page Numbers and
    /// Watermark (text or image). Apply hands a StampSpec back to the caller, which places the stamps on
    /// the editable stamp layer. Re-opening (double-click a stamp) seeds the window from the saved spec.
    /// </summary>
    internal sealed class StampWindow : Window
    {
        public bool Applied { get; private set; }
        public StampSpec Result { get; private set; }

        private BitmapSource _pageSrc;
        private double _pageWpt, _pageHpt;
        private readonly int _pageCount;
        private int _pageIndex;
        private readonly StampSpec _spec;
        // Renders an arbitrary page for the preview stepper: returns that page's bitmap + size in points.
        private readonly Func<int, (BitmapSource? src, double wpt, double hpt)>? _pageProvider;
        private TextBlock _pageNavLabel = null!;
        private Button _prevArrow = null!, _nextArrow = null!;
        private System.Windows.Threading.DispatcherTimer? _navRenderTimer;

        private readonly Image _preview = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.4 }
        };
        private readonly Canvas _overlay = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private FrameworkElement _previewArea = null!;
        private Button _applyBtn = null!;
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer;

        // Page-number controls
        private CheckBox _numEnable = null!;
        private CheckBox _numMirror = null!;
        private TextBox _numStart = null!, _numFormat = null!, _numSize = null!, _numRange = null!;
        private ComboBox _numPos = null!;
        private Border _numSwatch = null!;
        private Color _numColor;
        private StackPanel _numBody = null!;

        // Watermark controls
        private CheckBox _wmEnable = null!;
        private RadioButton _wmTextRadio = null!, _wmImageRadio = null!;
        private TextBox _wmText = null!, _wmSize = null!, _wmRange = null!;
        private ComboBox _wmPos = null!, _wmFont = null!;
        private Slider _wmAngle = null!, _wmOpacity = null!, _wmScale = null!;
        private Border _wmSwatch = null!;
        private Color _wmColor;
        private string? _wmImagePath;
        private BitmapImage? _wmImageSrc;
        private TextBlock _wmImageLabel = null!;
        private StackPanel _wmBody = null!, _wmTextPanel = null!, _wmImagePanel = null!;

        // Accordion state: only one section body is expanded at a time so the sidebar never overflows.
        // Each entry is (body panel, chevron). ExpandSection() collapses all but the requested one.
        private readonly List<(StackPanel body, TextBlock chevron)> _sections = new();
        private Dictionary<StackPanel, Button>? _sectionChevronBtns;
        private TextBlock _certChevron = null!, _wmChevron = null!, _numChevron = null!;

        // Certification controls
        private CheckBox _certEnable = null!;
        private CheckBox _certShowLogo = null!, _certShowName = null!, _certShowSig = null!, _certShowDate = null!, _certShowTime = null!;
        private TextBox _certLabel = null!, _certName = null!, _certDate = null!, _certRange = null!;
        private ComboBox _certSigPicker = null!, _certPos = null!, _certBorder = null!;
        private Slider _certScale = null!, _certLogoScale = null!;
        private Border _certSwatch = null!;
        private Color _certColor;
        private string? _certLogoPath, _certSigPath;
        private TextBlock _certLogoLabel = null!;
        private StackPanel _certBody = null!;
        private readonly MainWindow _owner;
        private readonly Services.SignatureStore _sigStore;
        // (a) Live cert preview + resize: the rendered block, its logo bitmap, and its corner resize handle.
        private BitmapSource? _certLogoSrc;
        private Border? _certPreviewBlock;
        // (b) Calendar popup for the date field.
        private System.Windows.Controls.Primitives.Popup _certDatePopup = null!;
        private System.Windows.Controls.Calendar _certCalendar = null!;
        // (c) Page-selection combo (All / Current / Custom range).
        private ComboBox _certPageMode = null!;
        private const int CertPageAll = 0, CertPageCurrent = 1, CertPageCustom = 2;
        // (d) Named presets: dropdown + Save/Rename/Delete.
        private ComboBox _certPresetCombo = null!;
        private readonly Services.CertStampStore _certStore = new();
        // Cached current-page-as-range string, recomputed when the page stepper moves.
        private string _certCurrentPageRange => $"{_pageIndex + 1}";

        private readonly Style? _darkSlider, _darkCombo;

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;

        // (resource key, horizontal 0/1/2, vertical 0 top / 1 middle / 2 bottom)
        private static readonly (string key, int h, int v)[] Positions =
        [
            ("Str_Pos_BottomCenter", 1, 2), ("Str_Pos_BottomRight", 2, 2), ("Str_Pos_BottomLeft", 0, 2),
            ("Str_Pos_TopCenter", 1, 0), ("Str_Pos_TopRight", 2, 0), ("Str_Pos_TopLeft", 0, 0),
            ("Str_Pos_Center", 1, 1), ("Str_Pos_Custom", -1, -1)
        ];

        public StampWindow(Window owner, BitmapSource pageSrc, double pageWpt, double pageHpt,
                           int pageCount, int pageIndex, StampSpec? existing,
                           Func<int, (BitmapSource? src, double wpt, double hpt)>? pageProvider = null)
        {
            _pageSrc = pageSrc;
            _pageWpt = pageWpt;
            _pageHpt = pageHpt;
            _pageCount = pageCount;
            _pageIndex = pageIndex;
            _pageProvider = pageProvider;
            _spec = existing?.Clone() ?? new StampSpec { NumbersEnabled = true };
            Result = _spec;

            _owner = owner as MainWindow ?? throw new InvalidOperationException("StampWindow requires a MainWindow owner");
            _sigStore = _owner._signatureStore;

            // NOTE: DialogChrome.BuildTitleBar looks for the literal substring "StealthPDF" in the
            // window title as a sentinel and swaps it for the styled Stealth+PDF wordmark. Do NOT
            // change/remove "StealthPDF -" here or the title bar will render as plain text.
            Title = "StealthPDF - " + S("Str_Stamp_Suffix");
            Width = 980;
            Height = 620;
            MinWidth = 680;
            MinHeight = 480;
            DialogChrome.Configure(this, owner, resizable: true);

            _darkSlider = owner.TryFindResource("DarkSlider") as Style;
            _darkCombo  = owner.TryFindResource("DarkComboBox") as Style;

            // Borrow the main window's themed scrollbar so the sidebar scroller isn't the OS-white default.
            if (owner.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style sbStyle)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sbStyle;

            _numColor = _spec.NumColor;
            _wmColor  = _spec.WmColor;
            _wmImagePath = _spec.WmImagePath;
            _certColor = _spec.CertColor;
            _certLogoPath = _spec.CertLogoPath;
            _certSigPath = _spec.CertSigPath;
            BuildUi(owner);
            LoadWatermarkImage();
            LoadCertLogoImage();
            UpdateEnabledStates();

            _previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            // Guarded: any surprise in the preview (bad drag state, half-loaded image, unexpected
            // measure/arrange condition) must NOT kill the tool. Fall through to a soft status message
            // and keep the dialog usable so the user can still commit or reset.
            _previewTimer.Tick += (_, _2) =>
            {
                _previewTimer.Stop();
                try { RenderPreview(); }
                catch (Exception ex)
                {
                    try { _overlay.Children.Clear(); } catch { }
                    try { SetStatusMsg("Stamp preview error: " + ex.Message); } catch { }
                }
            };
            // Expand the first enabled section (or the first section if none are enabled yet) so the
            // sidebar starts compact â€” one body visible â€” instead of all three overflowing the window.
            var firstOn = _certEnable.IsChecked == true ? _certBody
                        : _wmEnable.IsChecked   == true ? _wmBody
                        : _numEnable.IsChecked   == true ? _numBody
                        : _certBody;
            ExpandSection(firstOn);
            RenderPreview();
        }

        private void Schedule() { _previewTimer.Stop(); _previewTimer.Start(); }

        private void BuildUi(Window owner)
        {
            var root = new DockPanel();

            // ---- Right sidebar ----
            // Small right padding so the always-on scrollbar tucks near the window edge; the footer and
            // scrolled content get their own right inset so nothing sits under the bar.
            var sidebar = new Border { Width = 300, Background = Brushes.Transparent, Padding = new Thickness(16, 8, 4, 14) };
            DockPanel.SetDock(sidebar, Dock.Right);
            var side = new DockPanel();

            // Docked footer: Reset all link above a right-aligned Cancel / Apply row. Right inset keeps the
            var stack = new StackPanel();
            stack.Children.Add(BuildCertificationSection());
            stack.Children.Add(Divider());
            stack.Children.Add(BuildWatermarkSection());
            stack.Children.Add(Divider());
            stack.Children.Add(BuildNumbersSection());
            // buttons off the reserved scrollbar gutter.
            var bottom = new StackPanel { Margin = new Thickness(0, 10, 12, 0) };
            var resetLink = UiKit.LinkLabel(S("Str_Tf_ResetAll"), ResetAll);
            resetLink.Margin = new Thickness(0, 0, 0, 8);
            bottom.Children.Add(resetLink);
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = UiKit.Make(S("Str_Tf_Cancel"), false);
            cancelBtn.Click += (_, _2) => { Applied = false; Close(); };
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            actionRow.Children.Add(cancelBtn);
            _applyBtn = UiKit.Make(S("Str_Tf_Apply"), true);
            _applyBtn.Click += (_, _2) => CommitAndClose();
            actionRow.Children.Add(_applyBtn);
            bottom.Children.Add(actionRow);
            DockPanel.SetDock(bottom, Dock.Bottom);
            side.Children.Add(bottom);


            // Scrollbar is ALWAYS reserved (Visible, not Auto) so the content never shifts left when it
            // appears. This is the rule for these sidebar windows.
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, Content = stack };
            side.Children.Add(scroller);
            sidebar.Child = side;
            root.Children.Add(sidebar);

            // ---- Left preview ----
            var previewWrap = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(8, 4, 8, 12),
                ClipToBounds = true
            };
            previewWrap.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
            previewWrap.SetResourceReference(Border.BorderBrushProperty, "PaneBorder");

            // The page image (row 0) and the stepper (row 1) live in separate rows so the stepper sits
            // BELOW the page instead of overlapping it - same row layout as the print preview.
            var previewLayout = new Grid();
            previewLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var imageHost = new Grid();
            AddGrain(imageHost, owner, 0.05, cornerRadius: 0);
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            _preview.Source = _pageSrc;
            imageHost.Children.Add(_preview);
            imageHost.Children.Add(_overlay);
            Grid.SetRow(imageHost, 0);
            previewLayout.Children.Add(imageHost);
            previewWrap.Child = previewLayout;
            _previewArea = imageHost;   // size the page against the image row only, never the stepper row
            // Page stepper: the wheel over the preview (or the arrows) walks pages so you can preview the
            // stamp on any page - the per-page number and range checks update as you go. Only when the caller
            // supplies a page provider and there's more than one page.
            if (_pageProvider != null && _pageCount > 1)
            {
                var nav = BuildPageNav();
                Grid.SetRow(nav, 1);
                previewLayout.Children.Add(nav);
                previewWrap.PreviewMouseWheel += (_, e) =>
                {
                    int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
                    StepPage(e.Delta < 0 ? notches : -notches);
                    e.Handled = true;
                };
            }
            _previewArea.SizeChanged += (_, _2) => { SizePreviewImage(); Schedule(); };
            root.Children.Add(previewWrap);

            // "StealthPDF - ..." is the DialogChrome wordmark sentinel (renders as the styled Stealth+PDF logo).
            Content = DialogChrome.Frame(this, Owner, "StealthPDF - " + S("Str_Stamp_Suffix"), () => { Applied = false; Close(); }, root);

            // Esc-to-close is wired by DialogChrome.Frame; Enter commits.
            KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitAndClose(); };
        }

        private static void AddGrain(Grid host, Window owner, double fallback, double cornerRadius)
        {
            var grain = (owner as MainWindow)?.GrainTexture;
            if (grain == null) return;
            double op = Application.Current.Resources["GrainOpacity"] is double go ? go : fallback;
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(cornerRadius), IsHitTestVisible = false, Opacity = op,
                Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
            });
        }

        // ---------- Page Numbers section ----------
        private FrameworkElement BuildNumbersSection()
        {
            var wrap = new StackPanel();
            _numBody = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            _numEnable = SectionToggle(S("Str_Stamp_SecNumbers"), _spec.NumbersEnabled);
            _numEnable.Checked   += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            wrap.Children.Add(SectionHeaderRow(_numEnable, _numBody, out _numChevron));
            _numEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };

            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_StartAt")));
            _numStart = UiKit.Field();
            _numStart.Text = _spec.StartNumber.ToString();
            _numStart.Margin = new Thickness(0, 0, 0, 8);
            _numStart.TextChanged += (_, _2) => Schedule();
            _numBody.Children.Add(_numStart);

            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Format")));
            _numFormat = UiKit.Field();
            _numFormat.Text = _spec.Format;
            _numFormat.TextChanged += (_, _2) => Schedule();
            _numBody.Children.Add(_numFormat);
            _numBody.Children.Add(new TextBlock { Text = S("Str_Stamp_Hint"), Foreground = R("TextSecondary"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            _numBody.Children.Add(new TextBlock { Text = S("Str_Stamp_Hint2"), Foreground = R("TextSecondary"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

            _numBody.Children.Add(SliderBoxRow(S("Str_Stamp_FontSize"), 6, 96, _spec.NumFontPt, out _, out _numSize));

            _numBody.Children.Add(ColorRow(S("Str_Stamp_Color"), _numColor, out _numSwatch, c => { _numColor = c; Schedule(); }));

            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Pages")));
            _numRange = UiKit.Field();
            _numRange.Text = _spec.NumRange;
            _numRange.ToolTip = S("Str_Crop_RangeTip");
            _numRange.Margin = new Thickness(0, 0, 0, 8);
            _numRange.TextChanged += (_, _2) => Schedule();
            _numBody.Children.Add(_numRange);

            // Position is the last page-number option (it's the least-changed setting).
            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Position")));
            _numPos = MakePosCombo(_spec.NumPosH, _spec.NumPosV);
            _numPos.SelectionChanged += (_, _2) => { UpdateMirrorEnabled(); Schedule(); };
            _numBody.Children.Add(_numPos);

            _numMirror = UiKit.CheckBox(S("Str_Stamp_Mirror"));
            _numMirror.IsChecked = _spec.NumMirror;
            _numMirror.Margin = new Thickness(0, 6, 0, 0);
            _numMirror.Checked   += (_, _2) => Schedule();
            _numMirror.Unchecked += (_, _2) => Schedule();
            _numBody.Children.Add(_numMirror);
            UpdateMirrorEnabled();

            wrap.Children.Add(_numBody);
            return wrap;
        }

        private FrameworkElement BuildCertificationSection()
        {
            var wrap = new StackPanel();
            _certBody = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            _certEnable = SectionToggle(S("Str_Stamp_SecCert"), _spec.CertEnabled);
            _certEnable.Checked   += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            wrap.Children.Add(SectionHeaderRow(_certEnable, _certBody, out _certChevron));
            _certEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };

            // (d) Preset dropdown + Save / Rename / Delete, at the top of the section so it seeds everything below.
            _certBody.Children.Add(BuildCertPresetRow());

            // Logo (toggle + Choose button + filename).
            _certEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };

            // Logo (toggle + Choose button + filename).
            _certShowLogo = UiKit.CheckBox(S("Str_Stamp_CertLogo"));
            _certShowLogo.IsChecked = _spec.CertShowLogo;
            _certShowLogo.Checked   += (_, _2) => Schedule();
            _certShowLogo.Unchecked += (_, _2) => Schedule();
            _certBody.Children.Add(_certShowLogo);
            var logoRow = new DockPanel { Margin = new Thickness(24, 2, 0, 8) };
            var logoBtn = UiKit.Make(S("Str_Stamp_ChooseImage"), false);
            logoBtn.Click += (_, _2) => ChooseCertLogo();
            DockPanel.SetDock(logoBtn, Dock.Right);
            logoRow.Children.Add(logoBtn);
            _certLogoLabel = new TextBlock { Text = System.IO.Path.GetFileName(_certLogoPath ?? ""), Foreground = R("TextSecondary"), FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            logoRow.Children.Add(_certLogoLabel);
            _certBody.Children.Add(logoRow);

            // Label + Name.
            _certBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_CertLabel")));
            _certLabel = UiKit.Field();
            _certLabel.Text = _spec.CertLabel;
            _certLabel.Margin = new Thickness(0, 0, 0, 8);
            _certLabel.TextChanged += (_, _2) => Schedule();
            _certBody.Children.Add(_certLabel);
            _certShowName = UiKit.CheckBox(S("Str_Stamp_CertName"));
            _certShowName.IsChecked = _spec.CertShowName;
            _certShowName.Checked   += (_, _2) => Schedule();
            _certShowName.Unchecked += (_, _2) => Schedule();
            _certBody.Children.Add(_certShowName);
            _certName = UiKit.Field();
            _certName.Text = _spec.CertName;
            _certName.Margin = new Thickness(24, 2, 0, 8);
            _certName.TextChanged += (_, _2) => Schedule();
            _certBody.Children.Add(_certName);
            AddCertificationSectionTail();

            wrap.Children.Add(_certBody);
            return wrap;

        }

        // Second half of the certification section: signature picker, date/time, appearance, placement.
        private void AddCertificationSectionTail()
        {
            _certShowSig = UiKit.CheckBox(S("Str_Stamp_CertSig"));
            _certShowSig.IsChecked = _spec.CertShowSig;
            _certShowSig.Checked   += (_, _2) => { PopulateCertSigPicker(); Schedule(); };
            _certShowSig.Unchecked += (_, _2) => Schedule();
            _certBody.Children.Add(_certShowSig);
            var sigRow = new DockPanel { Margin = new Thickness(24, 2, 0, 4) };
            _certSigPicker = new ComboBox { Height = 26, MaxDropDownHeight = 320, MinWidth = 120 };
            if (_darkCombo != null) _certSigPicker.Style = _darkCombo; else { _certSigPicker.Background = R("BgCanvas"); _certSigPicker.Foreground = R("TextPrimary"); }
            PopulateCertSigPicker();
            _certSigPicker.SelectionChanged += (_, _2) => { OnCertSigPicked(); Schedule(); };
            DockPanel.SetDock(_certSigPicker, Dock.Left);
            sigRow.Children.Add(_certSigPicker);
            var sigImgBtn = UiKit.Make(S("Str_Stamp_CertChooseImg"), false);
            sigImgBtn.Margin = new Thickness(8, 0, 0, 0);
            sigImgBtn.Click += (_, _2) => ChooseCertSigImage();
            DockPanel.SetDock(sigImgBtn, Dock.Right);
            sigRow.Children.Add(sigImgBtn);
            _certBody.Children.Add(sigRow);

            _certShowDate = UiKit.CheckBox(S("Str_Stamp_CertDate"));
            _certShowDate.IsChecked = _spec.CertShowDate;
            _certShowDate.Checked   += (_, _2) => Schedule();
            _certShowDate.Unchecked += (_, _2) => Schedule();
            _certBody.Children.Add(_certShowDate);
            // (b) Date field + calendar popup. Clicking the field opens the calendar (so a back-dated stamp
            // can be picked visually); blank means "today", resolved at apply time.
            var dateRow = new DockPanel { Margin = new Thickness(24, 2, 0, 4) };
            _certDate = UiKit.Field();
            _certDate.Text = _spec.CertDate;
            _certDate.MinWidth = 150;
            _certDate.TextChanged += (_, _2) => Schedule();
            var dateBtn = UiKit.Make(S("Str_Stamp_CertPickDate"), false);
            dateBtn.Margin = new Thickness(6, 0, 0, 0);
            dateBtn.Click += (_, _2) => OpenCertCalendar();
            DockPanel.SetDock(dateBtn, Dock.Right);
            dateRow.Children.Add(dateBtn);
            dateRow.Children.Add(_certDate);
            _certBody.Children.Add(dateRow);
            _certCalendar = new System.Windows.Controls.Calendar { DisplayDate = TryParseCertDate(_certDate.Text, out var picked) ? picked : DateTime.Today };
            _certCalendar.SelectedDatesChanged += (_, _2) =>
            {
                if (_certCalendar.SelectedDate is { } dt)
                { _certDate.Text = dt.ToString("dd-MMM-yyyy"); _certDatePopup.IsOpen = false; Schedule(); }
            };
            var popStack = new StackPanel { Background = R("BgCanvas") };
            var calHost = new Border { Child = _certCalendar, Background = R("BgCanvas"), BorderBrush = R("BorderDim"), BorderThickness = new Thickness(1), Padding = new Thickness(4) };
            popStack.Children.Add(calHost);
            var clearBtn = UiKit.Make(S("Str_Stamp_CertClearDate"), false);
            clearBtn.Margin = new Thickness(4);
            clearBtn.Click += (_, _2) => { _certDate.Text = ""; _certDatePopup.IsOpen = false; Schedule(); };
            popStack.Children.Add(clearBtn);
            _certDatePopup = new System.Windows.Controls.Primitives.Popup { Child = popStack, PlacementTarget = _certDate, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            _certShowTime = UiKit.CheckBox(S("Str_Stamp_CertTime"));
            _certShowTime.IsChecked = _spec.CertShowTime;
            _certShowTime.Checked   += (_, _2) => Schedule();
            _certShowTime.Unchecked += (_, _2) => Schedule();
            _certBody.Children.Add(_certShowTime);
            AddCertificationSectionTail2();
        }

        private void AddCertificationSectionTail2()
        {
            var scaleRow = SliderBoxRow(S("Str_Stamp_CertScale"), 0.6, 2.5, _spec.CertScale, out var scaleSl, out _);
            _certScale = scaleSl;
            _certBody.Children.Add(scaleRow);
            var logoScaleRow = SliderBoxRow(S("Str_Stamp_CertLogoScale"), 25, 300, _spec.CertLogoScale * 100, out var logoScaleSl, out _);
            _certLogoScale = logoScaleSl;
            _certBody.Children.Add(logoScaleRow);
            _certBody.Children.Add(BorderStyleRow());
            _certBody.Children.Add(ColorRow(S("Str_Stamp_Color"), _certColor, out _certSwatch, c => { _certColor = c; Schedule(); }));
            _certBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Position")));
            _certPos = MakePosCombo(_spec.CertPosH, _spec.CertPosV);
            _certPos.SelectionChanged += (_, _2) => Schedule();
            _certBody.Children.Add(_certPos);
            // (c) Page selection: All pages / Current page / Custom range. The custom option reveals the
            // free-text range box (reusing the existing ParseRange), so a selection can target any subset.
            _certBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Pages")));
            _certPageMode = new ComboBox { Height = 26, Margin = new Thickness(0, 0, 0, 4) };
            if (_darkCombo != null) _certPageMode.Style = _darkCombo; else { _certPageMode.Background = R("BgCanvas"); _certPageMode.Foreground = R("TextPrimary"); }
            _certPageMode.Items.Add(S("Str_Stamp_CertPagesAll"));
            _certPageMode.Items.Add(S("Str_Stamp_CertPagesCurrent"));
            _certPageMode.Items.Add(S("Str_Stamp_CertPagesCustom"));
            _certPageMode.SelectedIndex = ResolveCertPageMode(_spec.CertRange);
            _certBody.Children.Add(_certPageMode);
            _certRange = UiKit.Field();
            _certRange.Text = _spec.CertRange;
            _certRange.Margin = new Thickness(0, 0, 0, 8);
            _certRange.TextChanged += (_, _2) => Schedule();
            _certBody.Children.Add(_certRange);
            UpdateCertRangeVisibility();
            _certPageMode.SelectionChanged += (_, _2) => { UpdateCertRangeVisibility(); Schedule(); };
        }

        // Maps the stored range string to a page-mode index (All=blank, Current=single page, Custom=else).
        private int ResolveCertPageMode(string range)
        {
            if (string.IsNullOrWhiteSpace(range)) return CertPageAll;
            return range.Trim() == _certCurrentPageRange ? CertPageCurrent : CertPageCustom;
        }

        // Shows the free-text range box only when "Custom range" is selected; for All/Current the range is
        // derived from the mode at commit time.
        private void UpdateCertRangeVisibility()
        {
            if (_certRange is null || _certPageMode is null) return;
            _certRange.Visibility = _certPageMode.SelectedIndex == CertPageCustom ? Visibility.Visible : Visibility.Collapsed;
        }

        // (b) Calendar popup: opens under the date field; picking a date writes it back; "Clear" resets to today.
        private void OpenCertCalendar()
        {
            if (_certDatePopup is null) return;
            _certCalendar.SelectedDate = TryParseCertDate(_certDate.Text, out var d) ? d : (DateTime?)null;
            _certCalendar.DisplayDate = TryParseCertDate(_certDate.Text, out var d2) ? d2 : DateTime.Today;
            _certDatePopup.IsOpen = true;
        }

        private static bool TryParseCertDate(string text, out DateTime result)
        {
            result = DateTime.Today;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] fmts = { "dd-MMM-yyyy", "d-MMM-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy" };
            foreach (var f in fmts)
                if (DateTime.TryParseExact(text.Trim(), f, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                    return true;
            if (DateTime.TryParse(text.Trim(), out result)) return true;
            result = DateTime.Today; return false;
        }
        // (d) Preset row: dropdown of saved presets + Save/Rename/Delete. Selecting a preset loads it.
        private FrameworkElement BuildCertPresetRow()
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(UiKit.GroupLabel(S("Str_Stamp_CertPreset")));
            _certPresetCombo = new ComboBox { Height = 26, Margin = new Thickness(0, 0, 0, 4) };
            if (_darkCombo != null) _certPresetCombo.Style = _darkCombo; else { _certPresetCombo.Background = R("BgCanvas"); _certPresetCombo.Foreground = R("TextPrimary"); }
            PopulateCertPresetCombo();
            _certPresetCombo.SelectionChanged += (_, _2) =>
            {
                if (_certPresetCombo.SelectedIndex > 0) LoadCertPresetByName(_certPresetCombo.SelectedItem as string);
            };
            row.Children.Add(_certPresetCombo);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var save = UiKit.Make(S("Str_Stamp_CertSavePreset"), false); save.Margin = new Thickness(0, 0, 6, 0);
            save.Click += (_, _2) => SaveCertPreset();
            var rename = UiKit.Make(S("Str_Stamp_CertRenamePreset"), false); rename.Margin = new Thickness(0, 0, 6, 0);
            rename.Click += (_, _2) => RenameCertPreset();
            var del = UiKit.Make(S("Str_Stamp_CertDeletePreset"), false);
            del.Click += (_, _2) => DeleteCertPreset();
            btns.Children.Add(save); btns.Children.Add(rename); btns.Children.Add(del);
            row.Children.Add(btns);
            return row;
        }

        private void PopulateCertPresetCombo()
        {
            if (_certPresetCombo is null) return;
            _certPresetCombo.Items.Clear();
            _certPresetCombo.Items.Add(S("Str_Stamp_CertSigNone"));
            foreach (var p in _certStore.LoadPresets())
                _certPresetCombo.Items.Add(p.PresetName ?? "(unnamed)");
            _certPresetCombo.SelectedIndex = 0;
        }
        private void SaveCertPreset()
        {
            var dlg = new InputDialog(S("Str_Stamp_CertPresetNameTitle"), S("Str_Stamp_CertPresetNamePrompt"),
                _certPresetCombo?.SelectedIndex > 0 ? (_certPresetCombo.SelectedItem as string ?? "") : "", this);
            dlg.ShowDialog();
            var name = dlg.Answer;
            if (string.IsNullOrEmpty(name)) { SetStatusMsg(S("Str_Stamp_CertPresetNameRequired")); return; }
            var d = HarvestCertDefaults(); d.PresetName = name;
            _certStore.SavePreset(d); PopulateCertPresetCombo(); SelectCertPresetCombo(name!);
            SetStatusMsg(string.Format(S("Str_Stamp_CertPresetSaved"), name));
        }

        private void RenameCertPreset()
        {
            if (_certPresetCombo is null || _certPresetCombo.SelectedIndex <= 0) return;
            var old = _certPresetCombo.SelectedItem as string ?? "";
            var dlg = new InputDialog(S("Str_Stamp_CertPresetRenameTitle"), S("Str_Stamp_CertPresetNamePrompt"), old, this);
            dlg.ShowDialog();
            var newName = dlg.Answer;
            if (string.IsNullOrEmpty(newName)) { SetStatusMsg(S("Str_Stamp_CertPresetNameRequired")); return; }
            _certStore.RenamePreset(old, newName!); PopulateCertPresetCombo(); SelectCertPresetCombo(newName!);
        }

        private void DeleteCertPreset()
        {
            if (_certPresetCombo is null || _certPresetCombo.SelectedIndex <= 0) return;
            var name = _certPresetCombo.SelectedItem as string ?? "";
            if (System.Windows.MessageBox.Show(this, string.Format(S("Str_Stamp_CertPresetConfirmDelete"), name),
                S("Str_Stamp_CertPresetNameTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _certStore.DeletePreset(name); PopulateCertPresetCombo();
            SetStatusMsg(string.Format(S("Str_Stamp_CertPresetDeleted"), name));
        }

        private void SelectCertPresetCombo(string name)
        {
            if (_certPresetCombo is null) return;
            for (int i = 1; i < _certPresetCombo.Items.Count; i++)
                if ((_certPresetCombo.Items[i] as string)?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                { _certPresetCombo.SelectedIndex = i; return; }
            _certPresetCombo.SelectedIndex = 0;
        }
        private Services.CertStampStore.CertDefaults HarvestCertDefaults()
        {
            _spec.CertShowLogo = _certShowLogo.IsChecked == true; _spec.CertLogoPath = _certLogoPath;
            _spec.CertLogoScale = _certLogoScale.Value / 100.0;
            _spec.CertColor = _certColor; _spec.CertScale = _certScale.Value;
            _spec.CertBorder = Math.Max(0, Math.Min(2, _certBorder.SelectedIndex));
            _spec.CertLabel = string.IsNullOrEmpty(_certLabel.Text) ? "Document seen by:" : _certLabel.Text;
            _spec.CertShowName = _certShowName.IsChecked == true; _spec.CertName = _certName.Text;
            _spec.CertShowSig = _certShowSig.IsChecked == true; _spec.CertSigPath = _certSigPath;
            _spec.CertShowDate = _certShowDate.IsChecked == true; _spec.CertShowTime = _certShowTime.IsChecked == true;
            _spec.CertDate = _certDate.Text?.Trim() ?? ""; _spec.CertRange = ResolveCertRangeFromMode();
            (_spec.CertPosH, _spec.CertPosV) = (Positions[Math.Max(0, _certPos.SelectedIndex)].h, Positions[Math.Max(0, _certPos.SelectedIndex)].v);
            return Services.CertStampStore.ToDefaults(_spec);
        }

        private string ResolveCertRangeFromMode()
        {
            if (_certPageMode is null) return _certRange.Text.Trim();
            return _certPageMode.SelectedIndex switch { CertPageAll => "", CertPageCurrent => _certCurrentPageRange, _ => _certRange.Text.Trim() };
        }

        private void LoadCertPresetByName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var p in _certStore.LoadPresets())
                if ((p.PresetName ?? "").Equals(name, StringComparison.OrdinalIgnoreCase))
                { ApplyCertDefaultsToControls(Services.CertStampStore.CertDefaultsToSpec(p)); Schedule(); return; }
        }

        private void SetStatusMsg(string msg) => _owner.SetStatus(msg);

        private FrameworkElement BorderStyleRow()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = S("Str_Stamp_CertBorder"), Foreground = R("TextSecondary"), FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _certBorder = new ComboBox { Height = 26, Width = 150 };
            if (_darkCombo != null) _certBorder.Style = _darkCombo; else { _certBorder.Background = R("BgCanvas"); _certBorder.Foreground = R("TextPrimary"); }
            _certBorder.Items.Add(S("Str_Stamp_CertBorderRect"));
            _certBorder.Items.Add(S("Str_Stamp_CertBorderRound"));
            _certBorder.Items.Add(S("Str_Stamp_CertBorderNone"));
            _certBorder.SelectedIndex = Math.Max(0, Math.Min(2, _spec.CertBorder));
            _certBorder.SelectionChanged += (_, _2) => Schedule();
            row.Children.Add(_certBorder);
            return row;
        }


        private void ChooseCertLogo()
        {
            var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
            if (ofd.ShowDialog() == true) { _certLogoPath = ofd.FileName; _certLogoLabel.Text = System.IO.Path.GetFileName(_certLogoPath); LoadCertLogoImage(); Schedule(); }
        }

        private void ChooseCertSigImage()
        {
            var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
            if (ofd.ShowDialog() == true)
            {
                _certSigPath = ofd.FileName; _spec.CertSignatureId = null;
                if (_certSigPicker.Items.Count > 0) _certSigPicker.SelectedIndex = 0;
                Schedule();
            }
        }

        private void PopulateCertSigPicker()
        {
            if (_certSigPicker is null) return;
            _certSigPicker.Items.Clear();
            _certSigPicker.Items.Add(S("Str_Stamp_CertSigNone"));
            int sel = 0;
            foreach (var sig in _sigStore.Signatures)
            {
                _certSigPicker.Items.Add(string.IsNullOrEmpty(sig.Name) ? $"Signature {_certSigPicker.Items.Count}" : sig.Name);
                if (sig.Id == _spec.CertSignatureId) sel = _certSigPicker.Items.Count - 1;
            }
            _certSigPicker.SelectedIndex = sel;
        }

        private void OnCertSigPicked()
        {
            if (_certSigPicker is null || _certSigPicker.SelectedIndex <= 0) { _spec.CertSignatureId = null; return; }
            var sigs = _sigStore.Signatures;
            int idx = _certSigPicker.SelectedIndex - 1;
            if (idx >= 0 && idx < sigs.Count) { _spec.CertSignatureId = sigs[idx].Id; _certSigPath = null; }
        }

        private FrameworkElement BuildWatermarkSection()
        {
            var wrap = new StackPanel();
            _wmBody = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            _wmEnable = SectionToggle(S("Str_Stamp_SecWatermark"), _spec.WmEnabled);
            _wmEnable.Checked   += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            wrap.Children.Add(SectionHeaderRow(_wmEnable, _wmBody, out _wmChevron));
            _wmEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };

            // Type: text vs image (clean UiKit radios line up with the section content directly).
            var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            _wmTextRadio = MakeRadio(S("Str_Stamp_WmText"), !_spec.WmIsImage);
            _wmTextRadio.Margin = new Thickness(0, 0, 14, 0);
            _wmImageRadio = MakeRadio(S("Str_Stamp_WmImage"), _spec.WmIsImage);
            _wmTextRadio.Checked  += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            _wmImageRadio.Checked += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            typeRow.Children.Add(_wmTextRadio);
            typeRow.Children.Add(_wmImageRadio);
            _wmBody.Children.Add(typeRow);

            // Text sub-panel
            _wmTextPanel = new StackPanel();
            _wmTextPanel.Children.Add(UiKit.GroupLabel(S("Str_Stamp_WmTextLabel")));
            _wmText = UiKit.Field();
            _wmText.Text = _spec.WmText;
            _wmText.Margin = new Thickness(0, 0, 0, 8);
            _wmText.TextChanged += (_, _2) => Schedule();
            _wmTextPanel.Children.Add(_wmText);

            var wmFontRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            wmFontRow.Children.Add(new TextBlock { Text = S("Str_Bar_Font"), Foreground = R("TextSecondary"), FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _wmFont = new ComboBox { Width = 188, Height = 26, MaxDropDownHeight = 320, VerticalAlignment = VerticalAlignment.Center };
            if (_darkCombo != null) _wmFont.Style = _darkCombo; else { _wmFont.Background = R("BgCanvas"); _wmFont.Foreground = R("TextPrimary"); }
            foreach (var fn in MainWindow.SystemFontNames) _wmFont.Items.Add(fn);
            _wmFont.SelectedItem = _spec.WmFont;
            _wmFont.SelectionChanged += (_, _2) => Schedule();
            wmFontRow.Children.Add(_wmFont);
            _wmTextPanel.Children.Add(wmFontRow);
            _wmTextPanel.Children.Add(SliderBoxRow(S("Str_Stamp_FontSize"), 12, 200, _spec.WmFontPt, out _, out _wmSize));
            _wmTextPanel.Children.Add(ColorRow(S("Str_Stamp_Color"), _wmColor, out _wmSwatch, c => { _wmColor = c; Schedule(); }));
            _wmBody.Children.Add(_wmTextPanel);

            // Image sub-panel: filename fills the left, the Choose button sits right-aligned across from it.
            _wmImagePanel = new StackPanel();
            var imgRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var chooseBtn = UiKit.Make(S("Str_Stamp_ChooseImage"), false);
            chooseBtn.Click += (_, _2) => ChooseImage();
            DockPanel.SetDock(chooseBtn, Dock.Right);
            imgRow.Children.Add(chooseBtn);
            _wmImageLabel = new TextBlock { Text = System.IO.Path.GetFileName(_wmImagePath ?? ""), Foreground = R("TextSecondary"), FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            imgRow.Children.Add(_wmImageLabel);
            _wmImagePanel.Children.Add(imgRow);
            _wmImagePanel.Children.Add(SliderBoxRow(S("Str_Stamp_Scale"), 10, 200, _spec.WmScale * 100, out _wmScale, out _));
            _wmBody.Children.Add(_wmImagePanel);

            // Shared watermark controls
            _wmBody.Children.Add(SliderBoxRow(S("Str_Stamp_Angle"), -90, 90, _spec.WmAngle, out _wmAngle, out _));
            _wmBody.Children.Add(SliderBoxRow(S("Str_Stamp_Opacity"), 5, 100, _spec.WmOpacity * 100, out _wmOpacity, out _));

            _wmBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Position")));
            _wmPos = MakePosCombo(_spec.WmPosH, _spec.WmPosV);
            _wmPos.SelectionChanged += (_, _2) => Schedule();
            _wmBody.Children.Add(_wmPos);

            _wmBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Pages")));
            _wmRange = UiKit.Field();
            _wmRange.Text = _spec.WmRange;
            _wmRange.ToolTip = S("Str_Crop_RangeTip");
            _wmRange.TextChanged += (_, _2) => Schedule();
            _wmBody.Children.Add(_wmRange);

            wrap.Children.Add(_wmBody);
            return wrap;
        }

        // ---------- shared builders ----------
        private CheckBox SectionToggle(string text, bool on)
        {
            var cb = UiKit.CheckBox(text);
            cb.IsChecked = on;
            cb.FontSize = 13;
            cb.FontWeight = FontWeights.SemiBold;
            cb.VerticalAlignment = VerticalAlignment.Center;
            return cb;
        }

        // Collapsible section header with accordion behaviour. The whole header row (chevron + enable
        // checkbox) is clickable to expand this section and collapse the others. The enable checkbox only
        // controls whether the stamp is applied/previewed â€” NOT the section's expansion â€” so you can
        // configure any tool before turning it on, and all three never overflow the sidebar at once.
        private FrameworkElement SectionHeaderRow(CheckBox enable, StackPanel body, out TextBlock chevron)
        {
            // A full-width bar: chevron button on the left, section title checkbox on the right.
            // The chevron is a real Button so it's guaranteed clickable in all WPF event-routing scenarios.
            var bar = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(8, 6, 8, 6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            // The chevron is a chrome-free Button: no border, no background, just the arrow glyph.
            // Clicking it expands this section and collapses the others.
            var chevronBtn = new Button
            {
                Content = "â–¸",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            chevronBtn.Click += (_, _2) => ExpandSection(body);

            // The chevron text is tracked for expand/collapse state (â–¾ vs â–¸).
            // Since the Button's Content is a string, we update it via the ExpandSection loop.
            chevron = new TextBlock(); // placeholder; ExpandSection writes â–¾/â–¸ to chevron.Text â€” but since
            // the real chevron is a Button, store a reference so ExpandSection can update the Button's Content.
            // We'll use a closure: ExpandSection will update chevronBtn.Content instead.
            // But ExpandSection writes to chevron.Text. So instead, we keep the TextBlock as a hidden ref and
            // sync it in ExpandSection. Better: just make ExpandSection work with the Button directly.

            // Clicking the checkbox itself also expands the section.
            enable.Click += (_, _2) => ExpandSection(body);

            _sections.Add((body, chevron));
            body.Visibility = Visibility.Collapsed;

            // Hover: subtle accent-tinted background so the row reads as an interactive header.
            bar.MouseEnter += (_, _2) =>
            {
                bar.Background = new SolidColorBrush(Color.FromArgb(40, 100, 160, 255));
            };
            bar.MouseLeave += (_, _2) =>
            {
                bar.Background = Brushes.Transparent;
            };

            row.Children.Add(chevronBtn);
            row.Children.Add(enable);
            bar.Child = row;

            // Override ExpandSection chevron update for this section: we update the Button's Content,
            // not a TextBlock. Store the button reference keyed by body.
            _sectionChevronBtns ??= new Dictionary<StackPanel, Button>();
            _sectionChevronBtns[body] = chevronBtn;

            return bar;
        }

        // Accordion: expand exactly one section, collapsing the rest. Called on header click.
        private void ExpandSection(StackPanel target)
        {
            foreach (var (body, chev) in _sections)
            {
                bool isTarget = ReferenceEquals(body, target);
                body.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
                chev.Text = isTarget ? "â–¾" : "â–¸";
                // Also update the Button chevron if one exists for this section.
                if (_sectionChevronBtns != null && _sectionChevronBtns.TryGetValue(body, out var btn))
                    btn.Content = isTarget ? "â–¾" : "â–¸";
            }
        }
        // A slider paired with a small numeric input box (two-way synced), e.g. font size.
        private FrameworkElement SliderBoxRow(string label, double min, double max, double value, out Slider slider, out TextBox box)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            panel.Children.Add(UiKit.GroupLabel(label));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var s = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), SmallChange = 1, LargeChange = 4, VerticalAlignment = VerticalAlignment.Center };
            if (_darkSlider != null) s.Style = _darkSlider;
            var b = UiKit.Field(46);
            b.Text = ((int)Math.Round(value)).ToString();
            b.Margin = new Thickness(8, 0, 0, 0);
            bool guard = false;
            s.ValueChanged += (_, _2) => { if (guard) return; guard = true; b.Text = ((int)Math.Round(s.Value)).ToString(); guard = false; Schedule(); };
            b.TextChanged += (_, _2) => { if (guard) return; if (double.TryParse(b.Text, out double d)) { guard = true; s.Value = Math.Max(min, Math.Min(max, d)); guard = false; Schedule(); } };
            Grid.SetColumn(s, 0); Grid.SetColumn(b, 1);
            grid.Children.Add(s); grid.Children.Add(b);
            panel.Children.Add(grid);
            slider = s; box = b;
            return panel;
        }

        private ComboBox MakePosCombo(int h, int v)
        {
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Height = 26 };
            if (_darkCombo != null) combo.Style = _darkCombo; else { combo.Background = R("BgCanvas"); combo.Foreground = R("TextPrimary"); }
            int sel = 0;
            for (int i = 0; i < Positions.Length; i++)
            {
                combo.Items.Add(S(Positions[i].key));
                if (Positions[i].h == h && Positions[i].v == v) sel = i;
            }
            combo.SelectedIndex = sel;
            return combo;
        }

        private RadioButton MakeRadio(string text, bool isChecked)
        {
            var rb = UiKit.Radio(text);
            rb.IsChecked = isChecked;
            return rb;
        }

        private FrameworkElement ColorRow(string label, Color initial, out Border swatch, Action<Color> onPick)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = label, Foreground = R("TextSecondary"), FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

            var sw = new Border
            {
                Width = 44, Height = 22, CornerRadius = UiKit.RadControl,
                BorderBrush = R("BorderDim"), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(initial), SnapsToDevicePixels = true
            };

            // The swatch is a real Button (chrome-free template) so the click is rock-solid - a plain
            // Border's MouseLeftButtonUp was unreliable here, which is why the color never updated.
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            var btn = new Button
            {
                Content = sw, Cursor = Cursors.Hand, Focusable = false,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Template = new ControlTemplate(typeof(Button)) { VisualTree = cp }
            };
            btn.Click += (_, _2) =>
            {
                var current = sw.Background is SolidColorBrush b ? b.Color : initial;
                var dlg = new ColorPickerDialog(this, current);
                dlg.ShowDialog();
                // Apply SelectedColor unconditionally rather than gating on DialogResult: opening the picker
                // as a nested dialog from this modal window + the fade-close makes ShowDialog return false even
                // on OK. On Cancel, SelectedColor is still the original color, so this is harmless.
                sw.Background = new SolidColorBrush(dlg.SelectedColor);
                onPick(dlg.SelectedColor);
            };

            row.Children.Add(btn);
            swatch = sw;
            return row;


        }

        private FrameworkElement Divider() => new Border { Height = 1, Background = R("BorderDim"), Opacity = 0.6, Margin = new Thickness(0, 12, 0, 12) };

        private void UpdateEnabledStates()
        {
            if (_wmTextPanel != null) _wmTextPanel.Visibility = _wmImageRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            if (_wmImagePanel != null) _wmImagePanel.Visibility = _wmImageRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // Nothing to apply unless at least one section is enabled.
            if (_applyBtn != null) _applyBtn.IsEnabled = _numEnable.IsChecked == true || _wmEnable.IsChecked == true || _certEnable.IsChecked == true;
            // NOTE: section bodies are left editable regardless of their enable checkbox â€” you can configure
            // a stamp before turning it on. Visibility is governed by the accordion (ExpandSection), not here.
        }

        // Mirroring only makes sense for a left/right position, so grey it out on a centered one.
        private void UpdateMirrorEnabled()
        {
            if (_numMirror == null || _numPos == null) return;
            _numMirror.IsEnabled = Positions[Math.Max(0, _numPos.SelectedIndex)].h != 1;
        }

        private void ChooseImage()
        {
            var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
            if (ofd.ShowDialog() == true)
            {
                _wmImagePath = ofd.FileName;
                _wmImageLabel.Text = System.IO.Path.GetFileName(_wmImagePath);
                LoadWatermarkImage();
                Schedule();
            }
        }

        private void LoadWatermarkImage()
        {
            _wmImageSrc = null;
            if (string.IsNullOrEmpty(_wmImagePath) || !System.IO.File.Exists(_wmImagePath)) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(_wmImagePath!);
                bmp.EndInit();
                bmp.Freeze();
                _wmImageSrc = bmp;
            }
            catch { _wmImageSrc = null; }
        }
        private void LoadCertLogoImage()
        {
            _certLogoSrc = null;
            if (string.IsNullOrEmpty(_certLogoPath) || !System.IO.File.Exists(_certLogoPath)) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(_certLogoPath!);
                bmp.EndInit();
                bmp.Freeze();
                _certLogoSrc = bmp;
            }
            catch { _certLogoSrc = null; }
        }


        // ---------- preview ----------
        // ---------- Preview page stepper ----------
        private FrameworkElement BuildPageNav()
        {
            _prevArrow = MakeNavArrow("î«", () => GoToPage(_pageIndex - 1));   // ChevronLeft
            _nextArrow = MakeNavArrow("î¬", () => GoToPage(_pageIndex + 1));   // ChevronRight
            _pageNavLabel = new TextBlock
            {
                FontFamily = UiKit.UiFont, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0)
            };
            _pageNavLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 8)
            };
            row.Children.Add(_prevArrow);
            row.Children.Add(_pageNavLabel);
            row.Children.Add(_nextArrow);
            UpdatePageNav();
            return row;





        }

        // Same chrome as the print preview stepper (UiKit.Make), so the two windows share one button style.
        private Button MakeNavArrow(string glyph, Action onClick)
        {
            var b = UiKit.Make(glyph, false);
            b.FontFamily = UiKit.IconFont;
            b.FontSize = 12;
            b.Click += (_, _2) => onClick();
            return b;
        }

        // Button clicks step one page and render immediately.
        private void GoToPage(int idx)
        {
            if (_pageProvider == null) return;
            idx = Math.Max(0, Math.Min(_pageCount - 1, idx));
            if (idx == _pageIndex) return;
            _pageIndex = idx;
            UpdatePageNav();
            RenderCurrentPage();
        }

        // Wheel stepping advances the page number (and arrow states) instantly and defers the heavy page
        // render until the wheel settles, so a fast flick scrolls quickly instead of blocking on each
        // page's rasterization.
        private void StepPage(int delta)
        {
            if (_pageProvider == null) return;
            int idx = Math.Max(0, Math.Min(_pageCount - 1, _pageIndex + delta));
            if (idx == _pageIndex) return;
            _pageIndex = idx;
            UpdatePageNav();
            _navRenderTimer ??= MakeNavRenderTimer();
            _navRenderTimer.Stop();
            _navRenderTimer.Start();
        }

        private System.Windows.Threading.DispatcherTimer MakeNavRenderTimer()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            t.Tick += (_, _2) => { t.Stop(); RenderCurrentPage(); };
            return t;
        }

        private void RenderCurrentPage()
        {
            if (_pageProvider == null) return;
            var (src, wpt, hpt) = _pageProvider(_pageIndex);
            if (src == null) return;
            _pageSrc = src; _pageWpt = wpt; _pageHpt = hpt;
            _preview.Source = src;
            RenderPreview();
        }

        private void UpdatePageNav()
        {
            if (_pageNavLabel == null) return;
            _pageNavLabel.Text = string.Format(S("Str_PageOf"), _pageIndex + 1, _pageCount);
            _prevArrow.IsEnabled = _pageIndex > 0;
            _nextArrow.IsEnabled = _pageIndex < _pageCount - 1;
        }

        private void SizePreviewImage()
        {
            if (_previewArea == null) return;
            double availW = Math.Max(1, _previewArea.ActualWidth - 48);
            double availH = Math.Max(1, _previewArea.ActualHeight - 48);
            double ar = _pageHpt > 0 ? _pageWpt / _pageHpt : (_pageSrc.PixelWidth / (double)_pageSrc.PixelHeight);
            double w = availW, h = w / ar;
            if (h > availH) { h = availH; w = h * ar; }
            _preview.Width = w;
            _preview.Height = h;
            _overlay.Width = w;
            _overlay.Height = h;
        }

        private static HashSet<int> ParseRange(string range, int pageCount)
        {
            var set = new HashSet<int>();
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

        private void RenderPreview()
        {
            _overlay.Children.Clear();
            _overlay.IsHitTestVisible = false;   // re-enabled by MakeDraggable only when a custom stamp is shown
            SizePreviewImage();
            double pw = _preview.Width, ph = _preview.Height;
            if (double.IsNaN(pw) || pw <= 0 || double.IsNaN(ph) || ph <= 0) return;
            double pxPerPt = _pageHpt > 0 ? ph / _pageHpt : 1;   // preview pixels per PDF point
            double mx = pw * 0.05, my = ph * 0.04;

            // Watermark sits under the page-number text (drawn first).
            if (_wmEnable.IsChecked == true && ParseRange(_wmRange.Text, _pageCount).Contains(_pageIndex))
            {
                if (_wmImageRadio.IsChecked == true && _wmImageSrc != null)
                {
                    double scale = _wmScale.Value / 100.0;
                    double iw = Math.Min(pw, _wmImageSrc.PixelWidth * pxPerPt * 0.5) * scale;
                    double ih = iw * _wmImageSrc.PixelHeight / Math.Max(1, _wmImageSrc.PixelWidth);
                    var img = new Image { Source = _wmImageSrc, Width = iw, Height = ih, Opacity = _wmOpacity.Value / 100.0, Stretch = Stretch.Fill };
                    PlaceRotated(img, iw, ih, _wmPos.SelectedIndex, pw, ph, mx, my, _wmAngle.Value);
                }
                else if (_wmImageRadio.IsChecked != true && _wmText.Text.Length > 0)
                {
                    double fpx = ReadDouble(_wmSize, 64) * pxPerPt;
                    var tb = new TextBlock { Text = _wmText.Text, FontFamily = new FontFamily(_wmFont.SelectedItem as string ?? "Segoe UI"), FontWeight = FontWeights.Bold, FontSize = Math.Max(6, fpx), Foreground = new SolidColorBrush(_wmColor), Opacity = _wmOpacity.Value / 100.0 };
                    var sz = Measure(tb);
                    PlaceRotated(tb, sz.Width, sz.Height, _wmPos.SelectedIndex, pw, ph, mx, my, _wmAngle.Value);
                }
            }

            // Page number for the current page.
            if (_numEnable.IsChecked == true && ParseRange(_numRange.Text, _pageCount).Contains(_pageIndex))
            {
                double fpx = ReadDouble(_numSize, 12) * pxPerPt;
                string text = (_numFormat.Text.Length == 0 ? "{n}" : _numFormat.Text)
                    .Replace("{n}", (ReadInt(_numStart, 1) + _pageIndex).ToString())
                    .Replace("{N}", _pageCount.ToString());
                if (text.Length > 0)
                {
                    var tb = new TextBlock { Text = text, FontFamily = UiKit.UiFont, FontSize = Math.Max(5, fpx), Foreground = new SolidColorBrush(_numColor) };
                    var sz = Measure(tb);
                    int h = Positions[Math.Max(0, _numPos.SelectedIndex)].h, v = Positions[Math.Max(0, _numPos.SelectedIndex)].v;
                    double x, y;
                    if (h < 0)   // custom: drag the number anywhere on the page
                    {
                        bool mirroredHere = _numMirror.IsChecked == true && (_pageIndex % 2 == 1);
                        double cx = mirroredHere ? 1 - _spec.NumCustomX : _spec.NumCustomX;
                        x = cx * pw - sz.Width / 2;
                        y = _spec.NumCustomY * ph - sz.Height / 2;
                        MakeDraggable(tb, sz.Width, sz.Height, pw, ph, (fx, fy) =>
                        {
                            _spec.NumCustomX = mirroredHere ? 1 - fx : fx;
                            _spec.NumCustomY = fy;
                        });
                    }
                    else
                    {
                        if (_numMirror.IsChecked == true && h != 1 && (_pageIndex % 2 == 1)) h = 2 - h;
                        x = h == 0 ? mx : h == 2 ? pw - sz.Width - mx : (pw - sz.Width) / 2;
                        y = v == 0 ? my : v == 1 ? (ph - sz.Height) / 2 : ph - sz.Height - my;
                    }
                    Canvas.SetLeft(tb, x);
                    Canvas.SetTop(tb, y);
                    _overlay.Children.Add(tb);
                }
            }
            // (a/e) Certification block: live preview with drag-to-move (custom position) + corner resize handle.
            if (_certEnable.IsChecked == true)
                RenderCertPreview(pxPerPt, pw, ph, mx, my);
        }

        // (a) Renders the certification block onto the preview overlay. When the position is "custom" the block
        // is draggable anywhere on the page (e); a bottom-right thumb resizes the whole block live (a).
        private void RenderCertPreview(double pxPerPt, double pw, double ph, double mx, double my)
        {
            SyncCertControlsToSpec();
            bool anyText = _spec.CertShowName || _spec.CertShowDate || _spec.CertShowSig || !string.IsNullOrWhiteSpace(_spec.CertLabel);
            if (!anyText && !_spec.CertShowLogo) return;
            if (!ParseRange(ResolveCertRangeFromMode(), _pageCount).Contains(_pageIndex)) return;

            double fontPx = Math.Max(5, 10 * _spec.CertScale * pxPerPt);
            var brush = new SolidColorBrush(_spec.CertColor);
            var fill = _spec.CertWhiteFill ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) : null;
            double pad = 7 * _spec.CertScale * pxPerPt;
            var inner = new StackPanel { Margin = new Thickness(pad) };
            if (_spec.CertShowLogo && _certLogoSrc != null)
            {
                double logoPx = 26 * _spec.CertScale * _spec.CertLogoScale * pxPerPt;
                double logoW = _certLogoSrc.PixelWidth > 0 && _certLogoSrc.PixelHeight > 0 ? logoPx * _certLogoSrc.PixelWidth / _certLogoSrc.PixelHeight : logoPx;
                inner.Children.Add(new Image { Source = _certLogoSrc, Width = logoW, Height = logoPx, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) });
            }
            if (!string.IsNullOrWhiteSpace(_spec.CertLabel))
                inner.Children.Add(new TextBlock { Text = _spec.CertLabel, FontFamily = UiKit.UiFont, FontSize = fontPx, FontWeight = FontWeights.SemiBold, Foreground = brush, Margin = new Thickness(0, 0, 0, 2) });
            if (_spec.CertShowName && !string.IsNullOrWhiteSpace(_spec.CertName))
                inner.Children.Add(new TextBlock { Text = _spec.CertName, FontFamily = UiKit.UiFont, FontSize = fontPx, Foreground = brush });
            if (_spec.CertShowDate && !string.IsNullOrWhiteSpace(_spec.CertDate))
                inner.Children.Add(new TextBlock { Text = _spec.CertDate, FontFamily = UiKit.UiFont, FontSize = fontPx * 0.9, Foreground = brush });
            if (_spec.CertShowSig)
            {
                var sigSrc = ResolveCertSigBitmap();
                if (sigSrc != null)
                {
                    double sigH = 34 * _spec.CertScale * pxPerPt;
                    double sigW = sigH * sigSrc.PixelWidth / Math.Max(1, sigSrc.PixelHeight);
                    inner.Children.Add(new Image { Source = sigSrc, Width = sigW, Height = sigH, Stretch = Stretch.Fill, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
                }
            }
            var border = _certPreviewBlock = new Border
            {
                Background = fill,
                BorderBrush = _spec.CertBorder == 2 ? null : brush,
                BorderThickness = _spec.CertBorder == 2 ? new Thickness(0) : new Thickness(1.2),
                CornerRadius = _spec.CertBorder == 1 ? new CornerRadius(6) : new CornerRadius(0),
                Child = inner,
            };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = border.DesiredSize.Width, h = border.DesiredSize.Height;
            if (w <= 0 || h <= 0) return;
            int hpos = _spec.CertPosH, vpos = _spec.CertPosV;
            double x, y;
            if (hpos < 0)
            {
                x = _spec.CertCustomX * pw - w / 2; y = _spec.CertCustomY * ph - h / 2;
                MakeDraggable(border, w, h, pw, ph, (fx, fy) => { _spec.CertCustomX = fx; _spec.CertCustomY = fy; });
            }
            else
            {
                x = hpos == 0 ? mx : hpos == 2 ? pw - w - mx : (pw - w) / 2;
                y = vpos == 0 ? my : vpos == 1 ? (ph - h) / 2 : ph - h - my;
            }
            Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
            _overlay.Children.Add(border);
            if (hpos < 0) AddCertResizeThumb(brush, x, y, w, h, ph);
        }

        private void AddCertResizeThumb(SolidColorBrush brush, double x, double y, double w, double h, double ph)
        {
            const double thumb = 10;
            var thumbEl = new Border { Width = thumb, Height = thumb, Background = brush, Opacity = 0.7, Cursor = Cursors.SizeNWSE, BorderBrush = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(1) };
            _overlay.IsHitTestVisible = true; thumbEl.IsHitTestVisible = true;
            Canvas.SetLeft(thumbEl, x + w - thumb / 2); Canvas.SetTop(thumbEl, y + h - thumb / 2);
            bool resizing = false; Point? startP = null; double startScale = 1;
            thumbEl.MouseLeftButtonDown += (_, e) => { resizing = true; startP = e.GetPosition(_overlay); startScale = _certScale.Value; thumbEl.CaptureMouse(); e.Handled = true; };
            thumbEl.MouseLeftButtonUp += (_, _2) => { resizing = false; thumbEl.ReleaseMouseCapture(); };
            thumbEl.MouseMove += (_, e) =>
            {
                if (!resizing || startP is null) return;
                var p = e.GetPosition(_overlay);
                double drag = ((p.X - startP.Value.X) + (p.Y - startP.Value.Y)) / 2;
                double ns = Math.Max(0.6, Math.Min(2.5, startScale + drag / Math.Max(1, ph) * 1.5));
                _certScale.Value = ns; _spec.CertScale = ns; Schedule();
            };
            _overlay.Children.Add(thumbEl);
        }

        // Syncs the cert section's live control values into _spec (the preview reads from _spec).
        private void SyncCertControlsToSpec()
        {
            _spec.CertShowLogo = _certShowLogo.IsChecked == true; _spec.CertLogoPath = _certLogoPath;
            _spec.CertLogoScale = _certLogoScale.Value / 100.0;
            _spec.CertLabel = _certLabel.Text; _spec.CertShowName = _certShowName.IsChecked == true; _spec.CertName = _certName.Text;
            _spec.CertShowSig = _certShowSig.IsChecked == true; _spec.CertSigPath = _certSigPath;
            _spec.CertShowDate = _certShowDate.IsChecked == true; _spec.CertShowTime = _certShowTime.IsChecked == true;
            _spec.CertDate = _certDate.Text?.Trim() ?? ""; _spec.CertScale = _certScale.Value;
            _spec.CertBorder = Math.Max(0, Math.Min(2, _certBorder.SelectedIndex));
            _spec.CertColor = _certColor;
            (_spec.CertPosH, _spec.CertPosV) = (Positions[Math.Max(0, _certPos.SelectedIndex)].h, Positions[Math.Max(0, _certPos.SelectedIndex)].v);
        }

        // Resolves the signature bitmap for the cert preview: a picked image file, or a saved signature's PNG.
        private BitmapSource? ResolveCertSigBitmap()
        {
            if (!string.IsNullOrEmpty(_certSigPath) && System.IO.File.Exists(_certSigPath))
            {
                try { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(_certSigPath); b.EndInit(); b.Freeze(); return b; } catch { }
            }
            if (!string.IsNullOrEmpty(_spec.CertSignatureId))
            {
                foreach (var sig in _sigStore.Signatures)
                    if (sig.Id == _spec.CertSignatureId && !string.IsNullOrEmpty(sig.ImageData))
                    {
                        try { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(sig.ImageData!)); b.EndInit(); b.Freeze(); return b; } catch { }
                    }
            }
            return null;
        }

        private void PlaceRotated(FrameworkElement el, double w, double h, int posIndex, double pw, double ph, double mx, double my, double angle)
        {
            int hpos = Positions[Math.Max(0, posIndex)].h, vpos = Positions[Math.Max(0, posIndex)].v;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new RotateTransform(-angle);
            double x, y;
            if (hpos < 0)   // custom: drag the watermark anywhere on the page
            {
                x = _spec.WmCustomX * pw - w / 2;
                y = _spec.WmCustomY * ph - h / 2;
                MakeDraggable(el, w, h, pw, ph, (fx, fy) => { _spec.WmCustomX = fx; _spec.WmCustomY = fy; });
            }
            else
            {
                x = hpos == 0 ? mx : hpos == 2 ? pw - w - mx : (pw - w) / 2;
                y = vpos == 0 ? my : vpos == 1 ? (ph - h) / 2 : ph - h - my;
            }
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            _overlay.Children.Add(el);
        }

        // Makes a stamp element draggable in the preview; reports the new center as fractions of the page.
        private void MakeDraggable(FrameworkElement el, double elW, double elH, double pw, double ph, Action<double, double> onMove)
        {
            _overlay.IsHitTestVisible = true;
            el.IsHitTestVisible = true;
            el.Cursor = Cursors.SizeAll;
            bool dragging = false;
            el.MouseLeftButtonDown += (_, e) => { dragging = true; el.CaptureMouse(); e.Handled = true; };
            el.MouseLeftButtonUp   += (_, _2) => { dragging = false; el.ReleaseMouseCapture(); };
            el.MouseMove += (_, e) =>
            {
                if (!dragging) return;
                var p = e.GetPosition(_overlay);
                double left = Math.Max(0, Math.Min(pw - elW, p.X - elW / 2));
                double top  = Math.Max(0, Math.Min(ph - elH, p.Y - elH / 2));
                Canvas.SetLeft(el, left);
                Canvas.SetTop(el, top);
                double fx = pw > 0 ? Math.Max(0, Math.Min(1, (left + elW / 2) / pw)) : 0.5;
                double fy = ph > 0 ? Math.Max(0, Math.Min(1, (top + elH / 2) / ph)) : 0.5;
                onMove(fx, fy);
            };
        }

        private static Size Measure(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize;
        }

        private static double ReadDouble(TextBox tb, double fallback)
            => double.TryParse(tb.Text?.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out double d) && d > 0 ? d : fallback;
        private static int ReadInt(TextBox tb, int fallback)
            => int.TryParse(tb.Text?.Trim(), out int i) ? i : fallback;

        private void ResetAll()
        {
            var d = new StampSpec { NumbersEnabled = _numEnable.IsChecked == true };
            _numEnable.IsChecked = d.NumbersEnabled;
            _numStart.Text = d.StartNumber.ToString();
            _numFormat.Text = d.Format;
            _numSize.Text = d.NumFontPt.ToString("0");
            _numRange.Text = d.NumRange;
            _numColor = d.NumColor; _numSwatch.Background = new SolidColorBrush(d.NumColor);
            _wmText.Text = d.WmText;
            _wmSize.Text = d.WmFontPt.ToString("0");
            _wmFont.SelectedItem = d.WmFont;
            _wmColor = d.WmColor; _wmSwatch.Background = new SolidColorBrush(d.WmColor);
            _wmAngle.Value = d.WmAngle;
            _wmOpacity.Value = d.WmOpacity * 100;
            _wmScale.Value = d.WmScale * 100;
            // Certification: reset to defaults but keep the section's enabled state the user left it in.
            _certEnable.IsChecked = d.CertEnabled;
            _certShowLogo.IsChecked = d.CertShowLogo;
            _certLogoLabel.Text = ""; _certLogoPath = null;
            _certLabel.Text = d.CertLabel;
            _certShowName.IsChecked = d.CertShowName;
            _certName.Text = d.CertName;
            _certShowSig.IsChecked = d.CertShowSig;
            _certSigPath = null; _spec.CertSignatureId = null; PopulateCertSigPicker();
            _certShowDate.IsChecked = d.CertShowDate;
            _certDate.Text = d.CertDate;
            _certShowTime.IsChecked = d.CertShowTime;
            _certScale.Value = d.CertScale;
            _certLogoScale.Value = d.CertLogoScale * 100.0;
            _certBorder.SelectedIndex = d.CertBorder;
            _certColor = d.CertColor; _certSwatch.Background = new SolidColorBrush(d.CertColor);
            _certRange.Text = d.CertRange;
            if (_certPageMode != null) _certPageMode.SelectedIndex = ResolveCertPageMode(d.CertRange);
            UpdateCertRangeVisibility();
            Schedule();
        }

        // Loads a cert preset (as a StampSpec) into the certification controls only, leaving other sections intact.
        private void ApplyCertDefaultsToControls(StampSpec d)
        {
            _certShowLogo.IsChecked = d.CertShowLogo;
            _certLogoPath = d.CertLogoPath; _certLogoLabel.Text = System.IO.Path.GetFileName(d.CertLogoPath ?? "");
            LoadCertLogoImage();
            _certLabel.Text = d.CertLabel;
            _certShowName.IsChecked = d.CertShowName; _certName.Text = d.CertName;
            _certShowSig.IsChecked = d.CertShowSig;
            _certSigPath = d.CertSigPath; _spec.CertSignatureId = d.CertSignatureId; PopulateCertSigPicker();
            _certShowDate.IsChecked = d.CertShowDate; _certDate.Text = d.CertDate;
            _certShowTime.IsChecked = d.CertShowTime;
            _certScale.Value = d.CertScale;
            _certLogoScale.Value = d.CertLogoScale * 100.0;
            _certBorder.SelectedIndex = Math.Max(0, Math.Min(2, d.CertBorder));
            _certColor = d.CertColor; _certSwatch.Background = new SolidColorBrush(d.CertColor);
            _certRange.Text = d.CertRange;
            if (_certPageMode != null) _certPageMode.SelectedIndex = ResolveCertPageMode(d.CertRange);
            UpdateCertRangeVisibility();
            // Position: find the combo entry matching the spec's h/v.
            for (int i = 0; i < Positions.Length; i++)
                if (Positions[i].h == d.CertPosH && Positions[i].v == d.CertPosV) { _certPos.SelectedIndex = i; break; }
        }

        private void CommitAndClose()
        {
            _spec.NumbersEnabled = _numEnable.IsChecked == true;
            _spec.StartNumber = ReadInt(_numStart, 1);
            _spec.Format = _numFormat.Text.Length == 0 ? "{n}" : _numFormat.Text;
            _spec.NumFontPt = ReadDouble(_numSize, 12);
            _spec.NumColor = _numColor;
            _spec.NumRange = _numRange.Text.Trim();
            _spec.NumMirror = _numMirror.IsChecked == true;
            (_spec.NumPosH, _spec.NumPosV) = (Positions[Math.Max(0, _numPos.SelectedIndex)].h, Positions[Math.Max(0, _numPos.SelectedIndex)].v);

            _spec.WmEnabled = _wmEnable.IsChecked == true;
            _spec.WmIsImage = _wmImageRadio.IsChecked == true;
            _spec.WmText = _wmText.Text;
            _spec.WmFontPt = ReadDouble(_wmSize, 64);
            _spec.WmFont = _wmFont.SelectedItem as string ?? "Segoe UI";
            _spec.WmColor = _wmColor;
            _spec.WmOpacity = _wmOpacity.Value / 100.0;
            _spec.WmAngle = _wmAngle.Value;
            _spec.WmScale = _wmScale.Value / 100.0;
            _spec.WmImagePath = _wmImagePath;
            _spec.WmRange = _wmRange.Text.Trim();
            (_spec.WmPosH, _spec.WmPosV) = (Positions[Math.Max(0, _wmPos.SelectedIndex)].h, Positions[Math.Max(0, _wmPos.SelectedIndex)].v);

            // Certification
            _spec.CertEnabled = _certEnable.IsChecked == true;
            _spec.CertShowLogo = _certShowLogo.IsChecked == true;
            _spec.CertLogoPath = _certLogoPath;
            _spec.CertLogoScale = _certLogoScale.Value / 100.0;
            _spec.CertColor = _certColor;
            _spec.CertScale = _certScale.Value;
            _spec.CertBorder = Math.Max(0, Math.Min(2, _certBorder.SelectedIndex));
            _spec.CertLabel = string.IsNullOrEmpty(_certLabel.Text) ? "Document seen by:" : _certLabel.Text;
            _spec.CertShowName = _certShowName.IsChecked == true;
            _spec.CertName = _certName.Text;
            _spec.CertShowSig = _certShowSig.IsChecked == true;
            _spec.CertSigPath = _certSigPath;   // CertSignatureId already set live by OnCertSigPicked
            _spec.CertShowDate = _certShowDate.IsChecked == true;
            _spec.CertShowTime = _certShowTime.IsChecked == true;
            _spec.CertDate = _certDate.Text?.Trim() ?? "";
            _spec.CertRange = ResolveCertRangeFromMode();
            (_spec.CertPosH, _spec.CertPosV) = (Positions[Math.Max(0, _certPos.SelectedIndex)].h, Positions[Math.Max(0, _certPos.SelectedIndex)].v);

            Result = _spec;
            Applied = true;
            Close();
        }
    }
}
