namespace BILLIT.WhatsAppConnector.Services
{
    public record PrinterInfo(string Name, bool IsDefault, bool IsOnline = true, string? PortName = null);

    public record PrintJobRequest(
        string? PrinterName,
        int Copies,
        string FileBase64,
        string? PaperSize = "80mm" // "80mm", "58mm", "A4", or "Default"
    );

    public record RawPrintRequest(
        string? PrinterName,
        string DataBase64
    );

    public interface IPrinterService
    {
        IReadOnlyList<string> GetInstalledPrinters();
        string? GetDefaultPrinterName();
        IReadOnlyList<PrinterInfo> GetPrinterDetails();
        Task<(bool Success, string? Error)> PrintPdfAsync(byte[] pdfBytes, string? printerName, int copies = 1, string? paperSize = "80mm");
        Task<(bool Success, string? Error)> PrintRawAsync(byte[] rawBytes, string? printerName);
        Task<(bool Success, string? Error)> PrintRawTextAsync(string text, string? printerName);
        Task<(bool Success, string? Error)> PrintTestReceiptAsync(string? printerName);
        Task<(bool Success, string? Error)> OpenCashDrawerAsync(string? printerName);
        Task<(bool Success, string? Error)> CutPaperAsync(string? printerName);
    }
}
