using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace StealthPDF.Services.Signing
{
    /// <summary>
    /// Loads a signing certificate from a .pfx / .p12 file plus its password. Fully cross-platform,
    /// so this is the default certificate source on every OS.
    /// </summary>
    internal sealed class PfxFileCertificateProvider(string path, string password) : ICertificateProvider
    {
        public string DisplayName => Path.GetFileName(path);

        public X509Certificate2 GetCertificate()
            // Exportable so a Bouncy Castle signer can later pull the private key to build the CMS.
            // (EphemeralKeySet is intentionally not used - it does not exist on .NET Framework.)
            => new(path, password,
                   X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}
