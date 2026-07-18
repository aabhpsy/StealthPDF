using System.Windows;
using System.Windows.Controls;

namespace StealthPDF
{
    /// <summary>
    /// A tiny modal text-input dialog used by the Stamp window to name/rename a certification preset.
    /// Returns the trimmed text on OK, or null on Cancel/empty. Kept self-contained (no XAML) so it
    /// follows the same code-built pattern as the rest of the Stamp window.
    /// </summary>
    internal sealed class InputDialog : Window
    {
        public string? Answer { get; private set; }
        private readonly TextBox _field;

        internal InputDialog(string title, string prompt, string initial, Window owner)
        {
            // The DialogChrome title bar looks for the literal "StealthPDF" substring in the window
            // title and swaps it for the styled Killer+PDF wordmark. Prefix here so this small
            // input dialog gets the same branded header as Stamp / Transform.
            Title = "StealthPDF - " + title;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            ResizeMode = ResizeMode.NoResize;
            DialogChrome.Configure(this, owner);

            // Solid-background inner panel: DialogChrome.Configure makes the WINDOW transparent
            // (so the rounded corners can show through), which means anything not wrapped in the
            // themed Frame is see-through. Previously the panel was placed directly on the
            // transparent window and the whole dialog rendered invisible over the underlying PDF.
            var body = new Border
            {
                Background = (System.Windows.Media.Brush?)Application.Current.TryFindResource("BgModal")
                             ?? System.Windows.Media.Brushes.Black
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            if (!string.IsNullOrEmpty(prompt))
                panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondary"], FontSize = 11 });
            _field = UiKit.Field();
            _field.Text = initial ?? "";
            _field.SelectAll();
            _field.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(_field);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = UiKit.Make(Loc("Str_Tf_Cancel"), false);
            cancel.Click += (_, _2) => { Answer = null; Close(); };
            cancel.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(cancel);
            var ok = UiKit.Make(Loc("Str_Tf_Apply"), true);
            ok.Click += (_, _2) => { Answer = _field.Text?.Trim(); Close(); };
            row.Children.Add(ok);
            panel.Children.Add(row);

            body.Child = panel;

            // Wrap in the standard rounded-card chrome: title bar with wordmark + close button,
            // themed border and shadow, Esc-to-close. Also makes the dialog draggable by the bar.
            Content = DialogChrome.Frame(this, owner, Title, () => { Answer = null; Close(); }, body);

            // Enter commits, matching the other stamp dialogs.
            KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    Answer = _field.Text?.Trim();
                    Close();
                }
            };
            Loaded += (_, _2) => _field.Focus();
        }

        private static string Loc(string key) => Application.Current.TryFindResource(key) as string ?? key;
    }
}
