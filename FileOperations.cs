using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using StealthPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace StealthPDF
{
    public partial class MainWindow
    {
        // True while an open is finishing on a background thread (encryption strip / repair). The
        // synchronous open callers check this so they don't treat the not-yet-loaded _doc as a failure;
        // the background path finalizes the tab itself via FinalizeAsyncOpen.
        private bool _asyncOpenPending;

        private void OpenFile(string path)
        {
            // Record real user files in the recent list (skips blank/new docs, which don't open a path).
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) App.AddRecentFile(path);

            // Files on UNC / network shares - notably the WSL \\wsl$ 9P filesystem - can hand
            // back partial reads, making the PDF parser see a truncated file ("Unexpected EOF").
            // Copy such files to a local temp via File.ReadAllBytes (which reads to EOF) and open
            // from there. `path` stays the user's real path for display and Save.
            string srcPath = path;
            if (IsNetworkPath(path))
            {
                try
                {
                    var localCopy = App.MakeTempFile("netopen");
                    File.WriteAllBytes(localCopy, File.ReadAllBytes(path));
                    srcPath = localCopy;
                }
                catch { srcPath = path; }
            }

            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                // PdfSharp cannot save modified encrypted PDFs - it copies unmodified encrypted
                // stream bytes verbatim but fails when it has to re-serialize a dirty object.
                // Strip encryption silently at open time via Import so all edits work correctly.
                if (PdfFileHasEncryption(srcPath))
                {
                    // PdfSharp can read encrypted PDFs but cannot re-save them once modified, so the
                    // encryption is stripped (PDFium, lossless; Import fallback). That strip is CPU-heavy,
                    // so it runs off-thread behind the busy overlay instead of freezing the window. The
                    // background path finalizes the tab itself, so the flag tells the synchronous caller
                    // not to treat the not-yet-set _doc as a failed open.
                    _asyncOpenPending = true;
                    StripEncryptionAndOpen(srcPath, path, busyMessage: "Opening protected PDF...");
                    return;
                }
                _currentFile = srcPath;
                FinishOpenFile(path, srcPath);
            }
            catch (Exception ex) when (IsOwnerPasswordException(ex))
            {
                // PDF has owner/permissions restrictions but no open password -
                // open read-only so the user can still view and print it.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnly"), System.IO.Path.GetFileName(path), _doc.PageCount));
                }
                catch (Exception ex2)
                {
                    StealthDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (IsPasswordException(ex))
            {
                string? pw = PromptForPassword(path);
                if (pw is null) return;
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, pw, PdfDocumentOpenMode.Modify);
                    // Save a decrypted temp copy so Docnet can render without needing the password
                    var tempDec = App.MakeTempFile("dec");
                    _doc.Save(tempDec);
                    _doc.Close();
                    _doc = PdfReader.Open(tempDec, PdfDocumentOpenMode.Modify);
                    _currentFile = tempDec;
                    FinishOpenFile(path, tempDec);
                }
                catch (Exception ex2)
                {
                    StealthDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (IsXRefException(ex))
            {
                // Some PDFs have malformed or non-standard XRef tables that PdfSharp can't
                // open in Modify mode. Fall back to ReadOnly; if that also fails, offer repair.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnlyXRef"), System.IO.Path.GetFileName(path), _doc.PageCount));
                    StealthDialog.Show(this,
                        $"\"{System.IO.Path.GetFileName(path)}\" has a non-standard structure and was opened read-only.\n\nEditing, saving, and some other features may not work correctly.",
                        "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                    // ReadOnly also failed - offer to repair.
                    var result = StealthDialog.Show(this,
                        $"This PDF has a damaged structure and couldn't be opened.\n\nWould you like StealthPDF to attempt a repair? A repaired copy will be created - the original file will not be changed.\n\nNote: repaired files may be missing bookmarks, forms, and other interactive features.",
                        "StealthPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                        TryRepairAndOpen(srcPath);
                }
            }
            catch (Exception ex) when (IsEofParseException(ex))
            {
                // PdfSharpCore rejects some structurally-valid PDFs with "Unexpected EOF" even though
                // PDFium (and every common viewer) reads them fine. Re-save losslessly through PDFium on
                // a background thread (so the window doesn't freeze). The recovered copy is content-
                // equivalent, so it opens clean without nagging to save (markDirty: false).
                _asyncOpenPending = true;
                StripEncryptionAndOpen(srcPath, path, markDirty: false);
            }
            catch (Exception)
            {
                // Any other open failure (truncated file, malformed objects, an out-of-range parse, etc.):
                // we can't classify the damage, but the PDFium-based repair often recovers it anyway, so
                // offer the repair rather than just failing outright.
                var result = StealthDialog.Show(this,
                    "This PDF couldn't be opened - its structure may be damaged.\n\nWould you like StealthPDF to attempt a repair? A repaired copy will be created - the original file will not be changed.\n\nNote: repaired files may be missing bookmarks, forms, and other interactive features.",
                    "StealthPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                    TryRepairAndOpen(srcPath);   // sets _asyncOpenPending and finalizes the tab itself
            }
        }

        // PdfSharpCore throws on some structurally-valid PDFs that PDFium opens fine - most
        // often "Unexpected EOF" from SharpZipLib's Flate inflater while reading a FlateDecode
        // cross-reference stream (multi-revision PDFs with incremental updates / dangling xref
        // entries that tolerant parsers ignore). Match by message AND exception type across the
        // whole inner-exception chain so a wrapped SharpZipBaseException is still recovered.
        private static bool IsEofParseException(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                string msg  = e.Message ?? string.Empty;
                string type = e.GetType().FullName ?? string.Empty;
                if (msg.IndexOf("EOF", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("end of file", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("Inflater", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("FlateDecode", StringComparison.OrdinalIgnoreCase) >= 0
                    || type.IndexOf("SharpZip", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsXRefException(Exception ex) =>
            ex.Message.IndexOf("XRef", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("cross-reference", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Invalid PDF file", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("startxref", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Unexpected token", StringComparison.OrdinalIgnoreCase) >= 0;

        // True for UNC paths (\\server\share, \\wsl$\..., \\wsl.localhost\...) and mapped
        // network drives. Such files are copied locally before opening to avoid 9P short reads.
        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try
            {
                var root = System.IO.Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && root!.Length >= 2 && root[1] == ':')
                    return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { }
            return false;
        }

        private static bool IsOwnerPasswordException(Exception ex) =>
            ex.Message.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;

        private void FinishOpenFile(string displayPath, string workingPath)
        {
            _currentFile = workingPath;
            _originalFile = displayPath;
            FileNameLabel.Text = System.IO.Path.GetFileName(displayPath);
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formFontSizes.Clear();
            _formRadioValues.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            _gridScrollToPage = -1;
            MarkDirty(false);
            // Restore this file's last fit/zoom/view/page if we've seen it before; otherwise open at the
            // per-view-mode default. Set the fields first, then let BootstrapDocumentView apply them.
            if (TryGetDocState(displayPath, out var sfit, out var szoom, out var sview, out var spage))
            {
                _viewMode  = sview;
                _fitMode   = sfit;
                _zoomLevel = szoom;
                int pg = Math.Max(0, Math.Min(spage, _doc!.PageCount - 1));
                BootstrapDocumentView(pg, autoFit: false, restoreFitMode: true);
            }
            else
            {
                BootstrapDocumentView(0, autoFit: true);
            }
            SetStatus(string.Format(Loc("Str_Opened"), System.IO.Path.GetFileName(displayPath), _doc!.PageCount));
        }

        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        private string? PromptForPassword(string filename)
        {
            string? result = null;
            var win = new Window
            {
                Title = "Password Required",
                Width = 360,
                Height = 165,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
            };
            var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            sp.Children.Add(new TextBlock
            {
                Text = $"\"{System.IO.Path.GetFileName(filename)}\" is password protected.",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            var pwBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(pwBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "Open", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "Cancel", Width = 76 };
            okBtn.Click += (s, ev) => { result = pwBox.Password; win.DialogResult = true; };
            cancelBtn.Click += (s, ev) => { win.DialogResult = false; };
            pwBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { result = pwBox.Password; win.DialogResult = true; } };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            sp.Children.Add(btnRow);
            win.Content = sp;
            return win.ShowDialog() == true ? result : null;
        }

        // PDFium P/Invoke
        // PDFium (pdfium.dll) is already shipped with Docnet. We use it here to strip
        // encryption from PDFs that PdfSharpCore can read but cannot re-save when modified.

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocument(
            [MarshalAs(UnmanagedType.LPStr)] string filePath,
            [MarshalAs(UnmanagedType.LPStr)] string? password);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDF_SaveWithVersion(
            IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion);

        [StructLayout(LayoutKind.Sequential)]
        private struct FPDF_FILEWRITE
        {
            public int version;          // must be 1
            public IntPtr WriteBlock;    // cdecl: int WriteBlock(FPDF_FILEWRITE*, const void*, unsigned long)
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PdfWriteBlockDelegate(IntPtr pThis, IntPtr pData, uint size);

        private const uint FPDF_REMOVE_SECURITY = 3;

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPage(IntPtr document, int page_index);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(IntPtr page);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_SetRotation(IntPtr page, int rotation);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPage_GenerateContent(IntPtr page);

        /// <summary>
        /// Returns true if the PDF file has an /Encrypt entry in its trailer.
        /// Scans the last 2 KB so it's fast; works regardless of how PdfSharp
        /// reports security state after authenticating with an empty password.
        /// </summary>
        private static bool PdfFileHasEncryption(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                long scan = Math.Min(2048, fs.Length);
                fs.Seek(-scan, SeekOrigin.End);
                var buf = new byte[scan];
                _ = fs.Read(buf, 0, buf.Length);
                // Look for /Encrypt in the raw bytes (Latin-1 safe)
                var text = System.Text.Encoding.GetEncoding(1252).GetString(buf);
                return text.Contains("/Encrypt");
            }
            catch { return false; }
        }

        /// <summary>
        /// Uses PDFium to save a copy of <paramref name="sourcePath"/> with all security/encryption
        /// removed. Returns true on success. Falls back gracefully if PDFium is unavailable.
        /// PDFium is already initialised by Docnet; no separate init call is needed.
        /// </summary>
        private static bool TryPdfiumStripEncryption(string sourcePath, string destPath)
        {
            try
            {
                // Ensure PDFium is initialised - Docnet does this lazily on first use,
                // so force it now before we call PDFium P/Invoke directly.
                try { _ = DocLib.Instance; } catch { }

                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero) return false;
                try
                {
                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }
                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch { return false; }
        }

        /// <summary>
        /// Uses PDFium to load <paramref name="sourcePath"/>, zero-out all page /Rotate values,
        /// strip encryption, and save to <paramref name="destPath"/>. Returns true on success.
        /// Called from SaveTempAndReload's xref-error fallback - PDFium is guaranteed to be
        /// initialised by then because the page preview has already rendered via Docnet.
        /// </summary>
        private static bool TryPdfiumSaveWithZeroRotations(string sourcePath, string destPath)
        {
            try
            {
                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero)
                {
                    try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StealthPDF_pdfium_debug.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FPDF_LoadDocument returned null for: {sourcePath}\n\n"); } catch { }
                    return false;
                }
                try
                {
                    int pageCount = FPDF_GetPageCount(doc);
                    for (int i = 0; i < pageCount; i++)
                    {
                        var page = FPDF_LoadPage(doc, i);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            FPDFPage_SetRotation(page, 0);   // strip /Rotate so Docnet renders cleanly
                            FPDFPage_GenerateContent(page);
                        }
                        finally { FPDF_ClosePage(page); }
                    }

                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }

                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StealthPDF_pdfium_debug.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TryPdfiumSaveWithZeroRotations failed\n" +
                        $"  source: {sourcePath}\n" +
                        $"  type:   {ex.GetType().FullName}\n" +
                        $"  msg:    {ex.Message}\n" +
                        $"  stack:  {ex.StackTrace}\n\n");
                }
                catch { /* log failure is non-fatal */ }
                return false;
            }
        }

        /// <param name="stripRotations">
        /// Pass true when called from SaveTempAndReload (rotations already stripped in source).
        /// Pass false for open-time repair so original page rotations are preserved.
        /// </param>
        private static bool TryImportRepairToPath(string sourcePath, string destPath, bool stripRotations = false)
        {
            try
            {
                using var importDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                var cleanDoc = new PdfDocument();
                for (int i = 0; i < importDoc.PageCount; i++)
                    cleanDoc.Pages.Add(importDoc.Pages[i]);
                if (stripRotations)
                    for (int i = 0; i < cleanDoc.PageCount; i++)
                        cleanDoc.Pages[i].Rotate = 0;
                cleanDoc.Save(destPath);
                cleanDoc.Close();
                return true;
            }
            catch { return false; }
        }

        private async void TryRepairAndOpen(string path)
        {
            // Repair is CPU/IO heavy, so it runs on a background thread behind a spinner overlay -
            // otherwise the window froze (hourglass, no feedback) for the whole repair. Only the
            // file production runs off-thread; opening/rendering the result stays on the UI thread.
            _asyncOpenPending = true;   // the synchronous open caller defers tab finalization to here
            var ct = BeginCancellableOp("repair");
            var busy = ShowBusyOverlay("Repairing PDF...");
            try
            {
                // Release any open document before the worker reads the source file.
                if (_doc is not null) { _doc.Close(); _doc = null; }

                string? repairedPath = null;
                bool raster = false;

                // Strategy 1: PdfSharpCore Import mode - page-copy, more lenient than Modify/ReadOnly.
                // Works when the XRef is partially corrupt but the object data is intact. (Returns
                // null on failure rather than throwing.)
                repairedPath = await System.Threading.Tasks.Task.Run(() => RepairViaImportToFile(path));
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus("Repair cancelled"); return; }   // cancelled during strategy 1

                // Strategy 2: PDFium rasterize. PDFium's internal XRef recovery handles damage
                // PdfSharpCore cannot; each page is rendered to a bitmap and rebuilt into a clean PDF.
                // Text won't be selectable in the result, but the file will open and print.
                if (repairedPath is null)
                {
                    repairedPath = await System.Threading.Tasks.Task.Run(() => RepairViaDocnetRasterizeToFile(path));
                    raster = repairedPath is not null;
                }
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus("Repair cancelled"); return; }   // cancelled during strategy 2

                if (repairedPath is null)
                {
                    HideBusyOverlay(busy);
                    _asyncOpenPending = false;
                    StealthDialog.Show(this,
                        "Repair failed - the file is too severely damaged to recover.\n\nTry opening the original in a different application (Adobe Acrobat, browsers) which may have additional recovery options.",
                        "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Open and render the repaired copy on the UI thread.
                _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                _currentFile = repairedPath;
                FinishOpenFile(path, repairedPath);
                MarkDirty(true); // repaired copy lives in temp - user must Save As
                SetStatus(string.Format(Loc(raster ? "Str_OpenedRasterRepair" : "Str_OpenedRepaired"),
                                        System.IO.Path.GetFileName(path), _doc.PageCount));
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
                StealthDialog.Show(this,
                    raster
                        ? $"\"{System.IO.Path.GetFileName(path)}\" was repaired by rasterizing through PDFium.\n\nText is not selectable in the repaired copy. Use Save As to write it to a new location."
                        : $"\"{System.IO.Path.GetFileName(path)}\" was repaired successfully.\n\nBookmarks, forms, and other interactive features may have been lost. Use Save As to write the repaired file to a new location.",
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                _asyncOpenPending = false;
                StealthDialog.Show(this, $"Repair failed:\n{ex.Message}", "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Strips a PDF's encryption on a background thread (so the window doesn't freeze), then opens the
        // clean copy. Mirrors TryRepairAndOpen; finalizes the tab via FinalizeAsyncOpen.
        private async void StripEncryptionAndOpen(string srcPath, string displayPath, bool markDirty = true, string busyMessage = "Opening PDF...")
        {
            _asyncOpenPending = true;
            var ct = BeginCancellableOp("operation");
            var busy = ShowBusyOverlay(busyMessage);
            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                var repairedPath = App.MakeTempFile("repaired");
                bool ok = await System.Threading.Tasks.Task.Run(() =>
                    TryPdfiumStripEncryption(srcPath, repairedPath) || TryImportRepairToPath(srcPath, repairedPath));
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus("Cancelled"); EndCancellableOp(); return; }
                if (!ok)
                {
                    HideBusyOverlay(busy);
                    TryRepairAndOpen(srcPath);   // re-registers the cancellable op; repair finalizes the tab
                    return;
                }
                _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                _currentFile = repairedPath;
                FinishOpenFile(displayPath, repairedPath);
                if (markDirty) MarkDirty(true);   // stripped copy lives in temp - user must Save As to keep it
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
                EndCancellableOp();
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                _asyncOpenPending = false;
                StealthDialog.Show(this, $"Could not open the protected PDF:\n{ex.Message}",
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                EndCancellableOp();
            }
        }

        // Finalizes a background open (encryption strip / repair) once the document is loaded on the UI
        // thread: stores it into the active tab session and refreshes the tool + tab strip. Mirrors the
        // tail of OpenInNewTab, which is skipped while _asyncOpenPending is set.
        private void FinalizeAsyncOpen()
        {
            _asyncOpenPending = false;
            if (_active != null) CaptureSessionState(_active);
            SetTool(_currentTool);
            RebuildTabStrip();
        }

        /// <summary>
        /// Strategy 1 worker (background-safe, no UI/_doc access): page-copies the source through
        /// PdfSharpCore Import mode into a clean temp PDF and returns its path.
        /// </summary>
        private static string? RepairViaImportToFile(string path)
        {
            // Returns null (never throws) so a failed strategy falls through cleanly to the next one
            // and doesn't surface as a debugger "user-unhandled" break during the awaited Task.
            try
            {
                PdfDocument repairedDoc;
                using (var importDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                {
                    repairedDoc = new PdfDocument();
                    for (int i = 0; i < importDoc.PageCount; i++)
                        repairedDoc.Pages.Add(importDoc.Pages[i]);
                }
                var repairedPath = App.MakeTempFile("repaired");
                repairedDoc.Save(repairedPath);
                repairedDoc.Close();
                return repairedPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// Strategy 2 worker (background-safe, no UI/_doc access): uses PDFium (Docnet) to render
        /// each page to a bitmap, rebuilds a clean PdfSharpCore document from those bitmaps, and
        /// returns its temp path. Mirrors the flatten path, which also encodes off the UI thread.
        /// </summary>
        private static string? RepairViaDocnetRasterizeToFile(string path)
        {
            // Returns null (never throws) so the caller can show a clean "repair failed" message
            // without a debugger break on the awaited Task.
            try
            {
                const int RenderPx = 2048;

                using var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(RenderPx, RenderPx));
                int pageCount = docReader.GetPageCount();
                if (pageCount <= 0) return null;

                var newDoc = new PdfDocument();

                for (int i = 0; i < pageCount; i++)
                {
                    using var pr = docReader.GetPageReader(i);
                    int bw = pr.GetPageWidth();
                    int bh = pr.GetPageHeight();
                    if (bw <= 0 || bh <= 0) continue;

                    var raw = pr.GetImage();
                    if (raw is null || raw.Length == 0) continue;

                    var wb = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                    wb.WritePixels(new Int32Rect(0, 0, bw, bh), raw, bw * 4, 0);
                    wb.Freeze();

                    byte[] pngBytes;
                    using (var ms = new System.IO.MemoryStream())
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(wb));
                        enc.Save(ms);
                        pngBytes = ms.ToArray();
                    }

                    // Build the page at correct aspect ratio scaled to A4-ish width.
                    double pageW = 595.28;
                    double pageH = pageW * bh / bw;

                    var page = newDoc.AddPage();
                    page.Width  = XUnit.FromPoint(pageW);
                    page.Height = XUnit.FromPoint(pageH);

                    using var gfx = XGraphics.FromPdfPage(page);
                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(pngBytes));
                    gfx.DrawImage(xImg, 0, 0, pageW, pageH);
                }

                if (newDoc.PageCount == 0) return null;

                var repairedPath = App.MakeTempFile("repaired");
                newDoc.Save(repairedPath);
                newDoc.Close();
                return repairedPath;
            }
            catch { return null; }
        }

        // ============================================================
        // Close file (Ctrl+W) - returns to drop-zone state
        // ============================================================

        private void CloseFile()
        {
            if (_doc is null) return;
            if (_isDirty)
            {
                var res = StealthDialog.Show(this,
                    Loc("Str_Dlg_UnsavedClose"),
                    "StealthPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            _doc.Close();
            _doc = null;
            _currentFile = null;
            App.RemoveSetting("LastFile");   // don't reopen a manually-closed file on next launch (Issue #75)
            _activeTextBox = null;   // cancel any in-progress typewriter edit before canvas clear
            RemoveTextEditHandles();
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formFontSizes.Clear();
            _formRadioValues.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            PageImage.Source = null;
            _annotationCanvas.Children.Clear();
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PopulateRecentFilesList();   // refresh the empty-state recent list
            PagePreviewPanel.Visibility = Visibility.Collapsed;
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            SetTool(EditTool.Select);
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
            _pageJumpBox.IsEnabled = false;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _pageJumpBox.Text = "";
            _pageTotalLabel.Text = "/ -";
            OutlineTree.Items.Clear();
            SidebarOutlinesTab.IsEnabled = false;
            if (_sidebarShowingOutlines) SwitchSidebarToPagesTab();
            MarkDirty(false);
            SetStatus("Ready");
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e) => CloseTab(_active);

        // ============================================================
        // File toolbar handlers
        // ============================================================

        private void New_Click(object sender, RoutedEventArgs e) => NewDocument();

        private void NewDocument()
        {
            // A new blank document opens in its own tab; other open tabs keep their state, so
            // there's no need to prompt about unsaved changes here.
            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                var newDoc = new PdfDocument();
                newDoc.AddPage(); // one blank A4 page

                var tempPath = App.MakeTempFile("new");
                newDoc.Save(tempPath);
                newDoc.Close();

                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                FinishOpenFile("Untitled.pdf", tempPath);
                SetStatus("New blank document");
                CaptureSessionState(_active!);
                SetTool(_currentTool);   // sync the tool UI to this (new) tab's tool
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                StealthDialog.Show(this, $"Could not create new document:\n{ex.Message}",
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF" };
            if (dlg.ShowDialog(this) == true) OpenInNewTab(dlg.FileName);
        }

        // Dropdown next to the Open button: the recent-files list.
        private void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);

            menu.Items.Add(MakeMenuItem(Loc("Str_Menu_Import") + "...", (s2, e2) => ImportImages_Click(s2, e2)));
            menu.Items.Add(new Separator());

            var recents = App.GetRecentFiles();
            if (recents.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Menu_RecentNone"), IsEnabled = false });
            }
            else
            {
                foreach (var p in recents)
                {
                    string path = p;   // capture
                    var item = MakeMenuItem(System.IO.Path.GetFileName(path), (_, _) =>
                    {
                        if (System.IO.File.Exists(path)) OpenInNewTab(path);
                        else StealthDialog.Show(this, $"File not found:\n{path}", "StealthPDF",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    item.ToolTip = path;

                    // Header = filename then a small X right after it (kept tight - no right whitespace).
                    var rmBtn = new Button
                    {
                        Content = "",
                        FontFamily = UiKit.IconFont,
                        FontSize = 11,
                        Width = 18, Height = 18,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0),
                        // No local Foreground - it would override the DangerCloseButton hover trigger.
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("DangerCloseButton"),
                        ToolTip = "Remove from list"
                    };
                    rmBtn.Click += (_, ev) =>
                    {
                        ev.Handled = true;
                        App.RemoveRecentFile(path);
                        menu.Items.Remove(item);   // drop just this row in place - no rebuild, no blink
                        if (!menu.Items.OfType<MenuItem>().Any(mi => mi.Header is Grid))
                            menu.IsOpen = false;   // nothing left to show
                    };
                    // Filename (fills) + X right-aligned. Trim the MenuItem's default 40px right padding
                    // so the X sits near the edge instead of floating in whitespace.
                    // Negative right margin overlaps the template's empty InputGestureText column
                    // (it reserves ~24px), so the X lands near the real right edge instead of floating.
                    // Real file-type icon (left), filename (fills), X (right).
                    var fileIcon = new Image
                    {
                        Source              = GetShellIcon(path),
                        Width               = 18,
                        Height              = 18,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Margin              = new Thickness(0, 0, 8, 0),
                        Stretch             = Stretch.Uniform,
                        SnapsToDevicePixels = true
                    };
                    RenderOptions.SetBitmapScalingMode(fileIcon, BitmapScalingMode.HighQuality);
                    fileIcon.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 2, Direction = 270, Opacity = TryFindResource("IconShadowOpacity") is double so2 ? so2 : 0.5 };
                    var hdr = new Grid { Width = 348, Margin = new Thickness(0, 0, 0, 0) };   // no negative right margin - it pushed the remove X past the menu's right edge, clipping it out of frame
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // icon
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // remove X
                    var nameText = new TextBlock { Text = System.IO.Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    Grid.SetColumn(fileIcon, 0);
                    Grid.SetColumn(nameText, 1);
                    Grid.SetColumn(rmBtn, 2);
                    hdr.Children.Add(fileIcon);
                    hdr.Children.Add(nameText);
                    hdr.Children.Add(rmBtn);
                    item.Header = hdr;
                    item.Padding = new Thickness(20, 6, 8, 6);

                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ClearList"), (_, _) => App.ClearRecentFiles()));
            }

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // ---- File-type (shell) icons for the Recent list ----
        // Cached per extension. Uses SHGFI_USEFILEATTRIBUTES so the icon resolves from the extension alone -
        // works even when the file is missing, and never touches the file on disk.
        private static readonly Dictionary<string, ImageSource?> _shellIconCache = new(System.StringComparer.OrdinalIgnoreCase);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static ImageSource? GetShellIcon(string path)
        {
            string ext = System.IO.Path.GetExtension(path) ?? "";
            if (_shellIconCache.TryGetValue(ext, out var hit)) return hit;

            const uint SHGFI_ICON = 0x000000100, SHGFI_LARGEICON = 0x000000000, SHGFI_USEFILEATTRIBUTES = 0x000000010;
            const uint FILE_ATTRIBUTE_NORMAL = 0x80;
            ImageSource? src = null;
            try
            {
                var info = new SHFILEINFO();
                IntPtr res = SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                if (res != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    DestroyIcon(info.hIcon);
                }
            }
            catch { /* no icon available - the row simply shows none */ }
            _shellIconCache[ext] = src;
            return src;
        }

        // Fills the empty-state "Recent" list with clickable filenames (hidden when there are none).
        private void PopulateRecentFilesList()
        {
            if (RecentFilesList is null || RecentFilesBox is null) return;
            RecentFilesList.Items.Clear();
            var recents = App.GetRecentFiles();
            if (recents.Count == 0) { RecentFilesBox.Visibility = Visibility.Collapsed; return; }
            RecentFilesBox.Visibility = Visibility.Visible;
            var fam = UiKit.UiFont;
            foreach (var p in recents)
            {
                string path = p;   // capture
                bool exists = System.IO.File.Exists(path);
                string dir = System.IO.Path.GetDirectoryName(path) ?? "";
                string dateStr = exists
                    ? $"{System.IO.File.GetLastWriteTime(path):MMM d, yyyy}"
                    : "missing";

                var name = new TextBlock
                {
                    Text         = System.IO.Path.GetFileName(path),
                    FontFamily   = fam,
                    FontSize     = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                // DynamicResource so the color tracks theme switches (FindResource would freeze
                // whatever theme was active when the list was built).
                name.SetResourceReference(TextBlock.ForegroundProperty, exists ? "TextPrimary" : "TextDim");

                // File path line (slightly brighter) sits above the date line (slightly dimmer).
                var pathTb = new TextBlock
                {
                    Text         = dir,
                    FontFamily   = fam,
                    FontSize     = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin       = new Thickness(0, 2, 0, 0)
                };
                pathTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                var dateTb = new TextBlock
                {
                    Text         = dateStr,
                    FontFamily   = fam,
                    FontSize     = 11,
                    Opacity      = 0.6,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin       = new Thickness(0, 1, 0, 0)
                };
                dateTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                var stack = new StackPanel();
                stack.Children.Add(name);
                stack.Children.Add(pathTb);
                stack.Children.Add(dateTb);

                // Per-row remove button: a small X that fades in on hover and drops just this
                // entry from the recents list (it does not touch the file on disk).
                var delIcon = new TextBlock
                {
                    Text              = "",   // close (X) glyph below set via code
                    FontFamily        = UiKit.IconFont,
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                delIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                delIcon.Text = "";   // Segoe MDL2 ChromeClose (X)
                var del = new Border
                {
                    Width             = 22,
                    Height            = 22,
                    Background        = System.Windows.Media.Brushes.Transparent,
                    CornerRadius      = new CornerRadius(4),
                    Cursor            = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity           = 0,   // hidden until the row is hovered
                    Child             = delIcon,
                    ToolTip           = Loc("Str_Menu_RemoveFromRecents")
                };
                del.MouseEnter += (_, _) => { delIcon.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed"); delIcon.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.5 }; };
                del.MouseLeave += (_, _) => { delIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); delIcon.Effect = null; };
                del.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;   // don't open the file
                    App.RemoveRecentFile(path);
                    PopulateRecentFilesList();
                };

                // Real Windows file-type icon for this extension (left of the text).
                var icon = new Image
                {
                    Source              = GetShellIcon(path),
                    Width               = 32,
                    Height              = 32,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Margin              = new Thickness(0, 0, 10, 0),
                    Stretch             = Stretch.Uniform,
                    Opacity             = exists ? 1.0 : 0.45,   // dim missing files' icons, matching the text
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                icon.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 2, Direction = 270, Opacity = TryFindResource("IconShadowOpacity") is double so ? so : 0.5 };
                stack.VerticalAlignment = VerticalAlignment.Center;

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // icon
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // text
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // remove X
                Grid.SetColumn(icon, 0);
                Grid.SetColumn(stack, 1);
                Grid.SetColumn(del, 2);
                rowGrid.Children.Add(icon);
                rowGrid.Children.Add(stack);
                rowGrid.Children.Add(del);

                var row = new Border
                {
                    Background    = System.Windows.Media.Brushes.Transparent,
                    CornerRadius  = new CornerRadius(4),
                    Padding       = new Thickness(8, 6, 8, 6),
                    Margin        = new Thickness(0, 1, 0, 1),
                    Cursor        = Cursors.Hand,
                    Child         = rowGrid,
                    ToolTip       = path
                };
                row.MouseEnter += (_, _) => { row.Background = (SolidColorBrush)FindResource("BgHover"); del.Opacity = 1; };
                row.MouseLeave += (_, _) => { row.Background = System.Windows.Media.Brushes.Transparent; del.Opacity = 0; };
                row.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;   // don't bubble to the DropZone "click to browse" handler
                    if (System.IO.File.Exists(path)) OpenInNewTab(path);
                    else StealthDialog.Show(this, $"File not found:\n{path}", "StealthPDF",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                RecentFilesList.Items.Add(row);
            }
        }

        // Dropdown next to the Save button: explicit Save / Save As.
        private void SaveMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);

            if (_doc is null)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Menu_SaveNothing"), IsEnabled = false });
            }
            else
            {
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_Save"), (_, _) => SaveInPlace(), "Ctrl+S"));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_SaveAs"), (s2, e2) => SaveAs_Click(s2, e2), "Ctrl+Shift+S"));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_CompressZip"), (s2, e2) => CompressToZip_Click(s2, e2)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_DigitalSig"), (_, _) => OpenSignDialog()));
            }

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void DocInfo_Click(object sender, RoutedEventArgs e) => OpenDocumentInfo();

        // Opens the Document Info dialog; edits are applied to the live doc and persist on the next save.
        private void OpenDocumentInfo()
        {
            if (_doc is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new DocumentInfoDialog(this, _doc, _originalFile ?? _currentFile);
            dlg.ShowDialog();   // fade-close dialogs don't reliably return true; rely on the Saved flag
            if (dlg.Saved)
            {
                MarkDirty();
                SetStatus("Document info updated - save the file to keep the changes");
            }
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    int pageOffset = doc.PageCount;

                    // Open twice: Import mode for AddPage, ReadOnly for catalog access.
                    using var srcRead = PdfReader.Open(file, PdfDocumentOpenMode.ReadOnly);
                    var namedDestMap = BuildNamedDestMap(srcRead);

                    using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        doc.AddPage(src.Pages[i]);

                    // Rewrite named-destination links in the newly added pages so they
                    // resolve correctly after the catalog is not imported.
                    if (namedDestMap.Count > 0)
                        RewriteNamedDestLinks(doc, pageOffset, namedDestMap);
                }
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                StealthDialog.Show(this, $"Merge failed:\n{ex.Message}", "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Builds a map of named destination string -> 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        private Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var catalog = src.Internals.Catalog;

                // Legacy flat /Dests dictionary
                var destsDict = catalog.Elements.GetDictionary("/Dests");
                if (destsDict != null)
                {
                    foreach (var key in destsDict.Elements.Keys)
                    {
                        PdfItem? val = DerefItem(destsDict.Elements[key] ?? new PdfInteger(-1));
                        int? idx = ResolveDestPageIndexInDoc(src, val);
                        if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                    }
                }

                // Modern /Names /Dests name tree
                var namesDict = catalog.Elements.GetDictionary("/Names");
                var destTree  = namesDict?.Elements.GetDictionary("/Dests");
                if (destTree != null)
                    WalkNameTree(src, destTree, map);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
            return map;
        }

        private void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
        {
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var keyItem = namesArr.Elements[i];
                    string key  = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                    int? idx = ResolveDestPageIndexInDoc(src, val);
                    if (idx.HasValue) map[key] = idx.Value;
                }
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    if (DerefItem(kids.Elements[i]) is PdfDictionary kid)
                        WalkNameTree(src, kid, map);
                }
            }
        }

        /// <summary>
        /// Resolves a destination value (PdfArray or PdfDictionary with /D) to a page index
        /// within the given source document by matching the page object number.
        /// </summary>
        private static int? ResolveDestPageIndexInDoc(PdfDocument src, PdfItem? val)
        {
            PdfArray? arr = val as PdfArray;
            if (arr is null && val is PdfDictionary vd)
                arr = vd.Elements.GetArray("/D");
            if (arr is null || arr.Elements.Count == 0) return null;

            var first = arr.Elements[0];
            int objNum = GetObjectNumber(first);
            if (objNum > 0)
            {
                for (int i = 0; i < src.PageCount; i++)
                {
                    var pgRef = src.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
                }
            }
            else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
            {
                return pi.Value;
            }
            return null;
        }

        /// <summary>
        /// Walks all link annotations in pages [pageOffset, doc.PageCount) and rewrites any
        /// named-destination /D values to explicit [pageRef /Fit] arrays using the merged
        /// document's page references. This is needed because PdfSharpCore's import does not
        /// copy the source document's /Names /Dests catalog entries.
        /// </summary>
        private static void RewriteNamedDestLinks(PdfDocument doc, int pageOffset,
            Dictionary<string, int> namedDestMap)
        {
            for (int pi = pageOffset; pi < doc.PageCount; pi++)
            {
                try
                {
                    var page    = doc.Pages[pi];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                    {
                        PdfItem? elem = annotsArr.Elements[ai];
                        PdfDictionary? ann = elem as PdfDictionary
                            ?? (DerefItemStatic(elem) as PdfDictionary);
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Link")) continue;

                        // Check /A /D (GoTo action)
                        var actionDict = ann.Elements.GetDictionary("/A");
                        if (actionDict != null)
                        {
                            var s = actionDict.Elements["/S"]?.ToString() ?? "";
                            if (s.Contains("GoTo"))
                            {
                                var destItem = actionDict.Elements["/D"];
                                string? name = ExtractDestName(destItem);
                                if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                                {
                                    int targetIdx = pageOffset + srcIdx;
                                    if (targetIdx < doc.PageCount)
                                        actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                                }
                            }
                        }
                        else
                        {
                            // Bare /Dest on annotation
                            var destItem = ann.Elements["/Dest"];
                            string? name = ExtractDestName(destItem);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
            }
        }

        private static string? ExtractDestName(PdfItem? item)
        {
            if (item is null) return null;
            if (item is PdfString ps) return ps.Value;
            if (item is PdfName   pn) return pn.Value.TrimStart('/');
            return null;
        }

        private static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
        {
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[pageIndex].Reference);
            arr.Elements.Add(new PdfName("/Fit"));
            return arr;
        }

        // Static version of DerefItem for use in static helpers.
        private static PdfItem DerefItemStatic(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        private void SaveInPlace()
        {
            if (_doc is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            // Save back to the user's real file. After a page edit (crop/rotate) _currentFile is a
            // temp working copy, so the real path is kept in _originalFile. If there is no real path
            // (e.g. a repaired temp-backed open), fall back to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(this, new RoutedEventArgs()); return; }
            CommitActiveTextBox();
            string saveTarget = _originalFile!;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count
                // so mailto/URI links don't appear as strikethrough lines in other viewers.
                StripLinkAnnotationBorders(_doc);

                if (hasAnnotations)
                {
                    // Save a clean copy of the doc (without burned annotations), burn
                    // annotations into the real file, then restore the in-memory doc
                    // from the clean copy so future saves don't double-burn.
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawStampsOnDocument();
                    DrawAnnotationsOnDocument();
                    _doc.Save(saveTarget);
                    _doc.Close();
                    try
                    {
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    catch (Exception saveOpenEx) when (IsXRefException(saveOpenEx))
                    {
                        var fixedPath = App.MakeTempFile("savefixed");
                        if (!TryImportRepairToPath(tempClean, fixedPath)
                            && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                            throw;
                        tempClean = fixedPath;
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    _currentFile = tempClean;
                }
                else
                {
                    _doc.Save(saveTarget);
                }

                MarkDirty(false);
                SetStatus($"Saved - {System.IO.Path.GetFileName(saveTarget)}");
            }
            catch (Exception ex)
            {
                StealthDialog.Show(this, $"Save failed:\n{ex.Message}", "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            // No real path yet (repaired temp-backed open) -> go straight to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(sender, e); return; }
            var name = System.IO.Path.GetFileName(_originalFile);
            var choice = StealthDialog.Show(this, $"Overwrite {name}?", "Save",
                                           MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)      SaveInPlace();
            else if (choice == MessageBoxResult.No)  SaveAs_Click(sender, e);
            // Cancel or closed: do nothing.
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save PDF as",
                                           CheckFileExists = false, CheckPathExists = true };
            string? seed = _originalFile ?? _currentFile;
            if (!string.IsNullOrEmpty(seed))
            {
                dlg.FileName = System.IO.Path.GetFileName(seed);
                // Only seed the folder from a real saved location. Scanned/imported docs have no
                // original path, and Path.GetDirectoryName("") throws on .NET Framework.
                if (!string.IsNullOrEmpty(_originalFile))
                {
                    var seedDir = System.IO.Path.GetDirectoryName(_originalFile);
                    if (!string.IsNullOrEmpty(seedDir) && System.IO.Directory.Exists(seedDir))
                        dlg.InitialDirectory = seedDir;
                }
            }
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count.
                StripLinkAnnotationBorders(_doc);

                if (hasAnnotations)
                {
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawStampsOnDocument();
                    DrawAnnotationsOnDocument();
                    _doc.Save(dlg.FileName);
                    _doc.Close();
                    try
                    {
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    catch (Exception saveOpenEx) when (IsXRefException(saveOpenEx))
                    {
                        var fixedPath = App.MakeTempFile("savefixed");
                        if (!TryImportRepairToPath(tempClean, fixedPath)
                            && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                            throw;
                        tempClean = fixedPath;
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    _currentFile = tempClean;
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    SetStatus($"Saved with annotations to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
                else
                {
                    _doc.Save(dlg.FileName);
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    SetStatus($"Saved to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
            }
            catch (Exception ex)
            {
                StealthDialog.Show(this, $"Save failed:\n{ex.Message}", "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save Flattened PDF",
                                           CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;

            // Burn any pending annotations into a temp source for rasterization
            // (must happen on UI thread before we go async)
            string sourcePath;
            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            if (hasAnnotations)
            {
                var tempClean  = App.MakeTempFile("clean");
                var tempBurned = App.MakeTempFile("burned");
                _doc.Save(tempClean);
                DrawStampsOnDocument();
                DrawAnnotationsOnDocument();
                _doc.Save(tempBurned);
                _doc.Close();
                try
                {
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                catch (Exception saveOpenEx) when (IsXRefException(saveOpenEx))
                {
                    var fixedPath = App.MakeTempFile("savefixed");
                    if (!TryImportRepairToPath(tempClean, fixedPath)
                        && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                        throw;
                    tempClean = fixedPath;
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                _currentFile = tempClean;
                sourcePath = tempBurned;
            }
            else
            {
                var temp = App.MakeTempFile("src");
                _doc.Save(temp);
                sourcePath = temp;
            }

            int pageCount = _doc.PageCount;

            // Snapshot per-page dimensions (CropBox-aware) before going off-thread
            var pageDims = new (double widthPt, double heightPt)[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                var p = _doc.Pages[i];
                pageDims[i] = (p.Width.Point, p.Height.Point);
            }

            // Show a progress overlay so the user knows we're working
            var overlay = ShowFlattenProgress(pageCount);
            string outputPath = dlg.FileName;

            try
            {
                var ct = BeginCancellableOp("flatten");
                // Rasterize on a background thread - keeps the UI responsive
                await Task.Run(() =>
                {
                    // Rasterize pages across CPU cores. Docnet/PDFium is not thread-safe, so the
                    // pdfium render is serialized behind a lock; the JPEG encode (GDI+) runs in
                    // parallel. Pages are assembled into the PDF afterwards, in order.
                    //
                    // Pages are encoded as JPEG, not PNG: a lossless PNG of every rendered page
                    // accumulates hundreds of MB across a long document (the whole-document set is
                    // held until outDoc.Save() writes them), which exhausted memory and made long
                    // files fail before finishing. JPEG at q82 is ~5-10x smaller with no visible
                    // loss for a 150-DPI flattened page, so long documents now complete.
                    //
                    // The source document is opened ONCE here. The old code re-opened it inside
                    // the per-page loop, re-parsing the whole file on every page (O(pages) full
                    // document parses) - the dominant cost on large files. A single scaling
                    // factor renders each page at its own size at 150 DPI (150/72), so the doc
                    // no longer needs reopening to apply per-page pixel dimensions.
                    var pageImages = new byte[pageCount][];
                    var docGate  = new object();
                    int done     = 0;
                    var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
                    using var flattenReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(150.0 / 72.0));
                    Parallel.For(0, pageCount, po, i =>
                    {
                        if (ct.IsCancellationRequested) return;   // cooperative: skip remaining pages' work
                        byte[] bgra; int rw, rh;
                        lock (docGate)
                        {
                            using var pr = flattenReader.GetPageReader(i);
                            bgra = pr.GetImage();
                            rw   = pr.GetPageWidth();
                            rh   = pr.GetPageHeight();
                        }
                        // Encode BGRA to JPEG (GDI+) outside the lock so it parallelizes.
                        pageImages[i] = RenderToJpeg(bgra, rw, rh);

                        int n = System.Threading.Interlocked.Increment(ref done);
                        Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(overlay, n, pageCount)));
                    });

                    if (ct.IsCancellationRequested) return;   // cancelled during render: assemble/save nothing

                    // Assemble the output PDF in page order (PdfSharp is single-threaded).
                    var outDoc = new PdfDocument();
                    try
                    {
                        for (int i = 0; i < pageCount; i++)
                        {
                            var newPage = outDoc.AddPage();
                            newPage.Width  = XUnit.FromPoint(pageDims[i].widthPt);
                            newPage.Height = XUnit.FromPoint(pageDims[i].heightPt);
                            using var xi  = XImage.FromStream(() => new MemoryStream(pageImages[i]));
                            using var gfx = XGraphics.FromPdfPage(newPage);
                            gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
                        }
                        outDoc.Save(outputPath);
                    }
                    finally
                    {
                        outDoc.Dispose();
                    }
                });

                if (ct.IsCancellationRequested) { SetStatus("Flatten cancelled (no file written)"); return; }
                MarkDirty(false);
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                try { StealthDialog.Show(this, $"Flatten failed:\n{ex.GetType().Name}: {ex.Message}", "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { /* dialog failed; overlay still removed in finally */ }
            }
            finally
            {
                try { HideFlattenProgress(overlay); } catch { /* ensure overlay never leaks */ }
                EndCancellableOp();
            }
        }

        // ---- flatten progress overlay helpers ----

        private Border ShowFlattenProgress(int pageCount, string verb = "Flattening")
        {
            var progressText = new TextBlock
            {
                Text       = $"{verb} page 0 of {pageCount}...",
                Foreground = Brushes.White,
                FontSize   = 14,
                Tag        = verb   // stored so UpdateFlattenProgress can read it
            };
            var panel = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            panel.Children.Add(progressText);

            var overlay = new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(200, 0x1a, 0x1a, 0x1a)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                Child             = panel,
                Tag               = "FlattenOverlay"
            };
            Panel.SetZIndex(overlay, 999);

            // Attach to the root grid
            if (Content is Grid rootGrid)
                rootGrid.Children.Add(overlay);

            return overlay;
        }

        private static void UpdateFlattenProgress(Border overlay, int current, int total)
        {
            if (overlay.Child is StackPanel panel)
                foreach (var child in panel.Children)
                    if (child is TextBlock tb && tb.Tag is string verb)
                        tb.Text = $"{verb} page {current} of {total}...";
        }

        private void HideFlattenProgress(Border overlay)
        {
            if (Content is Grid rootGrid)
                rootGrid.Children.Remove(overlay);
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { StealthDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();

            // The print prep (annotation burn + doc reopen) runs synchronously on the UI thread and freezes
            // it for a moment. If Settings is open, close it and wait for the slide to finish before that
            // freeze, so the animation stays smooth; otherwise just yield one render cycle to keep the click
            // responsive. (Deeper fix - backgrounding the burn - is tracked separately.)
            if (SettingsOverlay?.Visibility == Visibility.Visible)
            {
                SlideSettingsClosed();
                var settleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
                settleTimer.Tick += (_, _2) => { settleTimer.Stop(); RunPrintFlow(); };
                settleTimer.Start();
            }
            else
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RunPrintFlow));
            }
        }

        private async void RunPrintFlow()
        {
            if (_doc is null || _currentFile is null) return;
            string srcFile = _currentFile;

            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            string printPath;
            string? tempFlattened = null;
            if (hasAnnotations || _docStampSpec is not null)
            {
                var tempClean = App.MakeTempFile("clean");
                _doc.Save(tempClean);   // UI-thread snapshot of the current doc (just serialization)
                // Snapshot the data the burn needs so the background thread reads no live UI state.
                var annotsSnap = _annotations.ToDictionary(kv => kv.Key, kv => new List<PageAnnotation>(kv.Value));
                var dimsSnap   = new Dictionary<int, (int w, int h)>(_renderDims);
                var stampSnap  = _docStampSpec?.Clone();
                var burnPath   = App.MakeTempFile("print");

                // Flatten the annotations onto a throwaway COPY on a background thread. The live _doc is never
                // touched (no close/reopen), so the UI stays responsive and the editing session keeps its
                // overlay annotations. DrawAnnotationsIntoDoc is static, so it can't reach UI state.
                bool burned = await Task.Run(() =>
                {
                    try
                    {
                        PdfDocument burnDoc;
                        try { burnDoc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify); }
                        catch (Exception ex) when (IsXRefException(ex))
                        {
                            // PdfSharpCore can write a snapshot its own reader then chokes on; repair via
                            // Import then PDFium, same as the save/undo paths.
                            var fixedPath = App.MakeTempFile("printfixed");
                            if (!TryImportRepairToPath(tempClean, fixedPath) && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                                return false;
                            burnDoc = PdfReader.Open(fixedPath, PdfDocumentOpenMode.Modify);
                        }
                        using (burnDoc)
                        {
                            DrawStampsIntoDoc(burnDoc, stampSnap, _signatureStore);   // stamps sit beneath annotations
                            DrawAnnotationsIntoDoc(burnDoc, annotsSnap, dimsSnap);
                            burnDoc.Save(burnPath);
                        }
                        return true;
                    }
                    catch { return false; }
                });

                if (!burned) SetStatus("Could not flatten annotations for printing; printing without them.");
                printPath     = burned ? burnPath : srcFile;
                tempFlattened = burned ? burnPath : null;
            }
            else
            {
                printPath = srcFile;
            }

            if (_doc is null) return;   // re-check after the await (the doc was untouched, this satisfies flow analysis)
            int pageCount = _doc.PageCount;

            // Each page's true physical size in DIPs (96/inch) so the dialog can offer an exact
            // "actual size" / custom scale. Computed on the UI thread (PdfSharp isn't thread-safe).
            var pageDipW = new double[pageCount];
            var pageDipH = new double[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                double pw = _doc.Pages[i].Width.Point;
                double ph = _doc.Pages[i].Height.Point;
                if (_pageRotations.TryGetValue(i, out int rot) && (rot == 90 || rot == 270))
                    (pw, ph) = (ph, pw);
                pageDipW[i] = pw * 96.0 / 72.0;
                pageDipH[i] = ph * 96.0 / 72.0;
            }

            // Open the preview window immediately. Pages rasterize on a background thread and
            // stream in via SetRenderedPage, so the window appears at once and the app stays
            // responsive on large files. WPF's OS PrintDialog can't show a preview, so StealthPDF
            // renders it and drives printing itself.
            string  renderPath = printPath;
            string? cleanup    = tempFlattened;
            // Preview rasters are display-only (shown fit-to-pane in a Viewbox), so render them at a
            // modest budget that scales DOWN as the document grows - this keeps the preview's resident
            // bitmaps from ballooning on large files. The Print button re-renders the chosen pages at a
            // true 300 DPI on demand (PrintPreviewWindow.DoPrint), so output stays crisp (issue #83).
            int previewBox = pageCount <= 80 ? 1536 : pageCount <= 250 ? 1100 : 800;
            var preview = new PrintPreviewWindow(this, pageCount, pageDipW, pageDipH, renderPath, cleanup);

            _ = Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(renderPath, new PageDimensions(previewBox, previewBox));
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (preview.Cancelled) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        byte[] png = RenderToPng(pr.GetImage(), w, h);
                        BitmapSource src;
                        using (var ms = new MemoryStream(png))
                            src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        src.Freeze();   // frozen so it can cross back to the UI thread
                        int ci = i;
                        try { preview.Dispatcher.Invoke(() => preview.SetRenderedPage(ci, src, w, h)); }
                        catch { return; }   // window closed mid-render
                    }
                    if (!preview.Cancelled)
                        try { preview.Dispatcher.Invoke(preview.FinishLoading); } catch { }
                }
                catch (Exception ex)
                {
                    try { preview.Dispatcher.Invoke(() => preview.LoadFailed(ex.Message)); } catch { }
                }
                // The flattened temp (cleanup) is NOT deleted here anymore: the Print button re-reads
                // renderPath to rasterize at 300 DPI, so the window owns the temp and deletes it on close.
            });

            try
            {
                if (preview.ShowDialog() == true)
                    SetStatus(string.Format(Loc("Str_Printed"), preview.PrintedPageCount));
            }
            catch (Exception ex)
            {
                try { StealthDialog.Show(this, $"Print failed:\n{ex.GetType().Name}: {ex.Message}", "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }
        }
    }
}
