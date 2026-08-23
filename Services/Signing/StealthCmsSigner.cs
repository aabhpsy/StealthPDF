using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using PdfSharp.Pdf.Signatures;

namespace StealthPDF.Services.Signing
{
    /// <summary>
    /// A PDF signer that assembles the PKCS#7 / CMS itself with .NET's <see cref="SignedCms"/> rather
    /// than PDFsharp's PdfSharpDefaultSigner. SignedCms drives the certificate's private key through
    /// the modern (CNG) provider, which is what makes cloud / token keys usable - notably Certum
    /// SimplySign, where the default signer throws "An internal error occurred" because it cannot
    /// reach a non-legacy key. It also signs plain software .pfx keys fine, so this is the single
    /// signing path for every certificate source.
    ///
    /// Implements PDFsharp 6.2's <c>IDigitalSigner</c>: PDFsharp hands us the ByteRange content to
    /// sign and drops the returned DER bytes into the /Contents placeholder. A detached CMS over that
    /// content is exactly an adbe.pkcs7.detached PDF signature.
    /// </summary>
    internal sealed class StealthCmsSigner(X509Certificate2 cert) : IDigitalSigner
    {
        private static readonly Oid Sha256 = new("2.16.840.1.101.3.4.2.1");   // id-sha256

        public string CertificateName =>
            cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } n
                ? n : cert.Subject;

        // Reserve generous space in /Contents for the CMS (signer cert + full chain, SHA-256 RSA).
        // No timestamp yet, so 16 KB sits comfortably above a typical 4-8 KB signature.
        public Task<int> GetSignatureSizeAsync() => Task.FromResult(16384);

        public Task<byte[]> GetSignatureAsync(Stream stream)
        {
            // PDFsharp hands us a RangedStream positioned at its END, so reading straight away yields
            // zero bytes ("Cannot create CMS signature for empty content"). Its Seek() throws
            // NotImplementedException ("Cannot seek in a RangedStream") even though CanSeek is true -
            // but the Position setter IS implemented, so rewind with that. Then read SYNCHRONOUSLY;
            // CopyToAsync / ReadAsync return nothing on this stream.
            using var ms = new MemoryStream();
            stream.Position = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                ms.Write(buffer, 0, read);

            byte[] data = ms.ToArray();
            if (data.Length == 0)
                throw new CryptographicException("The content to sign was empty - the PDF stream could not be read.");

            var content = new ContentInfo(data);
            var signedCms = new SignedCms(content, detached: true);

            var signer = new CmsSigner(cert)
            {
                DigestAlgorithm = Sha256,
                IncludeOption = X509IncludeOption.WholeChain,
            };

            // silent:false lets a token / cloud KSP (SimplySign) surface a PIN or confirmation prompt
            // if it needs one, instead of failing outright.
            signedCms.ComputeSignature(signer, silent: false);
            return Task.FromResult(signedCms.Encode());
        }
    }
}
