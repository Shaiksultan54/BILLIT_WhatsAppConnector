using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace BILLIT.WhatsAppConnector
{
    /// <summary>
    /// Owns the one WhatsApp session this connector process is responsible
    /// for (one process = one shop, since the connector's JWT is scoped to a
    /// single shop). Spawns the Node.js session-bridge process, and reports
    /// every state change / send result back through the SignalR connection
    /// that ConnectorWorker owns and assigns here via the Connection property.
    ///
    /// The bridge's own contract (unchanged from the original design):
    ///   stdin  -> {"cmd":"start"} | {"cmd":"send-text","messageLogId":1,"to":"+91...","message":"..."}
    ///             | {"cmd":"send-document","messageLogId":1,"to":"+91...","caption":"...","fileBase64":"...","fileName":"..."}
    ///             | {"cmd":"disconnect"}
    ///   stdout <- {"event":"qr","qrBase64":"..."}
    ///             | {"event":"connected","number":"+91..."}
    ///             | {"event":"disconnected","reason":"..."}
    ///             | {"event":"expired"}
    ///             | {"event":"send-result","messageLogId":1,"success":true,"messageId":"..."}
    ///             | {"event":"error","message":"..."}
    /// </summary>
    public class SessionManager
    {
        private readonly ILogger<SessionManager> _logger;
        private readonly string _sessionDir;
        private readonly string _bridgeExecutablePath;
        private Process? _bridgeProcess;
        private readonly SemaphoreSlim _processLock = new SemaphoreSlim(1, 1);

        // Runtime cache of last known state so a hub reconnect can re-affirm
        // status without needing to ask the bridge again.
        public string LastStatus { get; private set; } = "Disconnected";
        public string? LastConnectedNumber { get; private set; }
        public string? LastQrCodeBase64 { get; private set; }

        /// <summary>Set by ConnectorWorker once the hub connection exists.</summary>
        public HubConnection? Connection { get; set; }

        public SessionManager(ILogger<SessionManager> logger, IConfiguration config)
        {
            _logger = logger;
            _sessionDir = config["Connector:SessionDir"]
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BILLIT", "WhatsAppConnector", "session");
            _bridgeExecutablePath = config["Connector:BridgeScriptPath"]
                ?? Path.Combine(AppContext.BaseDirectory, "bridge", "billit-wa-bridge.js");

            Directory.CreateDirectory(_sessionDir);
        }

        /// <summary>Ensures the bridge process is running. Safe to call repeatedly.</summary>
        public async Task EnsureRunningAsync()
        {
            await _processLock.WaitAsync();
            try
            {
                if (_bridgeProcess is { HasExited: false }) return;

                _bridgeProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = $"\"{_bridgeExecutablePath}\" --session-dir=\"{_sessionDir}\"",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                _bridgeProcess.OutputDataReceived += (_, e) => _ = OnBridgeMessageAsync(e.Data);
                _bridgeProcess.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) _logger.LogWarning("[bridge] {Line}", e.Data);
                };

                _bridgeProcess.Exited += async (_, _) =>
                {
                    _logger.LogWarning("Bridge process exited unexpectedly.");
                    LastStatus = "Disconnected";
                    if (Connection is { State: HubConnectionState.Connected })
                    {
                        try { await Connection.InvokeAsync("ReportDisconnected", "Bridge process crashed"); } catch { }
                    }
                };

                _bridgeProcess.Start();
                _bridgeProcess.BeginOutputReadLine();
                _bridgeProcess.BeginErrorReadLine();

                await SendToBridgeAsync(new { cmd = "start" });
            }
            finally
            {
                _processLock.Release();
            }
        }

        public async Task StartSessionAsync()
        {
            if (LastStatus == "Expired" || LastStatus == "Disconnected")
            {
                _logger.LogInformation("Cleaning up old session state before starting");
                await DisconnectAsync();

                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            if (Directory.Exists(_sessionDir))
                            {
                                Directory.Delete(_sessionDir, true);
                            }
                            break;
                        }
                        catch (IOException)
                        {
                            if (i == 2) throw;
                            await Task.Delay(1000);
                        }
                    }
                    Directory.CreateDirectory(_sessionDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear session directory: {Message}", ex.Message);
                }
            }
            await EnsureRunningAsync();
        }

        public async Task DisconnectAsync()
        {
            await _processLock.WaitAsync();
            try
            {
                if (_bridgeProcess is { HasExited: false })
                {
                    try { await SendToBridgeAsync(new { cmd = "disconnect" }); } catch { }

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await _bridgeProcess.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Bridge process did not exit within 5 seconds, killing tree.");
                        try
                        {
                            _bridgeProcess.Kill(entireProcessTree: true);
                            _bridgeProcess.WaitForExit(2000);
                        }
                        catch { }
                    }
                }
                LastStatus = "Disconnected";
                LastConnectedNumber = null;
                LastQrCodeBase64 = null;
            }
            finally
            {
                _processLock.Release();
            }
        }

        public Task SendTextAsync(long messageLogId, string to, string message) =>
            SendToBridgeAsync(new { cmd = "send-text", messageLogId, to, message });

        public Task SendDocumentAsync(long messageLogId, string to, string caption, byte[] pdfBytes, string fileName) =>
            SendToBridgeAsync(new { cmd = "send-document", messageLogId, to, caption, fileName, fileBase64 = Convert.ToBase64String(pdfBytes) });

        /// <summary>Re-pushes whatever we last knew, useful right after the hub reconnects.</summary>
        public async Task ResendCurrentStatusAsync()
        {
            if (Connection is not { State: Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected }) return;

            switch (LastStatus)
            {
                case "PendingQr":
                    await Connection.InvokeAsync("ReportQr", LastQrCodeBase64);
                    break;
                case "Connected":
                    await Connection.InvokeAsync("ReportConnected", LastConnectedNumber);
                    break;
                case "Expired":
                    await Connection.InvokeAsync("ReportExpired");
                    break;
                default:
                    await Connection.InvokeAsync("ReportDisconnected", (string?)null);
                    break;
            }
        }

        private async Task SendToBridgeAsync(object payload)
        {
            if (_bridgeProcess is not { HasExited: false })
            {
                _logger.LogWarning("Bridge process is not running — cannot send command {Cmd}", payload);
                throw new InvalidOperationException("Bridge process is not running.");
            }
            try
            {
                await _bridgeProcess.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload));
                await _bridgeProcess.StandardInput.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write to bridge standard input");
                throw new InvalidOperationException("Failed to write to bridge standard input", ex);
            }
        }

        private async Task OnBridgeMessageAsync(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var evt = doc.RootElement.GetProperty("event").GetString();
                var conn = Connection;

                _logger.LogInformation("Bridge event: {Event}", evt);

                switch (evt)
                {
                    case "qr":
                        LastStatus = "PendingQr";
                        LastQrCodeBase64 = doc.RootElement.GetProperty("qrBase64").GetString();
                        if (conn != null) await conn.InvokeAsync("ReportQr", LastQrCodeBase64);
                        break;

                    case "connected":
                        LastStatus = "Connected";
                        LastConnectedNumber = doc.RootElement.GetProperty("number").GetString();
                        LastQrCodeBase64 = null;
                        if (conn != null) await conn.InvokeAsync("ReportConnected", LastConnectedNumber);
                        break;

                    case "disconnected":
                        LastStatus = "Disconnected";
                        var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
                        if (conn != null) await conn.InvokeAsync("ReportDisconnected", reason);
                        break;

                    case "expired":
                        LastStatus = "Expired";
                        if (conn != null) await conn.InvokeAsync("ReportExpired");

                        _logger.LogInformation("Session expired. Automatically restarting to generate a new QR code.");
                        _ = StartSessionAsync();
                        break;

                    case "send-result":
                        var messageLogId = doc.RootElement.GetProperty("messageLogId").GetInt64();
                        var success = doc.RootElement.GetProperty("success").GetBoolean();
                        var messageId = doc.RootElement.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
                        var error = doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : null;
                        if (conn != null) await conn.InvokeAsync("ReportSendResult", messageLogId, success, messageId, error);
                        break;

                    case "error":
                        var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "Unknown bridge error";
                        _logger.LogWarning("Bridge reported error: {Message}", msg);
                        break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Malformed message from bridge: {Line}", line);
            }
        }

        // ---------------- Local encryption helpers (Windows DPAPI), used by the bridge's own auth-state persistence if it shells out to this for at-rest encryption ----------------

        public static void SaveEncrypted(string filePath, byte[] plainBytes)
        {
            var encrypted = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(filePath, encrypted);
        }

        public static byte[] LoadDecrypted(string filePath)
        {
            var encrypted = File.ReadAllBytes(filePath);
            return ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.LocalMachine);
        }
    }
}
