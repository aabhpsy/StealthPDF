using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StealthPDF.Services;
using Microsoft.Win32;

namespace StealthPDF
{
    internal sealed class CertDefaultsWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly CertStampStore _store = new();
        private readonly CertStampStore.CertDefaults _defaults;

        private TextBox _nameField = null!, _labelField = null!;
        private TextBlock _logoLabel = null!, _sigLabel = null!;
        private string? _logoPath, _sigPath;
        private ComboBox _posCombo = null!, _borderCombo = null!;
        private Slider _logoScale = null!;
        private Border _colorSwatch = null!;
        private Color _color = Color.FromRgb(0x22, 0x22, 0x22);

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;

        public CertDefaultsWindow(MainWindow owner)
        {
            _owner = owner;
            _defaults = _store.Load() ?? new CertStampStore.CertDefaults();
            _color = CertStampStore.ColorFromHex(_defaults.ColorHex);
            _logoPath = _defaults.LogoPath;
            _sigPath = _defaults.SigPath;
            Title = S("Str_StampDefaults_Title");
            Width = 400;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            ResizeMode = ResizeMode.NoResize;
            DialogChrome.Configure(this, owner);
            BuildUi();
        }

        private void BuildUi()
        {
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            panel.Children.Add(UiKit.GroupLabel(S("Str_Stamp_CertName")));
            _nameField = UiKit.Field();
            _nameField.Text = _defaults.Name;
            _nameField.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(_nameField);

            panel.Children.Add(UiKit.GroupLabel(S("Str_Stamp_CertLabel")));
            _labelField = UiKit.Field();
            _labelField.Text = _defaults.Label;
            _labelField.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(_labelField);

            var logoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var logoBtn = UiKit.Make(S("Str_Stamp_CertLogo"), false);
            logoBtn.Click += (_, _2) => ChooseLogo();
            logoRow.Children.Add(logoBtn);
            _logoLabel = new TextBlock
            {
                Text = Path.GetFileName(_logoPath ?? S("Str_Stamp_CertSigNone")),
                Foreground = R("TextSecondary"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, MaxWidth = 200
            };
            logoRow.Children.Add(_logoLabel);
            panel.Children.Add(logoRow);
            panel.Children.Add(SliderBoxRow(S("Str_Stamp_CertLogoScale"), 25, 300, _defaults.LogoScale * 100.0, out _logoScale));

            var sigRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var sigBtn = UiKit.Make(S("Str_Stamp_CertSig"), false);
            sigBtn.Click += (_, _2) => ChooseSignature();
            sigRow.Children.Add(sigBtn);
            _sigLabel = new TextBlock
            {
                Text = Path.GetFileName(_sigPath ?? S("Str_Stamp_CertSigNone")),
                Foreground = R("TextSecondary"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, MaxWidth = 200
            };
            sigRow.Children.Add(_sigLabel);
            panel.Children.Add(sigRow);

            panel.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Position")));
            _posCombo = new ComboBox { Height = 26, Margin = new Thickness(0, 0, 0, 10) };
            string[] positions = { "Bottom Right", "Bottom Center", "Top Right", "Top Center", "Top Left", "Bottom Left", "Center", "Custom" };
            int[] posH = { 2, 1, 2, 1, 0, 0, 1, -1 };
            int[] posV = { 2, 2, 0, 0, 0, 2, 1, -1 };
            int sel = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                _posCombo.Items.Add(positions[i]);
                if (posH[i] == _defaults.PosH && posV[i] == _defaults.PosV) sel = i;
            }
            _posCombo.SelectedIndex = sel;
            panel.Children.Add(_posCombo);

            panel.Children.Add(UiKit.GroupLabel(S("Str_Stamp_CertBorder")));
            _borderCombo = new ComboBox { Height = 26, Margin = new Thickness(0, 0, 0, 10) };
            _borderCombo.Items.Add(S("Str_Stamp_CertBorderRect"));
            _borderCombo.Items.Add(S("Str_Stamp_CertBorderRound"));
            _borderCombo.SelectedIndex = Math.Max(0, Math.Min(1, _defaults.Border));
            panel.Children.Add(_borderCombo);

            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            colorRow.Children.Add(new TextBlock { Text = S("Str_Stamp_CertColor"), Foreground = R("TextSecondary"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _colorSwatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(_color), BorderBrush = R("BorderDim"), BorderThickness = new Thickness(1) };
            var colorBtn = new Button { Content = _colorSwatch, Width = 32, Height = 32, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
            colorBtn.Click += (_, _2) => PickColor();
            colorRow.Children.Add(colorBtn);
            panel.Children.Add(colorRow);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var cancelBtn = UiKit.Make(S("Str_Tf_Cancel"), false);
            cancelBtn.Click += (_, _2) => Close();
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            btnRow.Children.Add(cancelBtn);
            var saveBtn = UiKit.Make(S("Str_Tf_Apply"), true);
            saveBtn.Click += (_, _2) => { Save(); Close(); };
            btnRow.Children.Add(saveBtn);
            panel.Children.Add(btnRow);

            Content = panel;
        }

        private void ChooseLogo()
        {
            var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
            if (ofd.ShowDialog() == true)
            {
                _logoPath = ofd.FileName;
                _logoLabel.Text = Path.GetFileName(_logoPath);
            }
        }

        private void ChooseSignature()
        {
            var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
            if (ofd.ShowDialog() == true)
            {
                _sigPath = ofd.FileName;
                _sigLabel.Text = Path.GetFileName(_sigPath);
            }
        }

        private void PickColor()
        {
            var dlg = new ColorPickerDialog(this, _color);
            dlg.ShowDialog();
            _color = dlg.SelectedColor;
            _colorSwatch.Background = new SolidColorBrush(_color);
        }


        private FrameworkElement SliderBoxRow(string label, double min, double max, double value, out Slider slider)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(UiKit.GroupLabel(label));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var s = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), SmallChange = 1, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center };
            var box = UiKit.Field(46);
            box.Text = ((int)Math.Round(s.Value)).ToString();
            box.Margin = new Thickness(8, 0, 0, 0);

            bool guard = false;
            s.ValueChanged += (_, _2) => { if (guard) return; guard = true; box.Text = ((int)Math.Round(s.Value)).ToString(); guard = false; };
            box.TextChanged += (_, _2) => { if (guard) return; if (double.TryParse(box.Text, out double d)) { guard = true; s.Value = Math.Max(min, Math.Min(max, d)); guard = false; } };

            Grid.SetColumn(s, 0); Grid.SetColumn(box, 1);
            grid.Children.Add(s); grid.Children.Add(box);
            panel.Children.Add(grid);
            slider = s;
            return panel;
        }

        private void Save()
        {
            int[] posH = { 2, 1, 2, 1, 0, 0, 1, -1 };
            int[] posV = { 2, 2, 0, 0, 0, 2, 1, -1 };
            int idx = Math.Max(0, _posCombo.SelectedIndex);
            var d = new CertStampStore.CertDefaults
            {
                Name = _nameField.Text?.Trim() ?? "",
                Label = string.IsNullOrWhiteSpace(_labelField.Text) ? "Document seen by:" : _labelField.Text.Trim(),
                LogoPath = _logoPath,
                LogoScale = _logoScale.Value / 100.0,
                SigPath = _sigPath,
                ShowLogo = !string.IsNullOrEmpty(_logoPath),
                ShowSig = !string.IsNullOrEmpty(_sigPath),
                ShowName = !string.IsNullOrWhiteSpace(_nameField.Text),
                PosH = posH[idx],
                PosV = posV[idx],
                Border = Math.Max(0, Math.Min(1, _borderCombo.SelectedIndex)),
                ColorHex = CertStampStore.ColorToHex(_color),
                Scale = 1.0,
                ShowDate = true,
                ShowTime = false
            };
            _store.Save(d);
        }
    }
}
