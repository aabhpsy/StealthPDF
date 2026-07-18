using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StealthPDF.Services
{
    /// <summary>
    /// Keeps the single-exe build self-sufficient for OCR. The native Tesseract DLLs (x64) and the bundled
    /// language data are embedded as resources and self-extracted on first use, the same pattern Costura
    /// uses for the managed assemblies. Native libs go in a per-version cache (they must match the app);
    /// language data goes in a STABLE folder so user-downloaded packs survive app updates. Thread-safe.
    /// </summary>
    internal static class OcrNativeBootstrap
    {
        private const string NativePrefix = "StealthPDF.OcrNative.";
        private const string TessDataPrefix = "StealthPDF.OcrTessData.";

        private static readonly object _gate = new();
        private static bool _langReady;
        private static bool _nativeReady;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        /// <summary>
        /// Version-independent tessdata folder. The bundled English is extracted here on first use, and
        /// user-downloaded language packs are written here too, so they persist across app updates.
        /// </summary>
        public static string TessDataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StealthPDF", "tessdata");

        /// <summary>
        /// Ensures the bundled language data (English) is present in <see cref="TessDataDir"/> and returns
        /// that folder. Light - does not touch the native libraries, so it is safe to call just to inspect
        /// or list installed languages (e.g. when building the language menu).
        /// </summary>
        public static string EnsureLanguageData()
        {
            if (_langReady) return TessDataDir;
            lock (_gate)
            {
                if (_langReady) return TessDataDir;
                Directory.CreateDirectory(TessDataDir);

                var asm = typeof(OcrNativeBootstrap).Assembly;
                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (res.StartsWith(TessDataPrefix, StringComparison.Ordinal))
                    {
                        string file = res[TessDataPrefix.Length..];
                        ExtractResource(asm, res, Path.Combine(TessDataDir, file), onlyIfMissing: true);
                    }
                }

                _langReady = true;
                return TessDataDir;
            }
        }

        /// <summary>
        /// Extracts the native libs to a per-version cache, ensures language data, configures Tesseract's
        /// native loader, and returns the tessdata folder for OcrService. Call before constructing OcrService.
        /// </summary>
        public static string EnsureReady()
        {
            EnsureLanguageData();
            if (_nativeReady) return TessDataDir;
            lock (_gate)
            {
                if (_nativeReady) return TessDataDir;

                var asm = typeof(OcrNativeBootstrap).Assembly;
                string version = asm.GetName().Version?.ToString() ?? "0";
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StealthPDF", "ocr", version);
                string nativeDir = Path.Combine(baseDir, "x64");
                Directory.CreateDirectory(nativeDir);

                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (res.StartsWith(NativePrefix, StringComparison.Ordinal))
                    {
                        string file = res[NativePrefix.Length..];
                        // Tesseract's loader looks in the x64 subfolder; the flat copy covers any loader
                        // path that does not append the platform name.
                        ExtractResource(asm, res, Path.Combine(nativeDir, file), onlyIfMissing: false);
                        ExtractResource(asm, res, Path.Combine(baseDir, file), onlyIfMissing: false);
                    }
                }

                // Point Tesseract's native loader at the cache. Reflection avoids a compile-time bind in
                // case the loader type's visibility differs across package versions; the preload below is
                // the hard guarantee regardless.
                try
                {
                    var loaderType = Type.GetType("InteropDotNet.LibraryLoader, Tesseract");
                    object? instance = loaderType?
                        .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
                        .GetValue(null);
                    loaderType?.GetProperty("CustomSearchPath")?.SetValue(instance, baseDir);
                }
                catch { /* fall through to the preload */ }

                // Belt and suspenders: add the native dir to the DLL search path and preload the libs.
                // leptonica must load before tesseract50, which depends on it.
                try
                {
                    SetDllDirectory(nativeDir);
                    foreach (string dll in Directory.GetFiles(nativeDir, "leptonica*.dll")) LoadLibrary(dll);
                    foreach (string dll in Directory.GetFiles(nativeDir, "tesseract*.dll")) LoadLibrary(dll);
                }
                catch { /* loader search paths above still apply */ }

                _nativeReady = true;
                return TessDataDir;
            }
        }

        private static void ExtractResource(Assembly asm, string resourceName, string targetPath, bool onlyIfMissing)
        {
            // Language data is extracted only-if-missing: a user-downloaded pack (e.g. a high-quality model,
            // or an HQ English) must never be clobbered by the bundled copy on the next launch. Native libs
            // keep the length check so a version change refreshes them.
            if (onlyIfMissing && File.Exists(targetPath)) return;

            using var src = asm.GetManifestResourceStream(resourceName);
            if (src == null) return;

            if (!onlyIfMissing && File.Exists(targetPath) && new FileInfo(targetPath).Length == src.Length) return;

            string tmp = targetPath + ".tmp";
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tmp, targetPath);
        }
    }
}
