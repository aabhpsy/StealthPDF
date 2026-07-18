using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StealthPDF
{
    // Helpers for the Fill & Sign top bar: control factories. Kept in a companion file so
    // FillSign.cs stays focused on lifecycle (show/hide/exit) and stays small.
    public partial class MainWindow
    {
        // Font/size sub-controls, packaged as a horizontal StackPanel so ShowFillSignBar can drop
        // it into the row with one Add. Picks mirror into _textFontName / _textFontSize.
        private FrameworkElement BuildFillSignFontControls()
        {
            var wrap = new StackPanel { Orientation = Orientation.Horizontal };

            var fontCombo = new ComboBox
            {
                Width = 160, Height = 26,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc("Str_FS_Font"),
                Margin = new Thickness(2, 0, 6, 0),
                IsEditable = false,
            };
            foreach (var f in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
                fontCombo.Items.Add(f.Source);
            fontCombo.SelectedItem = _textFontName;
            if (fontCombo.SelectedItem is null && fontCombo.Items.Count > 0) fontCombo.SelectedIndex = 0;
            fontCombo.SelectionChanged += (_, _2) =>
            {
                if (fontCombo.SelectedItem is string name) { _textFontName = name; ApplyTextStyleToActiveBox(); }
            };
            wrap.Children.Add(fontCombo);

            var sizeCombo = new ComboBox
            {
                Width = 60, Height = 26,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc("Str_FS_Size"),
                Margin = new Thickness(0, 0, 6, 0),
                IsEditable = true,
            };
            foreach (var s in new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 })
                sizeCombo.Items.Add(s.ToString());
            sizeCombo.Text = ((int)_textFontSize).ToString();
            void ApplyPickedSize()
            {
                if (double.TryParse(sizeCombo.Text, out double v) && v >= 4 && v <= 400)
                { _textFontSize = v; ApplyTextStyleToActiveBox(); }
                else sizeCombo.Text = ((int)_textFontSize).ToString();
            }
            sizeCombo.SelectionChanged += (_, _2) => ApplyPickedSize();
            sizeCombo.LostFocus         += (_, _2) => ApplyPickedSize();
            sizeCombo.KeyDown           += (_, k) => { if (k.Key == System.Windows.Input.Key.Enter) ApplyPickedSize(); };
            wrap.Children.Add(sizeCombo);

            return wrap;
        }

        private Button FillSignModeButton(string label, string glyph, FillSignMode mode)
        {
            var btn = new Button
            {
                Padding = new Thickness(10, 3, 10, 3), Height = 26,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = TryFindResource("DarkButton") as Style,
                FontFamily = UiKit.UiFont, FontSize = 11, Tag = mode,
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = UiKit.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            btn.Content = sp;
            btn.Click += (_, _2) => ApplyFillSignMode(mode);
            return btn;
        }

        private Button FillSignStyleToggle(string label, Func<bool> get, Action<bool> set,
            FontWeight? fontWeight = null, bool italic = false)
        {
            var btn = new Button
            {
                Content = label,
                Width = 28, Height = 26, Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = TryFindResource("DarkButton") as Style,
                FontFamily = UiKit.UiFont, FontSize = 12,
                FontWeight = fontWeight ?? FontWeights.SemiBold,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            };
            void Refresh()
            {
                bool active = get();
                btn.Background = active ? (Brush)FindResource("SelectionBg") : (Brush)FindResource("BgPanel");
                btn.Foreground = active ? (Brush)FindResource("SelectionFg") : (Brush)FindResource("TextPrimary");
            }
            Refresh();
            btn.Click += (_, _2) => { set(!get()); Refresh(); ApplyTextStyleToActiveBox(); };
            return btn;
        }
    }
}
