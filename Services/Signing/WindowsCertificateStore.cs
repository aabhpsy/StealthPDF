using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace StealthPDF.Services.Signing
{
    /// <summary>
    /// Windows-only helper that lists signing-capable certificates from the current user's personal
    /// store, so the UI can offer a picker. Guard calls behind an OS check; on Linux/Mac the app
    /// should fall back to file-based certificates (or a platform keychain) instead.
    /// </summary>
    internal static class WindowsCertificateStore
    {
        public static IReadOnlyList<X509Certificate2> ListSigningCertificates()
        {
            var result = new List<X509Certificate2>();
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var cert in store.Certificates)
            {
                if (cert.HasPrivateKey && CanSign(cert))
                    result.Add(cert);
            }
            return result;
        }

        // Accept a cert whose Key Usage permits digital signatures, or that declares no Key Usage
        // restriction at all (which is permissive per the X.509 spec).
        private static bool CanSign(X509Certificate2 cert)
        {
            foreach (var ext in cert.Extensions)
                if (ext is X509KeyUsageExtension ku)
                    return (ku.KeyUsages &
                            (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) != 0;
            return true;
        }
    }

    /// <summary>Wraps a certificate already chosen from the Windows store as an ICertificateProvider.</summary>
    internal sealed class StoreCertificateProvider(X509Certificate2 cert) : ICertificateProvider
    {
        public string DisplayName =>
            cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } name
                ? name : cert.Subject;

        public X509Certificate2 GetCertificate() => cert;
    }
}
