using System.Diagnostics;
using System.Drawing.Printing;

namespace BILLIT.WhatsAppConnector
{
    /// <summary>
    /// Wraps the machine's installed Windows printers and silent PDF printing.
    ///
    /// .NET has no built-in PDF renderer, so this shells out to a small,
    /// portable, silent PDF-printing CLI. SumatraPDF is the standard choice
    /// for this and is widely bundled with POS/invoicing software for
    /// exactly this purpose (it's GPL-licensed — fine to invoke as a
    /// separate process, but check that license fits your distribution
    /// model before shipping it; swap in a different CLI printer, e.g. a
    /// Ghostscript-based one, if it doesn't).
    ///
    /// Setup: download the "SumatraPDF (32-bit or 64-bit) - portable" build
    /// from https://www.sumatrapdfreader.org/download-free-pdf-viewer, drop
    /// SumatraPDF.exe into a `tools\` folder next to the connector's .exe,
    /// and either leave Connector:SumatraPdfPath unset (this class defaults
    /// to that path) or point it elsewhere in appsettings.json.
    /// </summary>
    public class PrinterManager
    {
        private readonly ILogger<PrinterManager> _logger;
        private readonly string _sumatraPdfPath;

        public PrinterManager(ILogger<PrinterManager> logger, IConfiguration config)
        {
            _logger = logger;
            _sumatraPdfPath = config["Connector:SumatraPdfPath"]
                ?? Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.exe");
        }

        /// <summary>All printer names Windows currently knows about on this machine (installed, not necessarily online).</summary>
        public IReadOnlyList<string> GetInstalledPrinters()
        {
            var names = new List<string>();
            foreach (string name in PrinterSettings.InstalledPrinters) names.Add(name);
            return names;
        }

        /// <summary>The machine's current Windows default printer, or null if none is set.</summary>
        public string? GetDefaultPrinterName()
        {
            var ps = new PrinterSettings();
            return ps.IsValid ? ps.PrinterName : null;
        }

        /// <summary>
        /// Prints a PDF silently (no dialogs, no viewer window popping up in
        /// front of the cashier) to the named printer, or the machine's
        /// current Windows default printer if <paramref name="printerName"/>
        /// is null/empty/no longer installed.
        /// </summary>
        public async Task<(bool Success, string? Error)> PrintPdfAsync(byte[] pdfBytes, string? printerName, int copies)
        {
            if (!File.Exists(_sumatraPdfPath))
            {
                return (false,
                    $"SumatraPDF.exe not found at '{_sumatraPdfPath}'. Bundle it with the connector " +
                    "(tools\\SumatraPDF.exe next to the .exe) or set Connector:SumatraPdfPath in appsettings.json.");
            }

            if (!string.IsNullOrWhiteSpace(printerName) && !GetInstalledPrinters().Contains(printerName, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Configured printer '{PrinterName}' is not currently installed — falling back to the default printer.", printerName);
                printerName = null;
            }

            var tempPdfPath = Path.Combine(Path.GetTempPath(), $"billit-print-{Guid.NewGuid():N}.pdf");
            try
            {
                await File.WriteAllBytesAsync(tempPdfPath, pdfBytes);

                var printTarget = string.IsNullOrWhiteSpace(printerName) ? "-print-to-default" : $"-print-to \"{printerName}\"";
                
                // Add shrink/fit settings specifically for 80mm receipt printers to ensure the PDF scales correctly to the paper width.
                var baseSettings = "shrink,fit";
                var settingsArg = copies > 1 ? $" -print-settings \"{baseSettings},{copies}x\"" : $" -print-settings \"{baseSettings}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = _sumatraPdfPath,
                    Arguments = $"{printTarget}{settingsArg} -silent \"{tempPdfPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null) return (false, "Failed to start the printer tool process.");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return (false, "Print job timed out after 30 seconds.");
                }

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    _logger.LogWarning("Printer tool exited with code {Code}: {Error}", process.ExitCode, stderr);
                    return (false, string.IsNullOrWhiteSpace(stderr) ? $"Printer tool exited with code {process.ExitCode}." : stderr.Trim());
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Printing failed");
                return (false, ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempPdfPath)) File.Delete(tempPdfPath); } catch { /* best effort cleanup */ }
            }
        }
    }
}
