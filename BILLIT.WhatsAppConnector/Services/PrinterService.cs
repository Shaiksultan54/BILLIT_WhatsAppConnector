using System.Diagnostics;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;

namespace BILLIT.WhatsAppConnector.Services
{
    public class PrinterService : IPrinterService
    {
        private readonly ILogger<PrinterService> _logger;
        private readonly IConfiguration _config;
        private readonly SemaphoreSlim _printLock = new(1, 1);
        private const int MaxPdfSizeBytes = 25 * 1024 * 1024; // 25 MB max limit

        public PrinterService(ILogger<PrinterService> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public IReadOnlyList<string> GetInstalledPrinters()
        {
            var names = new List<string>();
            try
            {
                foreach (string name in PrinterSettings.InstalledPrinters)
                {
                    names.Add(name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate installed printers");
            }
            return names;
        }

        public string? GetDefaultPrinterName()
        {
            try
            {
                var ps = new PrinterSettings();
                return ps.IsValid ? ps.PrinterName : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get default printer name");
                return null;
            }
        }

        public IReadOnlyList<PrinterInfo> GetPrinterDetails()
        {
            var defaultPrinter = GetDefaultPrinterName();
            var list = new List<PrinterInfo>();
            foreach (var printer in GetInstalledPrinters())
            {
                bool isDefault = string.Equals(printer, defaultPrinter, StringComparison.OrdinalIgnoreCase);
                bool isOnline = true;
                string? port = null;

                try
                {
                    var ps = new PrinterSettings { PrinterName = printer };
                    isOnline = ps.IsValid;
                }
                catch { }

                list.Add(new PrinterInfo(printer, isDefault, isOnline, port));
            }
            return list;
        }

        public async Task<(bool Success, string? Error)> PrintPdfAsync(byte[] pdfBytes, string? printerName, int copies = 1, string? paperSize = "80mm")
        {
            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                return (false, "Cannot print empty document bytes.");
            }

            if (pdfBytes.Length > MaxPdfSizeBytes)
            {
                return (false, $"Document size ({pdfBytes.Length / (1024 * 1024)}MB) exceeds the maximum allowed limit of 25MB.");
            }

            copies = Math.Clamp(copies, 1, 100);

            var sumatraPath = AppPaths.ResolveSumatraPdfPath(_config["Connector:SumatraPdfPath"]);
            if (string.IsNullOrEmpty(sumatraPath) || !File.Exists(sumatraPath))
            {
                return (false, $"SumatraPDF.exe not found at '{sumatraPath}'. Ensure SumatraPDF is in the tools folder or install it on the POS station.");
            }

            var installed = GetInstalledPrinters();
            if (!string.IsNullOrWhiteSpace(printerName) && !installed.Contains(printerName, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Requested printer '{PrinterName}' is not installed — falling back to default printer.", printerName);
                printerName = null;
            }

            var tempPdfPath = Path.Combine(AppPaths.TempDirectory, $"billit-print-{Guid.NewGuid():N}.pdf");

            // Serialize print spooling so multiple rapid concurrent requests do not collide
            await _printLock.WaitAsync();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await File.WriteAllBytesAsync(tempPdfPath, pdfBytes);

                var targetPrinterArg = string.IsNullOrWhiteSpace(printerName)
                    ? "-print-to-default"
                    : $"-print-to \"{printerName}\"";

                // Optimized paper fit settings
                var settingsList = new List<string>();
                if (string.Equals(paperSize, "A4", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(paperSize, "Letter", StringComparison.OrdinalIgnoreCase))
                {
                    settingsList.Add("fit");
                }
                else // 80mm or 58mm thermal rolls
                {
                    settingsList.Add("shrink");
                    settingsList.Add("fit");
                }

                if (copies > 1)
                {
                    settingsList.Add($"{copies}x");
                }

                var settingsArg = settingsList.Count > 0
                    ? $"-print-settings \"{string.Join(",", settingsList)}\""
                    : "";

                var psi = new ProcessStartInfo
                {
                    FileName = sumatraPath,
                    Arguments = $"{targetPrinterArg} {settingsArg} -silent \"{tempPdfPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _logger.LogInformation("Spooling PDF job to '{Printer}' (Copies: {Copies}, Paper: {Paper}): {File}",
                    printerName ?? "Default", copies, paperSize ?? "80mm", Path.GetFileName(tempPdfPath));

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return (false, "Failed to launch printing utility.");
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (false, "Print job timed out after 45 seconds.");
                }

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    _logger.LogWarning("Printer utility exited with code {Code}: {Error}", process.ExitCode, stderr);
                    return (false, string.IsNullOrWhiteSpace(stderr) ? $"Printer process exited with code {process.ExitCode}." : stderr.Trim());
                }

                stopwatch.Stop();
                _logger.LogInformation("Print job successfully completed in {Elapsed}ms.", stopwatch.ElapsedMilliseconds);
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during PDF print execution");
                return (false, ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPdfPath))
                    {
                        File.Delete(tempPdfPath);
                    }
                }
                catch { }

                _printLock.Release();
            }
        }

        public Task<(bool Success, string? Error)> PrintRawAsync(byte[] rawBytes, string? printerName)
        {
            var target = string.IsNullOrWhiteSpace(printerName) ? GetDefaultPrinterName() : printerName;
            if (string.IsNullOrWhiteSpace(target))
            {
                return Task.FromResult<(bool, string?)>((false, "No printer specified and no default printer found."));
            }

            try
            {
                bool success = RawPrinterHelper.SendBytesToPrinter(target, rawBytes);
                if (success)
                {
                    _logger.LogInformation("Dispatched {Bytes} raw bytes to printer '{Printer}'.", rawBytes.Length, target);
                    return Task.FromResult<(bool, string?)>((true, null));
                }
                return Task.FromResult<(bool, string?)>((false, "Printer spooler rejected the raw buffer. Check if printer is online and accepts RAW commands."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Raw print failed on '{Printer}'", target);
                return Task.FromResult<(bool, string?)>((false, ex.Message));
            }
        }

        public Task<(bool Success, string? Error)> PrintRawTextAsync(string text, string? printerName)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Task.FromResult<(bool, string?)>((false, "Cannot print empty text."));
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            return PrintRawAsync(bytes, printerName);
        }

        public Task<(bool Success, string? Error)> OpenCashDrawerAsync(string? printerName)
        {
            // Standard ESC/POS Cash Drawer Kick Command: ESC p 0 25 250 (Pin 2)
            byte[] drawerPulse = new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA };
            return PrintRawAsync(drawerPulse, printerName);
        }

        public Task<(bool Success, string? Error)> CutPaperAsync(string? printerName)
        {
            // Standard ESC/POS Paper Cut with feed: GS V 65 3
            byte[] cutCommand = new byte[] { 0x1D, 0x56, 0x41, 0x03 };
            return PrintRawAsync(cutCommand, printerName);
        }

        public async Task<(bool Success, string? Error)> PrintTestReceiptAsync(string? printerName)
        {
            var target = string.IsNullOrWhiteSpace(printerName) ? GetDefaultPrinterName() : printerName;
            if (string.IsNullOrWhiteSpace(target))
            {
                return (false, "No default printer found on this system.");
            }

            var sb = new StringBuilder();
            sb.Append("\x1B\x40");      // Initialize printer
            sb.Append("\x1B\x61\x01");  // Center align
            sb.Append("\x1B\x45\x01");  // Bold on
            sb.Append("================================\n");
            sb.Append("       BILLIT DEVICE AGENT      \n");
            sb.Append("    THERMAL PRINTER TEST PAGE   \n");
            sb.Append("================================\n");
            sb.Append("\x1B\x45\x00");  // Bold off
            sb.Append("\x1B\x61\x00");  // Left align
            sb.Append($"Date/Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            sb.Append($"Printer   : {target}\n");
            sb.Append($"Host Port : http://localhost:5050\n");
            sb.Append($"Status    : 100% Operational\n");
            sb.Append("--------------------------------\n");
            sb.Append("✓ Local Printing Engine: OK\n");
            sb.Append("✓ Windows Spooler Link : OK\n");
            sb.Append("✓ Thermal ESC/POS Sync : OK\n");
            sb.Append("--------------------------------\n");
            sb.Append("\x1B\x61\x01");  // Center align
            sb.Append("BILLIT POS Hardware Verification\n\n\n\n");
            sb.Append("\x1D\x56\x41\x03"); // Paper cut

            byte[] rawBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var (rawSuccess, rawError) = await PrintRawAsync(rawBytes, target);
            if (rawSuccess)
            {
                return (true, null);
            }

            return (false, $"Raw test spool rejected: {rawError}. Note: Laser/GDI printers require PDF printing via /api/print.");
        }

        // --- Low-level Windows Spooler P/Invoke ---
        private static class RawPrinterHelper
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            public class DOCINFOA
            {
                [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
                [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
                [MarshalAs(UnmanagedType.LPStr)] public string? pDataType;
            }

            [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

            [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern bool ClosePrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

            [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern bool EndDocPrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern bool StartPagePrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern bool EndPagePrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

            public static bool SendBytesToPrinter(string szPrinterName, byte[] bytes)
            {
                var pUnmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, pUnmanagedBytes, bytes.Length);

                bool success = false;
                if (OpenPrinter(szPrinterName.Normalize(), out var hPrinter, IntPtr.Zero))
                {
                    var di = new DOCINFOA
                    {
                        pDocName = "BILLIT Direct Raw Print",
                        pDataType = "RAW"
                    };

                    if (StartDocPrinter(hPrinter, 1, di))
                    {
                        if (StartPagePrinter(hPrinter))
                        {
                            success = WritePrinter(hPrinter, pUnmanagedBytes, bytes.Length, out _);
                            EndPagePrinter(hPrinter);
                        }
                        EndDocPrinter(hPrinter);
                    }
                    ClosePrinter(hPrinter);
                }
                Marshal.FreeCoTaskMem(pUnmanagedBytes);
                return success;
            }
        }
    }
}
