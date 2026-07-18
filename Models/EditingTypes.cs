using System;
using System.Windows;
using System.Windows.Media;

namespace StealthPDF
{
    public enum EditTool { Select, Text, Edit, Highlight, Strikethrough, Underline, Draw, Signature, Image, Crop, Line, Rotate }

    /// <summary>How a HighlightAnnotation paints over its bounds.</summary>
    public enum HighlightStyle { Fill, Strikethrough, Underline }

    public abstract class PageAnnotation
    {
        public int PageIndex { get; set; }
        // Links a text-edit cover to its replacement text (same non-empty id on both). A cover with a
        // PairId renders dashed (it's "paired"); when the partner text is deleted the cover's PairId is
        // cleared and it renders as a solid box. Empty for everything else.
        public string PairId { get; set; } = "";

        // Groups arbitrary annotations so they select and move together (same non-empty id on every
        // member). Independent of PairId. Empty when the annotation isn't grouped.
        public string GroupId { get; set; } = "";
    }

    /// <summary>
    /// Base class for placed/resizable annotations (signature, image).
    /// Carries the shared position, scale, and source-dimension properties used by the resize handle.
    /// </summary>
    public abstract class PlacedAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public double Scale { get; set; } = 0.5;
        public double SourceWidth { get; set; } = 400;
        public double SourceHeight { get; set; } = 150;

        // Runtime-only cache of the decoded image (for image signatures / placed images). Held in
        // memory so a resize-drag doesn't re-decode the Base64 on every mouse tick. Not serialized;
        // the immutable ImageData stays the source of truth.
        public System.Windows.Media.Imaging.BitmapSource? CachedBitmap;
    }

    public class TextAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public string Content { get; set; } = "";
        public double FontSize { get; set; } = 14;
        // Typeface and style. FontName is a font-family name (any installed system font). Bold/Italic/Strike
        // apply to the whole box. Defaults keep text placed before these existed rendering as plain Segoe UI.
        public string FontName { get; set; } = "Segoe UI";
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Strike { get; set; }
        public bool Underline { get; set; }
        public byte ColorR { get; set; } = 0;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        // Box geometry. Width is fixed (text wraps to it); Height auto-grows to fit the wrapped text.
        public double Width { get; set; } = 200;
        public double Height { get; set; } = 28;

        // Optional background fill (the "whiteout"/highlight behind the text). BgA == 0 means no fill.
        public byte BgR { get; set; } = 255;
        public byte BgG { get; set; } = 255;
        public byte BgB { get; set; } = 255;
        public byte BgA { get; set; } = 0;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        public Color GetFill() => Color.FromArgb(BgA, BgR, BgG, BgB);
        public void SetFill(Color c) { BgR = c.R; BgG = c.G; BgB = c.B; BgA = c.A; }
        public bool HasFill => BgA > 0;
    }

    public class InkAnnotation : PageAnnotation
    {
        public List<Point> Points { get; set; } = [];
        public double StrokeWidth { get; set; } = 2;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }
    }

    // One brush-eraser pass over a highlight: a stroke (canvas-space points) of the given radius. The
    // highlight renders as its rectangle MINUS the union of these widened strokes - one anti-aliased
    // geometry, so the erased edges are smooth curves, not blocky steps or seamed strips.
    public sealed class HighlightErase
    {
        public System.Collections.Generic.List<Point> Points { get; set; } = [];
        public double Radius { get; set; }
    }

    public class HighlightAnnotation : PageAnnotation
    {
        public Rect Bounds { get; set; }
        // Brush-eraser passes carved out of this highlight (null = untouched solid rect). Only Fill-style
        // highlights are ever carved.
        public System.Collections.Generic.List<HighlightErase>? Erases { get; set; }
        public HighlightStyle Style { get; set; } = HighlightStyle.Fill;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 255;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 80;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public virtual void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        /// <summary>
        /// The actual rectangle painted for this annotation. Fill uses the whole bounds;
        /// strikethrough is a thin band at the vertical centre; underline sits at the bottom.
        /// </summary>
        public Rect DrawRect()
        {
            double t = Math.Max(2.0, Bounds.Height * 0.10);
            switch (Style)
            {
                case HighlightStyle.Strikethrough:
                    return new Rect(Bounds.X, Bounds.Y + Bounds.Height / 2 - t / 2, Bounds.Width, t);
                case HighlightStyle.Underline:
                    return new Rect(Bounds.X, Bounds.Y + Bounds.Height - t, Bounds.Width, t);
                default:
                    return Bounds;
            }
        }
    }

    /// <summary>
    /// An opaque filled rectangle that covers ("erases") existing PDF content - the background half
    /// of a text edit. Subclasses HighlightAnnotation so it inherits all rect plumbing (render, drag,
    /// corner-resize, hit-test, export) for free; the only differences are an opaque default fill and
    /// a SetColor that can never go translucent (a see-through cover would let the old text ghost
    /// through, the exact bug this feature exists to avoid). The paired replacement text is a normal
    /// TextAnnotation placed on top, so it is independently editable, movable, and recolorable.
    /// </summary>
    public class CoverAnnotation : HighlightAnnotation
    {
        public CoverAnnotation()
        {
            ColorR = 255; ColorG = 255; ColorB = 255; ColorA = 255;   // opaque white by default
            Style = HighlightStyle.Fill;
        }

        /// <summary>Recolor the cover but keep it fully opaque - drop any alpha the caller passed.</summary>
        public override void SetColor(Color c) => base.SetColor(Color.FromArgb(255, c.R, c.G, c.B));
    }

    /// <summary>
    /// A signature placed on a PDF page: either ink strokes or an imported image.
    /// </summary>
    public class SignatureAnnotation : PlacedAnnotation
    {
        public List<List<Point>> Strokes { get; set; } = [];
        /// <summary>Pen thickness (DIPs at source scale); multiplied by Scale when rendered.</summary>
        public double StrokeWidth { get; set; } = 2.5;
        /// <summary>Base-64 encoded PNG. Non-null = image sig; null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }

    /// <summary>
    /// An image placed on a PDF page as a resizable annotation.
    /// </summary>
    public class ImageAnnotation : PlacedAnnotation
    {
        /// <summary>Base-64 encoded image bytes (PNG, JPG, BMP, etc.).</summary>
        public string ImageData { get; set; } = "";
    }

    /// <summary>
    /// A point that can be serialized to JSON (WPF Point doesn't serialize well).
    /// </summary>
    public class SerializablePoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// A saved signature stored in the user's AppData for reuse.
    /// </summary>
    /// <summary>Distinguishes a full signature from a (smaller) initials stamp. Default is Signature
    /// so signatures saved before this field existed still deserialize correctly.</summary>
    public enum SignatureKind { Signature, Initials }

    public class SavedSignature
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Signature";
        /// <summary>Whether this is a full signature or an initials stamp. Drives which popup section
        /// it appears in and the default placement scale.</summary>
        public SignatureKind Kind { get; set; } = SignatureKind.Signature;
        /// <summary>Pen thickness the signature was drawn with (DIPs at CanvasWidth/Height scale).</summary>
        public double StrokeWidth { get; set; } = 2.5;
        public List<List<SerializablePoint>> Strokes { get; set; } = [];
        public double CanvasWidth { get; set; } = 400;
        public double CanvasHeight { get; set; } = 150;
        /// <summary>Base-64 encoded PNG for imported image signatures. Null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }
}
