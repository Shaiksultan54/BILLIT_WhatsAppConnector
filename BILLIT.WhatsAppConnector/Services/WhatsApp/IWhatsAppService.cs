namespace BILLIT.WhatsAppConnector.Services.WhatsApp
{
    public enum WhatsAppMode
    {
        Disabled,
        LocalBridge,
        CloudApi
    }

    public record WhatsAppStatus(
        WhatsAppMode Mode,
        string State, // "Disabled", "Disconnected", "PendingQr", "Connected", "Expired", "Error"
        string? ConnectedNumber = null,
        string? QrCodeBase64 = null,
        string? ErrorMessage = null
    );

    public interface IWhatsAppService
    {
        WhatsAppMode Mode { get; }
        Task<WhatsAppStatus> GetStatusAsync();
        Task StartAsync();
        Task DisconnectAsync();
        Task<(bool Success, string? MessageId, string? Error)> SendTextAsync(long messageLogId, string to, string message);
        Task<(bool Success, string? MessageId, string? Error)> SendDocumentAsync(long messageLogId, string to, string caption, byte[] fileBytes, string fileName);

        // Event notifications for UI / SignalR push
        event Func<WhatsAppStatus, Task>? StatusChanged;
    }
}
