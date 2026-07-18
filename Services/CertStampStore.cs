using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace StealthPDF.Services
{
    /// <summary>
    /// Persists the agency certification stamp defaults (name, logo, signature choice, which fields
    /// show) to %LocalAppData%\StealthPDF\cert-stamp.json so a staff member configures it once and the
    /// Stamp tool pre-fills it on every later session. Mirrors the SignatureStore pattern. The date is
    /// intentionally NOT persisted (it resolves to "today" at apply time), so re-opening tomorrow still
    /// stamps the correct date.
    ///
    /// In addition to the single "last used" default, this store keeps a LIST of named presets
    /// (cert-stamp-presets.json) so a staff member can save several configurations - e.g. one per
    /// signatory - and recall any of them instantly from the Stamp window's preset dropdown.
    /// </summary>
    internal sealed class CertStampStore
    {
        private static readonly string DefaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StealthPDF");

        private static readonly string DefaultFile = Path.Combine(DefaultDir, "cert-stamp.json");
        private static readonly string PresetsFile = Path.Combine(DefaultDir, "cert-stamp-presets.json");

        private readonly string _file;
        private readonly string _presetsFile;
        public CertStampStore() : this(DefaultFile, PresetsFile) { }
        internal CertStampStore(string file) : this(file, Path.Combine(Path.GetDirectoryName(file) ?? "", "cert-stamp-presets.json")) { }
        internal CertStampStore(string file, string presetsFile) { _file = file; _presetsFile = presetsFile; }

        // A plain serializable snapshot of the Cert* fields on StampSpec. Kept separate from StampSpec so
        // the rest of the spec (page numbers, watermark) is never persisted here. A preset is a CertDefaults
        // plus a display name; the date string travels along so a back-dated preset is reproduced exactly.
        internal sealed class CertDefaults
        {
            public string? PresetName { get; set; }      // only set when this defaults object is a saved preset
            public bool   ShowLogo { get; set; }
            public string? LogoPath { get; set; }
            public double LogoScale { get; set; } = 1.0;
            public string Label { get; set; } = "Document seen by:";
            public bool   ShowName { get; set; } = true;
            public string Name { get; set; } = "";
            public bool   ShowSig { get; set; } = true;
            public string? SignatureId { get; set; }
            public string? SigPath { get; set; }
            public bool   ShowDate { get; set; } = true;
            public bool   ShowTime { get; set; }
            public string Date { get; set; } = "";       // blank = today at apply time; a preset may carry a fixed date
            public string ColorHex { get; set; } = "#222222";
            public double Scale { get; set; } = 1.0;
            public int    Border { get; set; } = 0;
            public bool   WhiteFill { get; set; } = false;
            public int    PosH { get; set; } = 2;
            public int    PosV { get; set; } = 2;
            public double CustomX { get; set; } = 0.78;
            public double CustomY { get; set; } = 0.86;
        }

        public CertDefaults? Load()
        {
            try
            {
                if (!File.Exists(_file)) return null;
                return JsonSerializer.Deserialize<CertDefaults>(File.ReadAllText(_file));
            }
            catch { return null; }
        }

        public void Save(CertDefaults d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best effort */ }
        }

        // ---- Named presets ----

        // The whole list, in the order it was saved. Returns an empty list on any error.
        public List<CertDefaults> LoadPresets()
        {
            try
            {
                if (!File.Exists(_presetsFile)) return new List<CertDefaults>();
                return JsonSerializer.Deserialize<List<CertDefaults>>(File.ReadAllText(_presetsFile)) ?? new List<CertDefaults>();
            }
            catch { return new List<CertDefaults>(); }
        }

        // Adds (or, when a preset with the same trimmed name exists, replaces) a preset and persists the list.
        public void SavePreset(CertDefaults preset)
        {
            if (preset is null) return;
            var name = (preset.PresetName ?? "").Trim();
            if (name.Length == 0) return;
            var list = LoadPresets();
            for (int i = 0; i < list.Count; i++)
                if ((list[i].PresetName ?? "").Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                { list[i] = preset; PersistPresets(list); return; }
            list.Add(preset);
            PersistPresets(list);
        }

        public void DeletePreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var list = LoadPresets();
            if (list.RemoveAll(p => (p.PresetName ?? "").Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
                PersistPresets(list);
        }

        public void RenamePreset(string oldName, string newName)
        {
            oldName = (oldName ?? "").Trim(); newName = (newName ?? "").Trim();
            if (oldName.Length == 0 || newName.Length == 0) return;
            var list = LoadPresets();
            if (list.Exists(p => (p.PresetName ?? "").Trim().Equals(newName, StringComparison.OrdinalIgnoreCase))) return;
            for (int i = 0; i < list.Count; i++)
                if ((list[i].PresetName ?? "").Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase))
                { list[i].PresetName = newName; PersistPresets(list); return; }
        }

        private void PersistPresets(List<CertDefaults> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_presetsFile)!);
                File.WriteAllText(_presetsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best effort */ }
        }

        // ---- Bridge to/from StampSpec ----

        internal static string ColorToHex(Color c) =>
            $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        internal static Color ColorFromHex(string hex)
        {
            try
            {
                if (hex.StartsWith("#")) hex = hex[1..];
                if (hex.Length == 6)
                    return Color.FromRgb(byte.Parse(hex[0..2], System.Globalization.NumberStyles.HexNumber),
                                        byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                                        byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
            }
            catch { }
            return Color.FromRgb(0x22, 0x22, 0x22);
        }

        public void SaveFromSpec(StampSpec s) => Save(ToDefaults(s));

        // Inverse of ToDefaults: builds a StampSpec seeded with a preset's values (for loading into the UI).
        internal static StampSpec CertDefaultsToSpec(CertDefaults d)
        {
            var s = new StampSpec();
            ApplyToSpec(d, s);
            return s;
        }

        internal static CertDefaults ToDefaults(StampSpec s) => new()
        {
            ShowLogo = s.CertShowLogo, LogoPath = s.CertLogoPath, LogoScale = s.CertLogoScale,
            Label = s.CertLabel, ShowName = s.CertShowName, Name = s.CertName,
            ShowSig = s.CertShowSig, SignatureId = s.CertSignatureId, SigPath = s.CertSigPath,
            ShowDate = s.CertShowDate, ShowTime = s.CertShowTime, Date = s.CertDate ?? "",
            ColorHex = ColorToHex(s.CertColor), Scale = s.CertScale, Border = s.CertBorder,
            WhiteFill = s.CertWhiteFill, PosH = s.CertPosH, PosV = s.CertPosV,
            CustomX = s.CertCustomX, CustomY = s.CertCustomY,
        };

        internal static void ApplyToSpec(CertDefaults d, StampSpec s)
        {
            s.CertShowLogo = d.ShowLogo; s.CertLogoPath = d.LogoPath; s.CertLogoScale = d.LogoScale <= 0 ? 1.0 : d.LogoScale;
            s.CertLabel = d.Label; s.CertShowName = d.ShowName; s.CertName = d.Name;
            s.CertShowSig = d.ShowSig; s.CertSignatureId = d.SignatureId; s.CertSigPath = d.SigPath;
            s.CertShowDate = d.ShowDate; s.CertShowTime = d.ShowTime; s.CertDate = d.Date ?? "";
            s.CertColor = ColorFromHex(d.ColorHex); s.CertScale = d.Scale; s.CertBorder = d.Border;
            s.CertWhiteFill = d.WhiteFill; s.CertPosH = d.PosH; s.CertPosV = d.PosV;
            s.CertCustomX = d.CustomX; s.CertCustomY = d.CustomY;
        }
    }
}
