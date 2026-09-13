namespace BILLIT.WhatsAppConnector.Services.WhatsApp
{
    public class WhatsAppCoordinator : IWhatsAppService, IDisposable
    {
        private readonly ILogger<WhatsAppCoordinator> _logger;
        private readonly ConnectorConfig _config;
        private readonly IServiceProvider _serviceProvider;

        private IWhatsAppService _currentService;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public WhatsAppMode Mode => _currentService.Mode;

        public event Func<WhatsAppStatus, Task>? StatusChanged;

        public WhatsAppCoordinator(
            ILogger<WhatsAppCoordinator> logger,
            ConnectorConfig config,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _config = config;
            _serviceProvider = serviceProvider;

            _currentService = ResolveService(_config.Mode);
            AttachEvents(_currentService);
        }

        private IWhatsAppService ResolveService(WhatsAppMode mode)
        {
            return mode switch
            {
                WhatsAppMode.LocalBridge => _serviceProvider.GetRequiredService<BaileysBridgeWhatsAppService>(),
                WhatsAppMode.CloudApi => _serviceProvider.GetRequiredService<CloudApiWhatsAppService>(),
                _ => _serviceProvider.GetRequiredService<DisabledWhatsAppService>()
            };
        }

        private void AttachEvents(IWhatsAppService service)
        {
            service.StatusChanged += OnSubServiceStatusChanged;
        }

        private void DetachEvents(IWhatsAppService service)
        {
            service.StatusChanged -= OnSubServiceStatusChanged;
        }

        private async Task OnSubServiceStatusChanged(WhatsAppStatus status)
        {
            if (StatusChanged != null)
            {
                await StatusChanged.Invoke(status);
            }
        }

        public async Task SwitchModeAsync(WhatsAppMode newMode)
        {
            await _lock.WaitAsync();
            try
            {
                if (_currentService.Mode == newMode) return;

                _logger.LogInformation("Switching WhatsApp mode from {Old} to {New}", _currentService.Mode, newMode);

                DetachEvents(_currentService);
                await _currentService.DisconnectAsync();

                _config.Mode = newMode;
                _config.Save();

                _currentService = ResolveService(newMode);
                AttachEvents(_currentService);

                if (newMode != WhatsAppMode.Disabled)
                {
                    await _currentService.StartAsync();
                }
                else
                {
                    await OnSubServiceStatusChanged(await _currentService.GetStatusAsync());
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task<WhatsAppStatus> GetStatusAsync() => _currentService.GetStatusAsync();

        public async Task StartAsync()
        {
            if (_currentService.Mode == WhatsAppMode.Disabled)
            {
                // Auto-activate to LocalBridge (or CloudApi if configured)
                var targetMode = !string.IsNullOrWhiteSpace(_config.CloudApiAccessToken) && !string.IsNullOrWhiteSpace(_config.CloudApiPhoneNumberId)
                    ? WhatsAppMode.CloudApi
                    : WhatsAppMode.LocalBridge;

                _logger.LogInformation("Activating WhatsApp service from dead state to {Mode}...", targetMode);
                await SwitchModeAsync(targetMode);
                return;
            }

            await _currentService.StartAsync();
        }

        public async Task DisconnectAsync()
        {
            await _currentService.DisconnectAsync();
            if (_currentService.Mode != WhatsAppMode.Disabled)
            {
                _logger.LogInformation("WhatsApp service deactivated. Returning to dead state (Disabled)...");
                await SwitchModeAsync(WhatsAppMode.Disabled);
            }
        }

        public Task<(bool Success, string? MessageId, string? Error)> SendTextAsync(long messageLogId, string to, string message) =>
            _currentService.SendTextAsync(messageLogId, to, message);

        public Task<(bool Success, string? MessageId, string? Error)> SendDocumentAsync(long messageLogId, string to, string caption, byte[] fileBytes, string fileName) =>
            _currentService.SendDocumentAsync(messageLogId, to, caption, fileBytes, fileName);

        public void Dispose()
        {
            DetachEvents(_currentService);
            if (_currentService is IDisposable d)
            {
                d.Dispose();
            }
            _lock.Dispose();
        }
    }
}
