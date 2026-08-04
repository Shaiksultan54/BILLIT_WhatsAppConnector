using Microsoft.AspNetCore.SignalR.Client;

namespace BILLIT.WhatsAppConnector
{
    /// <summary>
    /// Owns the outbound SignalR connection to the cloud Hub for the
    /// lifetime of the Windows Service. Responsibilities:
    ///   1. Build the HubConnection using the token from connector.config.json
    ///      (sent as ?access_token= on the handshake — standard SignalR+JWT pattern).
    ///   2. Register handlers for the commands the server can push:
    ///      StartSession, Disconnect, SendText, SendDocument,
    ///      PrintDocument, ListPrinters (the last two are the printing addition).
    ///   3. Ensure the WhatsApp bridge process is running (independent of the
    ///      hub connection's own up/down state — a dropped cloud link doesn't
    ///      need to tear down the local WhatsApp session).
    ///   4. Automatically reconnect (SignalR's built-in policy) and re-affirm
    ///      current status once reconnected, and send a light heartbeat.
    /// </summary>
    public class ConnectorWorker : BackgroundService
    {
        private readonly SessionManager _sessionManager;
        private readonly PrinterManager _printerManager; // PRINTING ADDITIONS
        private readonly ConnectorConfig _config;
        private readonly ILogger<ConnectorWorker> _logger;
        private HubConnection? _connection;

        public ConnectorWorker(
            SessionManager sessionManager,
            PrinterManager printerManager, // PRINTING ADDITIONS
            ConnectorConfig config,
            ILogger<ConnectorWorker> logger)
        {
            _sessionManager = sessionManager;
            _printerManager = printerManager; // PRINTING ADDITIONS
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _connection = new HubConnectionBuilder()
                .WithUrl(_config.HubUrl, options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(_config.ConnectorToken);
                    // Bypass SSL certificate validation for local development
                    // (the API uses a self-signed dev certificate on localhost)
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler clientHandler)
                        {
                            clientHandler.ServerCertificateCustomValidationCallback =
                                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                        }
                        return handler;
                    };
                })
                .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1) })
                .Build();

            _sessionManager.Connection = _connection;

            // ---- Server -> connector commands ----
            _connection.On("StartSession", async () =>
            {
                try
                {
                    _logger.LogInformation("Received StartSession");
                    await _sessionManager.StartSessionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in StartSession handler");
                }
            });

            _connection.On("Disconnect", async () =>
            {
                try
                {
                    _logger.LogInformation("Received Disconnect");
                    await _sessionManager.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Disconnect handler");
                }
            });

            _connection.On<SendTextPayload>("SendText", async payload =>
            {
                try
                {
                    await _sessionManager.SendTextAsync(payload.MessageLogId, payload.To, payload.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SendText handler for log ID {MessageLogId}", payload.MessageLogId);
                    if (_connection.State == HubConnectionState.Connected)
                    {
                        try { await _connection.InvokeAsync("ReportSendResult", payload.MessageLogId, false, (string?)null, "Connector exception: " + ex.Message); } catch { }
                    }
                }
            });

            _connection.On<SendDocumentPayload>("SendDocument", async payload =>
            {
                try
                {
                    var bytes = Convert.FromBase64String(payload.FileBase64);
                    await _sessionManager.SendDocumentAsync(payload.MessageLogId, payload.To, payload.Caption, bytes, payload.FileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SendDocument handler for log ID {MessageLogId}", payload.MessageLogId);
                    if (_connection.State == HubConnectionState.Connected)
                    {
                        try { await _connection.InvokeAsync("ReportSendResult", payload.MessageLogId, false, (string?)null, "Connector exception: " + ex.Message); } catch { }
                    }
                }
            });

            // ---- PRINTING ADDITIONS ----
            _connection.On<PrintDocumentPayload>("PrintDocument", async payload =>
            {
                bool success = false;
                string? error = null;
                try
                {
                    var bytes = Convert.FromBase64String(payload.FileBase64);
                    (success, error) = await _printerManager.PrintPdfAsync(bytes, payload.PrinterName, payload.Copies <= 0 ? 1 : payload.Copies);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in PrintDocument handler for job {PrintJobLogId}", payload.PrintJobLogId);
                    error = "Connector exception: " + ex.Message;
                }

                if (_connection.State == HubConnectionState.Connected)
                {
                    try { await _connection.InvokeAsync("ReportPrintResult", payload.PrintJobLogId, success, error); } catch { }
                }
            });

            _connection.On("ListPrinters", async () =>
            {
                try { await ReportPrintersAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Error reporting printers"); }
            });
            // ---- END PRINTING ADDITIONS ----

            _connection.Reconnecting += ex =>
            {
                _logger.LogWarning(ex, "Hub connection lost, reconnecting...");
                return Task.CompletedTask;
            };

            _connection.Reconnected += async _ =>
            {
                try
                {
                    _logger.LogInformation("Hub connection restored");
                    await _sessionManager.ResendCurrentStatusAsync();
                    await ReportPrintersAsync(); // PRINTING ADDITIONS
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Reconnected handler");
                }
            };

            _connection.Closed += ex =>
            {
                _logger.LogWarning(ex, "Hub connection closed");
                // Do not await StartAsync or block in the Closed event.
                // Reconnection will be handled by the main loop.
                return Task.CompletedTask;
            };

            // The local WhatsApp session runs independently of the cloud link —
            // start (or resume, if auth was already persisted) it right away.
            try
            {
                await _sessionManager.EnsureRunningAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start WhatsApp session. Printing will still be available.");
            }

            await ConnectWithRetryAsync(stoppingToken);
            await ReportPrintersAsync(); // PRINTING ADDITIONS — let the dashboard show the printer list right after first connect

            // Light heartbeat while the service is alive.
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                
                if (_connection.State == HubConnectionState.Disconnected)
                {
                    _logger.LogInformation("Connection is disconnected, attempting to reconnect...");
                    await ConnectWithRetryAsync(stoppingToken);
                    await ReportPrintersAsync(); // PRINTING ADDITIONS
                }
                else if (_connection.State == HubConnectionState.Connected)
                {
                    try { await _connection.InvokeAsync("Heartbeat", stoppingToken); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Heartbeat failed"); }
                }
            }
        }

        // PRINTING ADDITIONS
        private async Task ReportPrintersAsync()
        {
            if (_connection is not { State: HubConnectionState.Connected }) return;

            var printers = _printerManager.GetInstalledPrinters().ToList();
            var defaultPrinter = _printerManager.GetDefaultPrinterName();

            try { await _connection.InvokeAsync("ReportPrinters", defaultPrinter, printers); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to report printers"); }
        }

        private async Task ConnectWithRetryAsync(CancellationToken ct)
        {
            int retryCount = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _connection!.StartAsync(ct);
                    _logger.LogInformation("Connected to BILLIT hub at {Url}", _config.HubUrl);
                    return;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("TokenRevoked", StringComparison.OrdinalIgnoreCase) ||
                        (ex.InnerException?.Message.Contains("TokenRevoked", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        _logger.LogError("The hub rejected our token because it is stale or revoked. Please re-pair the connector. Halting connection attempts for 1 hour.");
                        try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { }
                        continue;
                    }

                    retryCount++;
                    var backoffSeconds = Math.Min(300, (int)Math.Pow(2, Math.Min(retryCount, 8))); // Max ~5 mins
                    
                    _logger.LogError(ex, "Failed to connect to hub — retrying in {BackoffSeconds}s", backoffSeconds);
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct); } catch { }
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_connection != null) await _connection.DisposeAsync();
            await base.StopAsync(cancellationToken);
        }

        private record SendTextPayload(long MessageLogId, string To, string Message);
        private record SendDocumentPayload(long MessageLogId, string To, string Caption, string FileBase64, string FileName);
        private record PrintDocumentPayload(long PrintJobLogId, string FileName, string? PrinterName, int Copies, string FileBase64); // PRINTING ADDITIONS
    }
}
