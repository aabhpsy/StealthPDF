using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace StealthPDF.Services
{
    /// <summary>
    /// Launches the out-of-process PyMuPDF compression helper.
    ///
    /// The main app is x64 (for pdfium/Tesseract). PyMuPDF is a C-extension that ships its own
    /// native MuPDF engine (mupdfcpp64.dll), so it runs in its own Python process. A portable
    /// Python 3.12 embeddable + PyMuPDF is deployed next to the app at build time under
    /// <c>PdfHelper\</c> (see CopyPdfHelperOutput target in StealthPDF.csproj). This class locates
    /// it, runs <c>compress_pdf.py compress ...</c>, and parses the stdout protocol.
    ///
    /// Protocol (stdout only):
    ///   PROGRESS\t&lt;page&gt;\t&lt;pageCount&gt;  (live progress, page-granular; flushed per page)

    ///   DONE\t&lt;beforeBytes&gt;\t&lt;afterBytes&gt;\t&lt;imagesProcessed&gt;\t&lt;imagesDownsampled&gt;   exit 0
    ///   ERROR\t&lt;message&gt;                                                            exit 1
    ///
    /// The helper is the source of truth for compression: image-level replacement preserves page
    /// rotation, text, and vector art, and only downsamples images that exceed the target DPI
    /// (never upsamples). See compress_pdf.py for the details.
    /// </summary>
    internal static class PdfCompressHelper
    {
        // Relative to the app exe. Populated by the build's CopyPdfHelperOutput target.
        private const string HelperFolder = "PdfHelper";
        private const string PythonExe = "python.exe";
        private const string ScriptName = "compress_pdf.py";

        /// <summary>
        /// Runs the compression. Blocks until the helper exits. Throws on helper failure or if the
        /// bundle is missing. Cancellation kills the helper process. onProgress receives
        /// (page, pageCount) updates as the helper reports them - invoked from a pooled thread,
        /// so callers must marshal to the UI thread themselves.
        /// </summary>
        public static void RunCompress(string src, string dest, int targetDpi, int jpegQuality,
            bool grayscale, CancellationToken ct, Action<int, int>? onProgress = null)
        {
            var (pythonExe, script, workDir) = LocateHelper();
            if (pythonExe == null)
                throw new InvalidOperationException(
                    "PDF compression helper (PyMuPDF) is not installed. Reinstall StealthPDF.");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                // Quote paths so spaces in the user's temp/output paths survive.
                Arguments = $"\"{script}\" compress \"{src}\" \"{dest}\" {targetDpi} {jpegQuality} {(grayscale ? 1 : 0)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // PyMuPDF loads mupdfcpp64.dll from its package folder; set the working dir to the
                // bundle root so its DLL search resolves correctly.
                WorkingDirectory = workDir,
            };

            using var p = Process.Start(psi);
            if (p == null)
                throw new InvalidOperationException("Failed to start the PDF compression helper.");

            // Read stdout/stderr on the helper's OWN background threads (BeginOutput/ErrorReadLine)
            // so this method never blocks on ReadToEnd. The previous synchronous read blocked
            // the caller for the entire compression run, meaning the Esc/Cancel token check
            // below was unreachable - the app looked frozen for large PDFs. The buffered lines
            // are collected into local StringBuilders and consumed after the process exits.
            var stdoutBuf = new System.Text.StringBuilder();
            var stderrBuf = new System.Text.StringBuilder();
            int lastPage = 0, lastTotal = 0;   // latest PROGRESS values (used in the timeout message)
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                // Live progress lines from the helper: forward them to the caller's callback
                // instead of buffering them with the final DONE/ERROR protocol output, so the
                // busy overlay animates on large files instead of looking frozen.
                if (e.Data.StartsWith("PROGRESS\t", StringComparison.Ordinal))
                {
                    var parts = e.Data.Split('\t');
                    if (parts.Length == 3
                        && int.TryParse(parts[1], out int cur) && int.TryParse(parts[2], out int tot))
                    {
                        lastPage = cur; lastTotal = tot;
                        try { onProgress?.Invoke(cur, tot); } catch { /* progress must not break the run */ }
                    }
                    return;
                }
                lock (stdoutBuf) stdoutBuf.AppendLine(e.Data);
            };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (stderrBuf) stderrBuf.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Poll-wait so cancellation can interrupt promptly. A 60-minute ceiling guards
            // runaway helpers (book-scale files legitimately take many minutes).
            var deadline = DateTime.UtcNow.AddMinutes(60);
            bool exited = false;
            while (!(exited = p.WaitForExit(200)))
            {
                if (ct.IsCancellationRequested)
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(2000); } catch { }
                    ct.ThrowIfCancellationRequested();
                }
                if (DateTime.UtcNow > deadline)
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException(lastTotal > 0
                        ? $"PDF compression timed out after 60 minutes (last progress: page {lastPage} of {lastTotal}). Try a lower DPI or JPEG quality, or split the document first."
                        : "PDF compression timed out after 60 minutes. Try a lower DPI or JPEG quality, or split the document first.");
                }
            }
            // Flush any final buffered output the async readers have not yet delivered.
            try { p.WaitForExit(); } catch { }
            string stdout, stderr;
            lock (stdoutBuf) stdout = stdoutBuf.ToString();
            lock (stderrBuf) stderr = stderrBuf.ToString();

            if (ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();

            if (p.ExitCode != 0)
            {
                string msg = ParseError(stdout) ?? stderr?.Trim() ?? $"helper exited with code {p.ExitCode}";
                throw new InvalidOperationException($"PDF compression failed: {msg}");
            }

            // Sanity: the helper must report DONE on stdout. If it produced nothing usable, fail.
            if (!stdout.Contains("DONE", StringComparison.Ordinal))
                throw new InvalidOperationException($"PDF compression failed: {stderr.Trim()}");
        }

        // Finds the bundled portable Python + script. Returns (null, null, null) if not present.
        private static (string? python, string? script, string workDir) LocateHelper()
        {
            string baseDir = AppContext.BaseDirectory;
            string helperRoot = Path.Combine(baseDir, HelperFolder);
            string bundledPython = Path.Combine(helperRoot, "python", PythonExe);
            string bundledScript = Path.Combine(helperRoot, ScriptName);

            if (File.Exists(bundledPython) && File.Exists(bundledScript))
                return (bundledPython, bundledScript, helperRoot);

            // Dev fallback: a system python on PATH with the script next to the source tree.
            string scriptInSource = Path.Combine(baseDir, HelperFolder, ScriptName);
            if (File.Exists(scriptInSource))
            {
                string? sysPython = TryFindSystemPython();
                if (sysPython != null)
                    return (sysPython, scriptInSource, Path.GetDirectoryName(scriptInSource)!);
            }
            return (null, null, baseDir);
        }

        private static string? TryFindSystemPython()
        {
            // Prefer a venv embedded next to the project, then python on PATH.
            string venv = Path.Combine(AppContext.BaseDirectory, HelperFolder, ".venv", "Scripts", PythonExe);
            if (File.Exists(venv)) return venv;
            foreach (var name in new[] { "python.exe", "python" })
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(name, "--version")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                    if (p != null && p.WaitForExit(3000) && p.ExitCode == 0) return name;
                }
                catch { /* not on PATH */ }
            }
            return null;
        }

        private static string? ParseError(string stdout)
        {
            foreach (var line in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim("\r".ToCharArray());
                if (t.StartsWith("ERROR\t", StringComparison.Ordinal))
                    return t["ERROR\t".Length..];
            }
            return null;
        }
    }
}
