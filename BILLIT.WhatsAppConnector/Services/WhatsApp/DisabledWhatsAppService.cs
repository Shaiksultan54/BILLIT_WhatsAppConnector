namespace BILLIT.WhatsAppConnector.Services.WhatsApp
{
    public class DisabledWhatsAppService : IWhatsAppService
    {
        public WhatsAppMode Mode => WhatsAppMode.Disabled;

        public event Func<WhatsAppStatus, Task>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<WhatsAppStatus> GetStatusAsync()
        {
            return Task.FromResult(new WhatsAppStatus(
                Mode: WhatsAppMode.Disabled,
                State: "Disabled",
                ConnectedNumber: null,
                QrCodeBase64: null,
                ErrorMessage: null
            ));
        }

        public Task StartAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<(bool Success, string? MessageId, string? Error)> SendTextAsync(long messageLogId, string to, string message)
        {
            return Task.FromResult<(bool, string?, string?)>((false, null, "WhatsApp messaging is disabled on this device."));
        }

        public Task<(bool Success, string? MessageId, string? Error)> SendDocumentAsync(long messageLogId, string to, string caption, byte[] fileBytes, string fileName)
        {
            return Task.FromResult<(bool, string?, string?)>((false, null, "WhatsApp messaging is disabled on this device."));
        }
    }
}
