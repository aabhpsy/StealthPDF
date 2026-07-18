using System.Security.Cryptography.X509Certificates;

namespace StealthPDF.Services.Signing
{
    /// <summary>
    /// A source of a signing certificate (with its private key). Kept as an interface so the only
    /// Windows-only piece is the certificate-store provider; .pfx files (and, later, OS keychains)
    /// keep the rest of the signing module portable for the planned Linux/Mac port.
    /// </summary>
    internal interface ICertificateProvider
    {
        /// <summary>Human-readable label for the UI (file name, or the cert's subject).</summary>
        string DisplayName { get; }

        /// <summary>
        /// Returns the certificate (must have a usable private key). Throws on failure - a wrong
        /// .pfx password, a missing private key, an unreadable file, etc.
        /// </summary>
        X509Certificate2 GetCertificate();
    }
}
