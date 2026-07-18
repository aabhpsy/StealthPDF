namespace StealthPDF
{
    /// <summary>
    /// Build-time constants written or verified by release.ps1.
    /// </summary>
    internal static class BuildInfo
    {
        /// <summary>
        /// SHA256 of pdfium.dll (original bytes, before Costura compression).
        /// Updated by release.ps1 immediately before each build.
        /// All-zeros means the check is disabled (dev / SkipSign builds).
        /// </summary>
        internal const string PdfiumSha256 = "BCA96944D731DD72877116D3472083C847FE307FC58CA39BCE16CBE998C478F1";

        internal const string PdfiumSha256Disabled = "0000000000000000000000000000000000000000000000000000000000000000";
    }
}