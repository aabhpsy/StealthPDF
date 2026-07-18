using System;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

namespace StealthPDF.Services.Signing
{
    /// <summary>
    /// Cryptographic (PAdES / PKCS#7) PDF signing, isolated from the rest of the app. Everything else
    /// in StealthPDF uses PdfSharpCore; this module uses PDFsharp 6.2 (the <c>PdfSharp.*</c> namespace),
    /// and the two coexist without clashing. There are deliberately no WPF or Windows-only types here,
    /// so the whole module ports to Avalonia / Linux / Mac unchanged.
    ///
    /// v1 milestone: an invisible-but-valid signature (Adobe still lists it in the Signatures panel),
    /// SHA-256, no timestamp. The .NET Framework build of PDFsharp cannot timestamp; once the plumbing
    /// is validated we swap the default signer for a Bouncy Castle IDigitalSigner to get portable
    /// crypto plus timestamps/LTV. A visible signature appearance comes after that.
    /// </summary>
    internal sealed class PdfSigner
    {
        public sealed record SignInfo(string Reason, string Location, string Contact);

        /// <summary>
        /// Signs <paramref name="inputPath"/> with <paramref name="cert"/> and writes the signed copy
        /// to <paramref name="outputPath"/>. Throws on failure.
        /// </summary>
        public void Sign(string inputPath, string outputPath, X509Certificate2 cert, SignInfo info)
        {
            if (cert is null) throw new ArgumentNullException(nameof(cert));
            if (!cert.HasPrivateKey)
                throw new InvalidOperationException(
                    "The selected certificate has no private key, so it cannot sign.");

            // Open the finalized PDF. (NOTE: confirm the exact open-mode enum on first build - PDFsharp
            // 6.2 may want PdfReadAccuracy / a different overload here.)
            using PdfDocument document = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);

            var options = new DigitalSignatureOptions
            {
                ContactInfo = info.Contact,
                Location    = info.Location,
                Reason      = info.Reason,
                // Invisible for v1: a zero rectangle and no AppearanceHandler. If PDFsharp requires a
                // non-null AppearanceHandler, that is the first thing to add (a minimal drawn field).
                Rectangle   = new XRect(0, 0, 0, 0),
            };

            // Our own SignedCms-based signer: drives the cert's modern (CNG) key provider, so it
            // works with cloud / token keys (Certum SimplySign) as well as software .pfx keys, where
            // PdfSharpDefaultSigner throws "An internal error occurred". Same IDigitalSigner slot, so a
            // Bouncy Castle variant can later swap in here for portability + timestamps.
            var signer = new KillerCmsSigner(cert);

            // Associates the signer + options with the document; the signature is produced on Save.
            _ = DigitalSignatureHandler.ForDocument(document, signer, options);

            document.Save(outputPath);
        }
    }
}
