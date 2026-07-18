// StealthPDF.TwainHelper — out-of-process x86 bridge to TWAIN scanners.
//
// The main StealthPDF app runs 64-bit (for 64-bit pdfium/Tesseract), so it cannot load
// 32-bit TWAIN drivers. This x86 helper loads the 32-bit twain_32.dll DSM and sees every
// installed TWAIN scanner. The main app launches it on demand and reads stdout.
//
// Protocol (stdout is the ONLY channel the parent parses; stderr is diagnostics):
//   list
//       One line per scanner:  <index>	<productName>, then DONE	<count>, exit 0.
//   acquire <index> <dpi> <color> <duplex> <outDir>
//       Acquires pages, prints each PNG absolute path on stdout, then DONE	<count>.
//       If the user cancels at the scanner UI, prints CANCELLED and exits 0.
// NTwain hosts its own internal message loop.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NTwain;
using NTwain.Data;

namespace StealthPDF.TwainHelper
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.Error.WriteLine("usage: StealthPDF.TwainHelper list | acquire <index> <dpi> <color> <duplex> <outDir>");
                    return 1;
                }

                // Prefer legacy twain_32.dll DSM for max compatibility with older 32-bit drivers.
                PlatformInfo.Current.PreferNewDSM = false;

                string cmd = args[0].ToLowerInvariant();
                return cmd switch
                {
                    "list"    => DoList(),
                    "acquire" => DoAcquire(args),
                    _         => BadCommand(cmd),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("TWAIN helper error: " + ex.Message);
                return 2;
            }
        }

        private static int BadCommand(string cmd)
        {
            Console.Error.WriteLine($"unknown command {cmd}");
            return 1;
        }

        // ── list ──────────────────────────────────────────────────────────

        private static int DoList()
        {
            var session = OpenSession();
            if (session == null) return 1;
            try
            {
                var sources = session.ToList();
                int count = 0;
                foreach (var ds in sources)
                {
                    // index 	 product name. Tab-separated so names with spaces split cleanly.
                    Console.WriteLine($"{count}\t{ds.Name}");
                    count++;
                }
                Console.WriteLine($"DONE\t{count}");
                return 0;
            }
            finally { try { session.Close(); } catch { } }
        }

        // ── acquire ───────────────────────────────────────────────────────

        private static int DoAcquire(string[] args)
        {
            if (args.Length < 6)
            {
                Console.Error.WriteLine("usage: acquire <index> <dpi> <color> <duplex> <outDir>");
                return 1;
            }
            if (!int.TryParse(args[1], out int index)) { Console.Error.WriteLine("bad index"); return 1; }
            if (!int.TryParse(args[2], out int dpi)) { Console.Error.WriteLine("bad dpi"); return 1; }
            string colorStr = args[3].ToLowerInvariant();
            bool duplex = args[4] == "1" || args[4].Equals("true", StringComparison.OrdinalIgnoreCase);
            string outDir = args[5];

            Directory.CreateDirectory(outDir);

            PixelType pixType = colorStr switch
            {
                "color" => PixelType.RGB,
                "gray"  => PixelType.Gray,
                "bw"    => PixelType.BlackWhite,
                _       => PixelType.Gray,
            };

            var session = OpenSession();
            if (session == null) return 1;

            try
            {
                var sources = session.ToList();
                if (index < 0 || index >= sources.Count)
                {
                    Console.Error.WriteLine($"index {index} out of range (have {sources.Count} sources)");
                    return 1;
                }

                var ds = sources[index];
                var rc = ds.Open();
                if (rc != ReturnCode.Success && rc != ReturnCode.CheckStatus)
                {
                    Console.Error.WriteLine($"open source failed: {rc}");
                    return 1;
                }

                try
                {
                    ApplyCapabilities(ds, dpi, pixType, duplex);

                    using var done = new ManualResetEventSlim(false);
                    var pngs = new List<string>();
                    bool cancelled = false;

                    session.DataTransferred += (s, e) =>
                    {
                        var stream = e.GetNativeImageStream();
                        if (stream == null) return;
                        string path = Path.Combine(outDir, $"StealthPDF-twain-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{pngs.Count}.png");
                        using var fs = File.Create(path);
                        stream.CopyTo(fs);
                        pngs.Add(path);
                        Console.WriteLine(path);
                        Console.Out.Flush();
                    };
                    session.SourceDisabled += (s, e) => done.Set();
                    session.TransferCanceled += (s, e) => { cancelled = true; done.Set(); };
                    session.TransferError += (s, e) => { };

                    // Show the scanner's native UI (same dialog Adobe presents).
                    var enableRc = ds.Enable(SourceEnableMode.ShowUI, true, IntPtr.Zero);
                    if (enableRc != ReturnCode.Success && enableRc != ReturnCode.CheckStatus)
                    {
                        Console.Error.WriteLine($"enable failed: {enableRc}");
                        return 1;
                    }

                    if (!done.Wait(TimeSpan.FromMinutes(15)))
                        Console.Error.WriteLine("scan timed out");

                    if (pngs.Count == 0) cancelled = true;

                    if (cancelled)
                    {
                        Console.WriteLine("CANCELLED");
                        return 0;
                    }
                    Console.WriteLine($"DONE\t{pngs.Count}");
                    return 0;
                }
                finally
                {
                    try { ds.Close(); } catch { }
                }
            }
            finally { try { session.Close(); } catch { } }
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static TwainSession? OpenSession()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
            var session = new TwainSession(appId);
            var rc = session.Open();
            if (rc != ReturnCode.Success && rc != ReturnCode.CheckStatus)
            {
                Console.Error.WriteLine($"twain session open failed: {rc}");
                return null;
            }
            return session;
        }

        private static void ApplyCapabilities(DataSource ds, int dpi, PixelType pixType, bool duplex)
        {
            try
            {
                // Resolution. TWFix32 has an implicit conversion from float/double so int dpi works.
                if (ds.Capabilities.ICapXResolution.CanSet)
                    ds.Capabilities.ICapXResolution.SetValue(dpi);
                if (ds.Capabilities.ICapYResolution.CanSet)
                    ds.Capabilities.ICapYResolution.SetValue(dpi);

                // Pixel type (color / gray / BW). Only set if the driver actually supports the value.
                if (ds.Capabilities.ICapPixelType.CanSet &&
                    ds.Capabilities.ICapPixelType.GetValues().Contains(pixType))
                {
                    ds.Capabilities.ICapPixelType.SetValue(pixType);
                }

                // Feeder + duplex. These use BoolType, not bool.
                var boolVal = duplex ? BoolType.True : BoolType.False;
                if (ds.Capabilities.CapFeederEnabled.CanSet &&
                    ds.Capabilities.CapFeederEnabled.GetValues().Contains(boolVal))
                    ds.Capabilities.CapFeederEnabled.SetValue(boolVal);
                if (ds.Capabilities.CapDuplexEnabled.CanSet &&
                    ds.Capabilities.CapDuplexEnabled.GetValues().Contains(boolVal))
                    ds.Capabilities.CapDuplexEnabled.SetValue(boolVal);
            }
            catch
            {
                // Best-effort; the scanner UI lets the user override.
            }
        }
    }
}