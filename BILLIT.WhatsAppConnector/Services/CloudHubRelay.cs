using Microsoft.AspNetCore.SignalR.Client;
using BILLIT.WhatsAppConnector.Services.WhatsApp;

namespace BILLIT.WhatsAppConnector.Services
{
    public class CloudHubRelay : BackgroundService
    {
        private readonly IPrinterService _printerService;
        private readonly IWhatsAppService _whatsAppService;
        private readonly ConnectorConfig _config;
        private readonly ILogger<CloudHubRelay> _logger;
        private HubConnection? _connection;

        public CloudHubRelay(
            IPrinterService printerService,
            IWhatsAppService whatsAppService,
            ConnectorConfig config,
            ILogger<CloudHubRelay> logger)
        {
            _printerService = printerService;
            _whatsAppService = whatsAppService;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Subscribe to WhatsApp events so any state changes are reported to the cloud hub
            _whatsAppService.StatusChanged += OnWhatsAppStatusChangedAsync;

            // Start WhatsApp service ONLY if configured to run and not Disabled
            if (_config.Mode != WhatsAppMode.Disabled)
            {
                try
                {
                    _logger.LogInformation("Starting WhatsApp provider ({Mode})...", _config.Mode);
                    await _whatsAppService.StartAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WhatsApp provider startup encountered an issue. Printing remains 100% operational.");
                }
            }
            else
            {
                _logger.LogInformation("WhatsApp service is set to Disabled. Printing is active and offline-ready.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_config.IsCloudHubConfigured())
                {
                    _logger.LogInformation("Cloud Hub is not configured. Running in Local Standalone Mode (Port {Port}).", _config.Port);
                    // Check every 30s in case config is updated at runtime via API
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                try
                {
                    await InitializeAndRunHubAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cloud Hub relay disconnected. Retrying in 15 seconds...");
                    try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch { }
                }
            }
        }

        private async Task InitializeAndRunHubAsync(CancellationToken ct)
        {
            if (_connection == null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(_config.HubUrl, options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(_config.ConnectorToken);
#if DEBUG
                        options.HttpMessageHandlerFactory = handler =>
                        {
                            if (handler is HttpClientHandler clientHandler)
                            {
                                clientHandler.ServerCertificateCustomValidationCallback =
                                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                            }
                            return handler;
                        };
#endif
                    })
                    .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1) })
                    .Build();

                RegisterHubHandlers(_connection);
            }

            await ConnectWithRetryAsync(ct);

            // 1. Report Printers (Guaranteed - Printing is priority 1)
            try
            {
                await ReportPrintersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial printer reporting failed; will retry when requested.");
            }

            // 2. Report WhatsApp status only if WhatsApp is enabled on this agent
            if (_config.Mode != WhatsAppMode.Disabled)
            {
                try
                {
                    await PushCurrentWhatsAppStatusAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Initial WhatsApp status push skipped or failed.");
                }
            }

            // 3. Keep-alive Heartbeat loop while connected
            while (!ct.IsCancellationRequested && _connection.State == HubConnectionState.Connected)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), ct);
                    if (_connection.State == HubConnectionState.Connected)
                    {
                        await _connection.InvokeAsync("Heartbeat", ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Heartbeat ping failed");
                }
            }

            // If disconnected, pause with backoff before reconnecting to prevent tight loops
            if (_connection.State != HubConnectionState.Connected && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("Cloud Hub disconnected. Backing off 30s before reconnecting...");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }

        private void RegisterHubHandlers(HubConnection conn)
        {
            // --- PRINTING COMMANDS (Priority 1) ---

            conn.On<PrintDocumentPayload>("PrintDocument", async payload =>
            {
                bool success = false;
                string? error = null;
                try
                {
                    _logger.LogInformation("Received remote PrintDocument request: Job {Id}, File '{File}', Printer '{Printer}', Paper '{Paper}'",
                        payload.PrintJobLogId, payload.FileName, payload.PrinterName ?? "Default", payload.PaperSize ?? "80mm");

                    var bytes = Convert.FromBase64String(payload.FileBase64);
                    (success, error) = await _printerService.PrintPdfAsync(
                        bytes,
                        payload.PrinterName,
                        payload.Copies <= 0 ? 1 : payload.Copies,
                        payload.PaperSize ?? "80mm");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Remote print execution failed for Job {Id}", payload.PrintJobLogId);
                    error = ex.Message;
                }

                if (conn.State == HubConnectionState.Connected)
                {
                    try { await conn.InvokeAsync("ReportPrintResult", payload.PrintJobLogId, success, error); } catch { }
                }
            });

            conn.On<PrintRawPayload>("PrintRaw", async payload =>
            {
                bool success = false;
                string? error = null;
                try
                {
                    _logger.LogInformation("Received remote PrintRaw request for Printer '{Printer}'", payload.PrinterName ?? "Default");
                    var bytes = Convert.FromBase64String(payload.DataBase64);
                    (success, error) = await _printerService.PrintRawAsync(bytes, payload.PrinterName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Remote raw print execution failed");
                    error = ex.Message;
                }

                if (payload.PrintJobLogId.HasValue && conn.State == HubConnectionState.Connected)
                {
                    try { await conn.InvokeAsync("ReportPrintResult", payload.PrintJobLogId.Value, success, error); } catch { }
                }
            });

            conn.On<PrinterActionPayload>("OpenCashDrawer", async payload =>
            {
                try
                {
                    _logger.LogInformation("Received remote OpenCashDrawer pulse request for Printer '{Printer}'", payload.PrinterName ?? "Default");
                    await _printerService.OpenCashDrawerAsync(payload.PrinterName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OpenCashDrawer failed");
                }
            });

            conn.On<PrinterActionPayload>("CutPaper", async payload =>
            {
                try
                {
                    _logger.LogInformation("Received remote CutPaper command for Printer '{Printer}'", payload.PrinterName ?? "Default");
                    await _printerService.CutPaperAsync(payload.PrinterName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CutPaper failed");
                }
            });

            conn.On("ListPrinters", async () =>
            {
                _logger.LogInformation("Received ListPrinters request from Cloud Hub.");
                await ReportPrintersAsync();
            });

            // --- WHATSAPP COMMANDS (User Option) ---

            conn.On("StartSession", async () =>
            {
                try
                {
                    _logger.LogInformation("Cloud Hub commanded StartSession. Activating WhatsApp service...");
                    await _whatsAppService.StartAsync();
                }
                catch (Exception ex) { _logger.LogError(ex, "Error starting WhatsApp session"); }
            });

            conn.On("EnableWhatsApp", async () =>
            {
                try
                {
                    _logger.LogInformation("Cloud Hub commanded EnableWhatsApp. Bringing WhatsApp alive...");
                    await _whatsAppService.StartAsync();
                }
                catch (Exception ex) { _logger.LogError(ex, "Error enabling WhatsApp"); }
            });

            conn.On("Disconnect", async () =>
            {
                try
                {
                    _logger.LogInformation("Cloud Hub commanded Disconnect. Returning WhatsApp to dead state...");
                    await _whatsAppService.DisconnectAsync();
                }
                catch (Exception ex) { _logger.LogError(ex, "Error disconnecting WhatsApp session"); }
            });

            conn.On("StopSession", async () =>
            {
                try
                {
                    _logger.LogInformation("Cloud Hub commanded StopSession. Returning WhatsApp to dead state...");
                    await _whatsAppService.DisconnectAsync();
                }
                catch (Exception ex) { _logger.LogError(ex, "Error stopping WhatsApp session"); }
            });

            conn.On("DisableWhatsApp", async () =>
            {
                try
                {
                    _logger.LogInformation("Cloud Hub commanded DisableWhatsApp. Returning WhatsApp to dead state...");
                    await _whatsAppService.DisconnectAsync();
                }
                catch (Exception ex) { _logger.LogError(ex, "Error disabling WhatsApp"); }
            });

            conn.On<SendTextPayload>("SendText", async payload =>
            {
                if (_config.Mode == WhatsAppMode.Disabled)
                {
                    _logger.LogWarning("Received SendText from cloud, but WhatsApp is Disabled on this connector station.");
                    if (conn.State == HubConnectionState.Connected)
                    {
                        try { await conn.InvokeAsync("ReportSendResult", payload.MessageLogId, false, (string?)null, "WhatsApp messaging is disabled on this local POS station."); } catch { }
                    }
                    return;
                }

                var (success, msgId, err) = await _whatsAppService.SendTextAsync(payload.MessageLogId, payload.To, payload.Message);
                if (conn.State == HubConnectionState.Connected)
                {
                    try { await conn.InvokeAsync("ReportSendResult", payload.MessageLogId, success, msgId, err); } catch { }
                }
            });

            conn.On<SendDocumentPayload>("SendDocument", async payload =>
            {
                if (_config.Mode == WhatsAppMode.Disabled)
                {
                    _logger.LogWarning("Received SendDocument from cloud, but WhatsApp is Disabled on this connector station.");
                    if (conn.State == HubConnectionState.Connected)
                    {
                        try { await conn.InvokeAsync("ReportSendResult", payload.MessageLogId, false, (string?)null, "WhatsApp messaging is disabled on this local POS station."); } catch { }
                    }
                    return;
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(payload.FileBase64);
                }
                catch (Exception ex)
                {
                    if (conn.State == HubConnectionState.Connected)
                    {
                        try { await conn.InvokeAsync("ReportSendResult", payload.MessageLogId, false, (string?)null, "Base64 decode error: " + ex.Message); } catch { }
                    }
                    return;
                }

                var (success, msgId, err) = await _whatsAppService.SendDocumentAsync(payload.MessageLogId, payload.To, payload.Caption, bytes, payload.FileName);
                if (conn.State == HubConnectionState.Connected)
                {
                    try { await conn.InvokeAsync("ReportSendResult", payload.MessageLogId, success, msgId, err); } catch { }
                }
            });

            conn.Reconnected += async _ =>
            {
                _logger.LogInformation("SignalR connection restored to cloud hub.");
                // Always restore printer reporting immediately
                await ReportPrintersAsync();
                if (_config.Mode != WhatsAppMode.Disabled)
                {
                    await PushCurrentWhatsAppStatusAsync();
                }
            };
        }

        private async Task<bool> ReportPrintersAsync()
        {
            if (_connection is not { State: HubConnectionState.Connected }) return false;

            var printers = _printerService.GetInstalledPrinters().ToList();
            var defaultPrinter = _printerService.GetDefaultPrinterName();

            try
            {
                await _connection.InvokeAsync("ReportPrinters", defaultPrinter, printers);
                _logger.LogInformation("Reported {Count} printers to cloud hub. Default: {Default}", printers.Count, defaultPrinter ?? "None");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to report printers to cloud hub: {Message}", ex.Message);
                return false;
            }
        }

        private async Task OnWhatsAppStatusChangedAsync(WhatsAppStatus status)
        {
            if (_connection is not { State: HubConnectionState.Connected }) return;

            // If WhatsApp is disabled, do NOT spam the server hub with disconnect reports
            if (_config.Mode == WhatsAppMode.Disabled) return;

            try
            {
                switch (status.State)
                {
                    case "PendingQr":
                        if (!string.IsNullOrEmpty(status.QrCodeBase64))
                            await _connection.InvokeAsync("ReportQr", status.QrCodeBase64);
                        break;
                    case "Connected":
                        if (!string.IsNullOrEmpty(status.ConnectedNumber))
                            await _connection.InvokeAsync("ReportConnected", status.ConnectedNumber);
                        break;
                    case "Expired":
                        await _connection.InvokeAsync("ReportExpired");
                        break;
                    case "Disconnected":
                    case "Error":
                        await _connection.InvokeAsync("ReportDisconnected", status.ErrorMessage ?? "WhatsApp disconnected");
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not push WhatsApp status to cloud hub");
            }
        }

        private async Task PushCurrentWhatsAppStatusAsync()
        {
            if (_config.Mode == WhatsAppMode.Disabled) return;

            var status = await _whatsAppService.GetStatusAsync();
            await OnWhatsAppStatusChangedAsync(status);
        }

        private async Task ConnectWithRetryAsync(CancellationToken ct)
        {
            int attempts = 0;
            while (!ct.IsCancellationRequested && _connection!.State != HubConnectionState.Connected)
            {
                try
                {
                    await _connection.StartAsync(ct);
                    _logger.LogInformation("✓ Connected to BILLIT cloud hub: {Url}", _config.HubUrl);
                    return;
                }
                catch (Exception ex)
                {
                    attempts++;
                    var delay = Math.Min(60, (int)Math.Pow(2, Math.Min(attempts, 6)));
                    _logger.LogWarning("Connecting to cloud hub failed ({Attempt}): {Msg}. Retrying in {Delay}s.", attempts, ex.Message, delay);
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _whatsAppService.StatusChanged -= OnWhatsAppStatusChangedAsync;
            if (_connection != null)
            {
                try { await _connection.DisposeAsync(); } catch { }
            }
            await base.StopAsync(cancellationToken);
        }

        private record SendTextPayload(long MessageLogId, string To, string Message);
        private record SendDocumentPayload(long MessageLogId, string To, string Caption, string FileBase64, string FileName);
        private record PrintDocumentPayload(long PrintJobLogId, string FileName, string? PrinterName, int Copies, string FileBase64, string? PaperSize = "80mm");
        private record PrintRawPayload(long? PrintJobLogId, string? PrinterName, string DataBase64);
        private record PrinterActionPayload(string? PrinterName);
    }
}
