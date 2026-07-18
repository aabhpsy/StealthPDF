using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StealthPDF
{
    // ============================================================
    // Fill & Sign - Adobe-style consolidated signing tool
    // ============================================================
    //
    // Lightweight meta-tool that composes three existing modes into one focused workflow for
    // signing / filling scanned contracts (no interactive form fields, need to type on the flat
    // page):
    //   1. Signature - opens the signature popup (pick/draw/upload), then EditTool.Signature.
    //   2. Fill Text - EditTool.Text preset to ~11pt for typical form-field text.
    //   3. Add Text  - Same as Fill Text but respects the font/size/B/I picked in the FS top bar.
    //
    // The top bar mirrors its picks into _textFontName / _textFontSize / _textBold / _textItalic
    // so the Text tool immediately picks them up when the user clicks the page.
    public partial class MainWindow
    {
        private Border? _fillSignBar;
        private enum FillSignMode { Signature, Fill, AddText }
        private FillSignMode _fillSignMode = FillSignMode.Signature;
        private bool _fillSignSizedForFill;

        private void ToolFillSign_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }
            if (_fillSignBar is not null) { ExitFillSign(); return; }
            ShowFillSignBar();
            ApplyFillSignMode(FillSignMode.Signature);
        }

        // Builds and shows the Fill & Sign top bar. Hosted in the same container as other floating
        // panels (PagePreviewPanel's parent Grid) so it layers above the page area.
        private void ShowFillSignBar()
        {
            var host = PagePreviewPanel.Parent as Grid;
            if (host is null) return;

            var bar = new Border
            {
                Background = (Brush)FindResource("BgPanel"),
                BorderBrush = (Brush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black
                },
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(FillSignModeButton(Loc("Str_FS_Mode_Signature"), "\uEE56", FillSignMode.Signature));
            row.Children.Add(FillSignModeButton(Loc("Str_FS_Mode_Fill"),      "\uE8D2", FillSignMode.Fill));
            row.Children.Add(FillSignModeButton(Loc("Str_FS_Mode_AddText"),   "\uE8D2", FillSignMode.AddText));
            row.Children.Add(new Rectangle { Width = 1, Fill = (Brush)FindResource("BorderDim"), Margin = new Thickness(10, 4, 10, 4) });
            row.Children.Add(BuildFillSignFontControls());
            row.Children.Add(FillSignStyleToggle("B", () => _textBold,   v => _textBold   = v, fontWeight: FontWeights.Bold));
            row.Children.Add(FillSignStyleToggle("I", () => _textItalic, v => _textItalic = v, italic: true));
            row.Children.Add(new Rectangle { Width = 1, Fill = (Brush)FindResource("BorderDim"), Margin = new Thickness(10, 4, 10, 4) });

            var exitBtn = new Button
            {
                Content = Loc("Str_FS_Exit"),
                Padding = new Thickness(12, 3, 12, 3), Height = 26,
                VerticalAlignment = VerticalAlignment.Center,
                Style = TryFindResource("DarkButton") as Style,
                FontFamily = UiKit.UiFont, FontSize = 11,
            };
            exitBtn.Click += (_, _2) => ExitFillSign();
            row.Children.Add(exitBtn);

            bar.Child = row;
            host.Children.Add(bar);
            _fillSignBar = bar;
        }
    }
}
