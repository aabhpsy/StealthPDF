using System;
using System.Windows;

namespace StealthPDF.Services
{
    internal enum Locale { EnUS, EnUK, NpNP, Es, ZhTW, ZhCN, Bn, TrTR, De, Fr }

    internal static class LocaleManager
    {
        private static Locale _current = Locale.EnUS;

        public static Locale Current => _current;

        /// <summary>
        /// Call once at startup (after ThemeManager.Initialize) to restore the saved locale.
        /// </summary>
        public static void Initialize()
        {
            var saved = App.GetSetting("Locale");
            _current = Enum.TryParse<Locale>(saved, out var l) ? l : Locale.EnUS;
            ApplyInternal(_current);
        }

        /// <summary>
        /// Switch locale, persist choice, and hot-swap the string ResourceDictionary.
        /// </summary>
        public static void Apply(Locale locale)
        {
            _current = locale;
            App.SetSetting("Locale", locale.ToString());
            ApplyInternal(locale);
        }

        // ── Internal ─────────────────────────────────────────────────────

        private static void ApplyInternal(Locale locale)
        {
            var merged = Application.Current.Resources.MergedDictionaries;

            // [0] theme. [1] en-US BASE - always present so any partial locale falls back to English for
            // keys it doesn't translate. [2] the chosen locale's overrides (absent for English).
            if (merged.Count > 1)
                merged[1] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") };

            Uri? overrideUri = locale switch
            {
                Locale.EnUK => new Uri("pack://application:,,,/Strings/en-UK.xaml"),
                Locale.NpNP => new Uri("pack://application:,,,/Strings/np-NP.xaml"),
                Locale.Es   => new Uri("pack://application:,,,/Strings/es.xaml"),
                Locale.Fr   => new Uri("pack://application:,,,/Strings/fr-FR.xaml"),
                Locale.ZhTW => new Uri("pack://application:,,,/Strings/zh-TW.xaml"),
                Locale.ZhCN => new Uri("pack://application:,,,/Strings/zh-CN.xaml"),
                Locale.Bn   => new Uri("pack://application:,,,/Strings/bn.xaml"),
                Locale.TrTR => new Uri("pack://application:,,,/Strings/tr-TR.xaml"),
                Locale.De   => new Uri("pack://application:,,,/Strings/de-DE.xaml"),
                _           => null,   // English (en-US): base only
            };

            if (overrideUri is not null)
            {
                var ov = new ResourceDictionary { Source = overrideUri };
                if (merged.Count > 2) merged[2] = ov; else merged.Add(ov);
            }
            else if (merged.Count > 2)
            {
                merged.RemoveAt(2);
            }
        }
    }
}
