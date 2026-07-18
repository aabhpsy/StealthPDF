using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace StealthPDF.Services
{
    internal static class ScannerService
    {
        // WIA device-type enum values. 1 = Scanner (Still Image), 2 = Video, 3 = Audio. We accept
        // Type 1 (the spec value for scanners). Some multi-function devices also expose a WIA "device
        // class" via the DeviceType string; we additionally keep anything whose Type is 1 OR whose
        // name looks like a scanner if enumeration returns nothing useful. We do NOT filter by the
        // ScannerDeviceType CLSID here (that is only used by the native Select-Device dialog).
        private const string ScannerDeviceType = "{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}";
        private const string WiaFormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";

        // ── TWAIN helper (x86) ─────────────────────────────────────────────
        // The main app is x64 and cannot load 32-bit TWAIN drivers, so it launches an x86 helper
        // (StealthPDF.TwainHelper.exe, copied next to the app at build time) that enumerates and
        // acquires from TWAIN via the 32-bit twain_32.dll DSM. Device ids that start with "twain:"
        // route to the helper; ids starting with "wia:" (or no prefix) use WIA in-process.

        private const string TwainIdPrefix = "twain:";
        private const string WiaIdPrefix   = "wia:";

        private static readonly string TwainHelperExe =
            Path.Combine(AppContext.BaseDirectory, "TwainHelper", "StealthPDF.TwainHelper.exe");

        public static bool HasScanner()
        {
            // A scanner is available if EITHER WIA sees one in-process OR the x86 TWAIN helper lists
            // at least one source. Checking TWAIN is important because WIA often sees nothing for
            // TWAIN-only drivers — exactly the case this helper exists for.
            if (HasWiaScanner()) return true;
            return TwainHelperCount() > 0;
        }

        public static List<(string id, string name)> ListScanners()
        {
            var list = new List<(string, string)>();

            // TWAIN first (matches what Adobe and most scanner apps show). Each TWAIN entry's id
            // encodes its index as twain:<index> so Acquire can pass it back to the helper.
            foreach (var (twainIndex, name) in ListTwainScanners())
                list.Add((TwainIdPrefix + twainIndex, "[TWAIN] " + name));

            // Then WIA devices. id = wia:<DeviceID>.
            foreach (var (id, name) in ListWiaScanners())
                list.Add((WiaIdPrefix + id, "[WIA] " + name));

            return list;
        }

        public static List<string> Acquire(string? deviceId, int dpi, ScanColor color, bool duplex)
        {
            // Route by id prefix. null/unknown -> try WIA native picker, then TWAIN helper.
            if (!string.IsNullOrEmpty(deviceId) && deviceId!.StartsWith(TwainIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return AcquireTwain(deviceId[TwainIdPrefix.Length..], dpi, color, duplex);
            }

            // WIA path (in-process). Every COM handle is explicitly released in finally: without
            // that, closing the Windows scanner dialog while a COM object is still referenced could
            // leave the WIA driver's DLL torn down while the CLR still holds a proxy, which then
            // faults on the next reference and takes the whole app down (the "closing the scanner
            // tool crashes StealthPDF" report). SEHException/AccessViolationException are also
            // caught here because some WIA vendor drivers marshal those back through the STA on
            // cancel.
            var results = new List<string>();
            dynamic? manager = null;
            dynamic? device = null;
            dynamic? connected = null;
            try
            {
                manager = CreateWiaManager();
                if (!string.IsNullOrEmpty(deviceId))
                {
                    string wiaId = deviceId!.StartsWith(WiaIdPrefix, StringComparison.OrdinalIgnoreCase)
                        ? deviceId[WiaIdPrefix.Length..] : deviceId;
                    foreach (var dev in manager.Devices)
                    {
                        if (string.Equals((string)dev.DeviceID, wiaId, StringComparison.OrdinalIgnoreCase))
                        { device = dev; break; }
                        SafeReleaseCom(dev);
                    }
                }
                // If the chosen scanner wasn't found (e.g. unplugged), fall back to Windows' native
                // Select-Device dialog so the user can pick any scanner the system knows about.
                if (device == null) device = PickDevice();
                if (device == null) return results;
                connected = device.Connect();
                bool feeder = FeederPresent(connected);
                int maxPages = (feeder || duplex) ? 100 : 1;
                for (int i = 0; i < maxPages; i++)
                {
                    var png = AcquireOne(connected, dpi, color, duplex);
                    if (png == null) break;
                    results.Add(png);
                }
            }
            catch (COMException) { }
            catch (System.Runtime.InteropServices.SEHException) { /* driver crashed after cancel */ }
            catch { }
            finally
            {
                SafeReleaseCom(connected);
                SafeReleaseCom(device);
                SafeReleaseCom(manager);
                // A hard GC after WIA hands its RCWs back stops the finalizer thread from touching
                // a torn-down vendor DLL later - the real root of the crash-on-close.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return results;
        }

        // Fire-and-forget release of a WIA/COM RCW. Never throws so it is safe to call from finally.
        private static void SafeReleaseCom(object? o)
        {
            if (o is null) return;
            try
            {
                if (Marshal.IsComObject(o))
                    Marshal.FinalReleaseComObject(o);
            }
            catch { }
        }
        // ── TWAIN bridge (launches the x86 helper out-of-process) ──────────

        private static bool HasWiaScanner()
        {
            try
            {
                dynamic manager = CreateWiaManager();
                foreach (var dev in manager.Devices)
                    if (dev.Type == 1 || dev.Type == 0) return true;
                return false;
            }
            catch { return false; }
        }

        private static List<(string id, string name)> ListWiaScanners()
        {
            var list = new List<(string, string)>();
            try
            {
                dynamic manager = CreateWiaManager();
                foreach (var dev in manager.Devices)
                    if (dev.Type == 1 || dev.Type == 0)
                        list.Add((dev.DeviceID, dev.Name));
            }
            catch { }
            return list;
        }

        // Runs `StealthPDF.TwainHelper.exe list` and returns each source as (index, name).
        // Returns an empty list if the helper is missing or fails (e.g. no TWAIN DSM installed).
        private static List<(int index, string name)> ListTwainScanners()
        {
            var list = new List<(int, string)>();
            try
            {
                if (!File.Exists(TwainHelperExe)) return list;
                var (out_, err, code) = RunHelper("list");
                if (code != 0) return list;

                // Parse "<index>\t<name>" lines until "DONE\t<count>".
                foreach (var line in out_.Split('\n'))
                {
                    var t = line.Trim('\r');
                    if (string.IsNullOrEmpty(t)) continue;
                    if (t.StartsWith("DONE\t", StringComparison.OrdinalIgnoreCase)) break;
                    var tab = t.IndexOf('\t');
                    if (tab <= 0) continue;
                    if (int.TryParse(t.Substring(0, tab), out int idx))
                        list.Add((idx, t.Substring(tab + 1)));
                }
            }
            catch { }
            return list;
        }

        private static int TwainHelperCount()
        {
            var list = ListTwainScanners();
            return list.Count;
        }

        // Runs `StealthPDF.TwainHelper.exe acquire <index> <dpi> <color> <duplex> <outDir>`.
        // Returns the list of acquired PNG paths. Empty if cancelled or failed.
        private static List<string> AcquireTwain(string indexStr, int dpi, ScanColor color, bool duplex)
        {
            var results = new List<string>();
            try
            {
                if (!File.Exists(TwainHelperExe)) return results;

                string colorArg = color switch
                {
                    ScanColor.Color     => "color",
                    ScanColor.Grayscale => "gray",
                    ScanColor.Text      => "bw",
                    _                   => "gray",
                };
                string outDir = Path.Combine(Path.GetTempPath(), "StealthPDF-twain");
                Directory.CreateDirectory(outDir);

                var (out_, err, code) = RunHelper($"acquire {indexStr} {dpi} {colorArg} {(duplex ? 1 : 0)} \"{outDir}\"");
                if (code != 0) return results;

                // Each page printed as an absolute PNG path; "DONE\t<n>" terminates.
                foreach (var line in out_.Split('\n'))
                {
                    var t = line.Trim('\r');
                    if (string.IsNullOrEmpty(t)) continue;
                    if (t.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) { results.Clear(); return results; }
                    if (t.StartsWith("DONE\t", StringComparison.OrdinalIgnoreCase)) break;
                    if (File.Exists(t)) results.Add(t);
                }
            }
            catch { }
            return results;
        }

        // Launches the x86 helper synchronously, capturing stdout/stderr and the exit code.
        // Run hidden (no window) so it doesn't flash a console behind the scanner's own UI.
        private static (string stdout, string stderr, int code) RunHelper(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = TwainHelperExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The helper needs to find twain_32.dll; its working dir should be its own folder.
                WorkingDirectory = Path.GetDirectoryName(TwainHelperExe)!,
            };
            Process? p = null;
            try
            {
                p = Process.Start(psi);
                if (p == null) return ("", "failed to start helper", -1);
                // Read synchronously to avoid deadlocks when the helper writes a lot before exit.
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                // Generous wait: the scanner UI may stay open while the user previews/adjusts.
                bool exited = p.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds);
                if (!exited)
                {
                    // A killed helper cannot report success. Return a cancel-like code so the
                    // caller treats it as no pages instead of crashing on p.ExitCode below (the
                    // real "closing scanner crashes app" trigger when the user aborts mid-scan).
                    try { p.Kill(); } catch { }
                    return (stdout, stderr, -1);
                }
                int code;
                try { code = p.ExitCode; } catch { code = -1; }
                return (stdout, stderr, code);
            }
            catch (Exception ex)
            {
                return ("", ex.Message, -1);
            }
            finally
            {
                try { p?.Dispose(); } catch { }
            }
        }

        private static string? AcquireOne(dynamic connected, int dpi, ScanColor color, bool duplex)
        {
            try
            {
                dynamic item = connected.Items[1];
                ApplyScanProperties(item, dpi, color, duplex);
                dynamic commonDialog = CreateWiaCommonDialog();
                dynamic imageFile = commonDialog.ShowAcquireImage(item, WiaFormatPng, false, true, true);
                if (imageFile == null) return null;
                string path = Path.Combine(Path.GetTempPath(), $"StealthPDF-scan-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                imageFile.SaveFile(path);
                return path;
            }
            catch (COMException) { return null; }
        }

        private static dynamic? PickDevice()
        {
            try
            {
                dynamic commonDialog = CreateWiaCommonDialog();
                return commonDialog.ShowSelectDevice(ScannerDeviceType, false, true);
            }
            catch (COMException) { return null; }
        }

        private static void ApplyScanProperties(dynamic item, int dpi, ScanColor color, bool duplex)
        {
            try
            {
                int colorVal = color switch
                {
                    ScanColor.Color => 0,
                    ScanColor.Grayscale => 1,
                    ScanColor.Text => 2,
                    _ => 1
                };
                foreach (var prop in item.Properties)
                {
                    int id = (int)prop.PropertyID;
                    if (id == 6147 || id == 6148) prop.set_Value(dpi);
                    else if (id == 4101) prop.set_Value(colorVal);
                }
                if (duplex)
                    foreach (var prop in item.Properties)
                        if ((int)prop.PropertyID == 3088) { prop.set_Value(3); break; }
            }
            catch { }
        }

        private static bool FeederPresent(dynamic connected)
        {
            try
            {
                dynamic item = connected.Items[1];
                foreach (var prop in item.Properties)
                    if ((int)prop.PropertyID == 3088) return true;
                return false;
            }
            catch { return false; }
        }

        private static dynamic CreateWiaManager()
            => Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager", throwOnError: true)!);
        private static dynamic CreateWiaCommonDialog()
            => Activator.CreateInstance(Type.GetTypeFromProgID("WIA.CommonDialog", throwOnError: true)!);
    }

    internal enum ScanColor { Color, Grayscale, Text }
}
