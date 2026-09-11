using BILLIT.WhatsAppConnector.Services;

namespace BILLIT.WhatsAppConnector
{
    /// <summary>
    /// Legacy wrapper around IPrinterService for backward compatibility with existing components.
    /// </summary>
    public class PrinterManager : PrinterService
    {
        public PrinterManager(ILogger<PrinterManager> logger, IConfiguration config)
            : base(logger, config)
        {
        }
    }
}
