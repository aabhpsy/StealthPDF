using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace StealthPDF
{
    // ============================================================
    // Remove Password / Break Encryption
    // ============================================================
    //
    // Two levels of "locked" PDFs exist in the wild:
    //
    //   1. Owner-password only (a.k.a. "permissions password"). The user password is empty, so any
    //      viewer opens the file - but printing, editing, and copying are refused unless the owner
    //      password is supplied. This is the case Adobe calls "restricted" and is what almost every
    //      "locked" invoice / bank statement really is.
    //
    //   2. User password. The file cannot be opened at all without the password. StealthPDF already
    //      prompts for that via PromptForPassword() in the normal open path.
    //
    // For case (1), stripping is straightforward: PDFium's FPDF_SaveWithVersion(FPDF_REMOVE_SECURITY)
    // rewrites the file with the /Encrypt dictionary removed. For case (2), PDFium needs the password
    // as well; if the doc is already open in StealthPDF the user has already supplied it, so we can
    // save via PdfSharp's Import mode which drops the security when copying pages to a fresh doc.
    //
    // The tool always writes an unlocked COPY (Save-As) instead of overwriting the source, so an
    // accidental click on a normal file can never destroy the original.
    public partial class MainWindow
    {
        private async void RemovePassword_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null)
            {
                KillerDialog.Show(this, Loc("Str_Unlock_NoDoc"), "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // A file that has no /Encrypt trailer is already unlocked - nothing to do.
            string sourceForCheck = _currentFile;
            if (!PdfFileHasEncryption(sourceForCheck))
            {
                KillerDialog.Show(this, Loc("Str_Unlock_None"), "StealthPDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = KillerDialog.Show(this, Loc("Str_Unlock_Confirm").Replace("\\n", "\n"),
                Loc("Str_Unlock_Title"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            var save = new SaveFileDialog
            {
                Filter = "PDF files|*.pdf",
                Title  = Loc("Str_Unlock_Title"),
                FileName = System.IO.Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile) + "-unlocked.pdf",
                CheckFileExists = false,
                CheckPathExists = true,
            };
            if (save.ShowDialog(this) != true) return;
            string destPath = save.FileName;

            // Snapshot the current doc to a temp file - the live _doc may be dirty, and PDFium needs
            // a real path to read from. This also means user edits are preserved in the unlocked copy.
            string sourcePath = App.MakeTempFile("unlocksrc");
            _doc.Save(sourcePath);

            var busy = ShowBusyOverlay(Loc("Str_Unlock_Working"));
            try
            {
                bool ok = await Task.Run(() =>
                {
                    // Strategy 1: PDFium REMOVE_SECURITY - true, lossless strip of the /Encrypt dict.
                    if (TryPdfiumStripEncryption(sourcePath, destPath)) return true;
                    // Strategy 2: PdfSharp Import - copies pages into a fresh unencrypted doc.
                    return TryImportRepairToPath(sourcePath, destPath);
                });

                if (!ok)
                {
                    KillerDialog.Show(this, Loc("Str_Unlock_Failed"), "StealthPDF",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                SetStatus(string.Format(Loc("Str_Unlock_Done"), System.IO.Path.GetFileName(destPath)));
                // Offer to open the unlocked copy in a new tab so the user can immediately keep working
                // with a version that actually allows printing / editing.
                var openIt = KillerDialog.Show(this,
                    $"Unlocked copy saved:\n{destPath}\n\nOpen the unlocked copy now?",
                    "StealthPDF", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (openIt == MessageBoxResult.Yes) OpenInNewTab(destPath);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Unlock_Failed") + "\n\n" + ex.Message,
                    "StealthPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideBusyOverlay(busy);
                try { File.Delete(sourcePath); } catch { }
            }
        }
    }
}
