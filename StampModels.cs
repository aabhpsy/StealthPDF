using System.Windows.Media;

namespace StealthPDF
{
    internal enum StampKind { PageNumber, Watermark, Certification }

    // The full configuration produced/edited by the Stamp window. One spec can drive page numbers,
    // a watermark, or both, each over its own page range. A spec is the unit that gets re-opened when
    // the user double-clicks a placed stamp, so it carries everything needed to recreate the stamps.
    internal sealed class StampSpec
    {
        // ---- Page numbers ----
        public bool   NumbersEnabled;
        public int    StartNumber = 1;
        public string Format      = "{n}";    // {n} = this page's number, {N} = total
        public int    NumPosH     = 1;         // 0 left, 1 center, 2 right
        public int    NumPosV     = 2;         // 0 top, 1 middle, 2 bottom
        public double NumFontPt   = 12;
        public Color  NumColor    = Colors.Black;
        public string NumRange    = "";        // "" = all pages; else "1-3,5"
        public bool   NumMirror;               // flip left/right each page so numbers sit on the outer edge
        public double NumCustomX  = 0.5;       // used when NumPosH == -1 (Custom): center as a fraction of page
        public double NumCustomY  = 0.92;

        // ---- Watermark ----
        public bool    WmEnabled;
        public bool    WmIsImage;              // false = text, true = image
        public string  WmText    = "DRAFT";
        public string  WmFont    = "Segoe UI";
        public double  WmFontPt  = 64;
        public Color   WmColor   = Color.FromRgb(0x88, 0x88, 0x88);
        public double  WmOpacity = 0.25;       // 0..1
        public double  WmAngle   = 45;         // degrees, counter-clockwise
        public int     WmPosH    = 1;          // 0 left, 1 center, 2 right
        public int     WmPosV    = 1;          // 0 top, 1 middle, 2 bottom
        public string? WmImagePath;            // source image when WmIsImage
        public double  WmScale   = 1.0;        // multiplier on the natural placement size
        public string  WmRange   = "";         // "" = all pages
        public double  WmCustomX = 0.5;        // used when WmPosH == -1 (Custom): center as a fraction of page
        public double  WmCustomY = 0.5;

        // ---- Certification stamp (agency sign-off block) ----
        // A composite, draggable block: logo + "Document seen by:" label + name + signature image +
        // date/time. Each field is independently toggleable. Rendered and burned as ONE unit so it moves
        // as a single block and lands identically on every page in the range. See Stamps.cs.
        public bool    CertEnabled;
        public bool    CertShowLogo;              // logo image on/off
        public string? CertLogoPath;              // source image for the logo (PNG/JPG)
        public double  CertLogoScale = 1.0;       // logo-only scale multiplier, independent of whole-block scale
        public string  CertLabel   = "Document seen by:";   // the lead label, editable
        public bool    CertShowName = true;
        public string  CertName    = "";           // e.g. "John A. Smith"
        public bool    CertShowSig = true;         // signature image on/off
        public string? CertSignatureId;           // id of a saved SignatureStore signature to embed
        public string? CertSigPath;               // OR a one-off image path (when not from the store)
        public bool    CertShowDate = true;        // date line on/off
        public bool    CertShowTime;               // time on the date line
        public string  CertDate    = "";           // "" = today (resolved at apply time); else literal
        // Appearance
        public Color   CertColor   = Color.FromRgb(0x22, 0x22, 0x22);   // border + text
        public double  CertScale   = 1.0;          // whole-block scale multiplier
        public int      CertBorder  = 0;            // 0 rectangle, 1 rounded, 2 none
        public bool      CertWhiteFill = false;      // off by default so the block is transparent over the page; user can enable a fill in the Stamp window
        // Placement (one position drives all pages in the range; -1 = custom draggable)
        public int     CertPosH    = 2;            // 0 left, 1 center, 2 right
        public int     CertPosV    = 2;            // 0 top, 1 middle, 2 bottom
        public double  CertCustomX = 0.78;         // fraction of page (used when CertPosH == -1)
        public double  CertCustomY = 0.86;
        public string  CertRange   = "";           // "" = all pages

        public StampSpec Clone() => (StampSpec)MemberwiseClone();
    }

    // A single placed stamp on one page. It points back at the spec that created it so a double-click
    // on the page can re-open the Stamp window with the original settings. The concrete text/position is
    // derived from Spec + page geometry at render/burn time, so nothing here needs the resolved layout.
    internal sealed class StampInstance
    {
        public int       PageIndex;
        public StampKind Kind;
        public StampSpec Spec = null!;
    }
}
