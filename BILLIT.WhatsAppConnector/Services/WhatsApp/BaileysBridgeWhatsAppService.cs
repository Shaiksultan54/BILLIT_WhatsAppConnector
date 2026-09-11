using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace BILLIT.WhatsAppConnector.Services.WhatsApp
{
    public class BaileysBridgeWhatsAppService : IWhatsAppService, IDisposable
    {
        private readonly ILogger<BaileysBridgeWhatsAppService> _logger;
        private readonly string _sessionDir;
        private readonly string _bridgeExecutablePath;
        private Process? _bridgeProcess;
        private readonly SemaphoreSlim _processLock = new(1, 1);

        private readonly ConcurrentDictionary<long, TaskCompletionSource<(bool Success, string? MessageId, string? Error)>> _pendingSends = new();

        private const int MaxAutoRestartsInWindow = 3;
        private static readonly TimeSpan AutoRestartWindow = TimeSpan.FromMinutes(10);
        private readonly Queue<DateTime> _autoRestartTimestamps = new();

        public WhatsAppMode Mode => WhatsAppMode.LocalBridge;
        public string State { get; private set; } = "Disconnected";
        public string? ConnectedNumber { get; private set; }
        public string? QrCodeBase64 { get; private set; }
        public string? ErrorMessage { get; private set; }

        public event Func<WhatsAppStatus, Task>? StatusChanged;

        public BaileysBridgeWhatsAppService(ILogger<BaileysBridgeWhatsAppService> logger, IConfiguration config)
        {
            _logger = logger;
            _sessionDir = AppPaths.SessionDirectory;
            _bridgeExecutablePath = AppPaths.ResolveBridgeScriptPath(config["Connector:BridgeScriptPath"]);
        }

        public Task<WhatsAppStatus> GetStatusAsync()
        {
            return Task.FromResult(new WhatsAppStatus(
                Mode: WhatsAppMode.LocalBridge,
                State: State,
                ConnectedNumber: ConnectedNumber,
                QrCodeBase64: QrCodeBase64,
                ErrorMessage: ErrorMessage
            ));
        }

        private bool HasExistingSessionFiles()
        {
            if (!Directory.Exists(_sessionDir)) return false;
            return File.Exists(Path.Combine(_sessionDir, "creds.json"));
        }

        public async Task StartAsync()
        {
            await _processLock.WaitAsync();
            try
            {
                _autoRestartTimestamps.Clear();
                ErrorMessage = null;

                if (_bridgeProcess is { HasExited: false })
                {
                    _logger.LogInformation("Baileys bridge process is already running.");
                    return;
                }

                if (!File.Exists(_bridgeExecutablePath))
                {
                    ErrorMessage = $"Bridge script not found at '{_bridgeExecutablePath}'.";
                    State = "Error";
                    await NotifyStatusChangedAsync();
                    return;
                }

                var nodeExecutable = AppPaths.ResolveNodeExecutablePath();
                _logger.LogInformation("Launching Baileys bridge using Node: '{Node}'", nodeExecutable);

                _bridgeProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = nodeExecutable,
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
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogWarning("[bridge] {Line}", e.Data);
                    }
                };

                _bridgeProcess.Exited += async (_, _) =>
                {
                    _logger.LogWarning("Baileys bridge process exited.");
                    State = "Disconnected";
                    ConnectedNumber = null;
                    QrCodeBase64 = null;
                    await NotifyStatusChangedAsync();
                };

                try
                {
                    _bridgeProcess.Start();
                    _bridgeProcess.BeginOutputReadLine();
                    _bridgeProcess.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to launch Node.js process for Baileys bridge. Is Node.js installed in PATH?");
                    ErrorMessage = "Node.js executable could not be started. Ensure Node.js is installed on this PC.";
                    State = "Error";
                    await NotifyStatusChangedAsync();
                    return;
                }

                if (HasExistingSessionFiles())
                {
                    _logger.LogInformation("Found existing session creds — bridge will attempt auto-resume.");
                }

                await SendToBridgeAsync(new { cmd = "start" });
            }
            finally
            {
                _processLock.Release();
            }
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
                        try
                        {
                            _bridgeProcess.Kill(entireProcessTree: true);
                            _bridgeProcess.WaitForExit(2000);
                        }
                        catch { }
                    }
                }

                State = "Disconnected";
                ConnectedNumber = null;
                QrCodeBase64 = null;
                ErrorMessage = null;
                await NotifyStatusChangedAsync();
            }
            finally
            {
                _processLock.Release();
            }
        }

        public async Task<(bool Success, string? MessageId, string? Error)> SendTextAsync(long messageLogId, string to, string message)
        {
            if (State != "Connected" || _bridgeProcess is not { HasExited: false })
            {
                return (false, null, $"WhatsApp is not connected (current state: {State}).");
            }

            var tcs = new TaskCompletionSource<(bool, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSends[messageLogId] = tcs;

            try
            {
                await SendToBridgeAsync(new { cmd = "send-text", messageLogId, to, message });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                cts.Token.Register(() => tcs.TrySetResult((false, null, "Timed out waiting for WhatsApp bridge response.")));

                return await tcs.Task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send text message {LogId}", messageLogId);
                return (false, null, ex.Message);
            }
            finally
            {
                _pendingSends.TryRemove(messageLogId, out _);
            }
        }

        public async Task<(bool Success, string? MessageId, string? Error)> SendDocumentAsync(long messageLogId, string to, string caption, byte[] fileBytes, string fileName)
        {
            if (State != "Connected" || _bridgeProcess is not { HasExited: false })
            {
                return (false, null, $"WhatsApp is not connected (current state: {State}).");
            }

            var tcs = new TaskCompletionSource<(bool, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSends[messageLogId] = tcs;

            try
            {
                await SendToBridgeAsync(new
                {
                    cmd = "send-document",
                    messageLogId,
                    to,
                    caption,
                    fileName,
                    fileBase64 = Convert.ToBase64String(fileBytes)
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                cts.Token.Register(() => tcs.TrySetResult((false, null, "Timed out waiting for document transmission.")));

                return await tcs.Task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send document {LogId}", messageLogId);
                return (false, null, ex.Message);
            }
            finally
            {
                _pendingSends.TryRemove(messageLogId, out _);
            }
        }

        private async Task SendToBridgeAsync(object payload)
        {
            if (_bridgeProcess is not { HasExited: false })
            {
                throw new InvalidOperationException("Bridge process is not running.");
            }

            var json = JsonSerializer.Serialize(payload);
            await _bridgeProcess.StandardInput.WriteLineAsync(json);
            await _bridgeProcess.StandardInput.FlushAsync();
        }

        private async Task OnBridgeMessageAsync(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("event", out var evtProp)) return;
                var evt = evtProp.GetString();

                switch (evt)
                {
                    case "qr":
                        State = "PendingQr";
                        QrCodeBase64 = doc.RootElement.GetProperty("qrBase64").GetString();
                        ErrorMessage = null;
                        await NotifyStatusChangedAsync();
                        break;

                    case "connected":
                        State = "Connected";
                        ConnectedNumber = doc.RootElement.GetProperty("number").GetString();
                        QrCodeBase64 = null;
                        ErrorMessage = null;
                        _autoRestartTimestamps.Clear();
                        await NotifyStatusChangedAsync();
                        break;

                    case "disconnected":
                        State = "Disconnected";
                        var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
                        ErrorMessage = reason;
                        await NotifyStatusChangedAsync();
                        break;

                    case "expired":
                        State = "Expired";
                        QrCodeBase64 = null;
                        ConnectedNumber = null;
                        ClearExpiredSessionFiles();
                        await NotifyStatusChangedAsync();
                        HandleAutoRestart();
                        break;

                    case "send-result":
                        if (doc.RootElement.TryGetProperty("messageLogId", out var idProp))
                        {
                            var id = idProp.GetInt64();
                            var success = doc.RootElement.GetProperty("success").GetBoolean();
                            var messageId = doc.RootElement.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
                            var error = doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : null;

                            if (_pendingSends.TryGetValue(id, out var tcs))
                            {
                                tcs.TrySetResult((success, messageId, error));
                            }
                        }
                        break;

                    case "error":
                        var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                        _logger.LogWarning("Bridge reported error: {Message}", msg);
                        ErrorMessage = msg;
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse bridge output: {Line}", line);
            }
        }

        private void ClearExpiredSessionFiles()
        {
            try
            {
                if (Directory.Exists(_sessionDir))
                {
                    _logger.LogInformation("Clearing expired Baileys session files from '{Dir}'...", _sessionDir);
                    foreach (var file in Directory.GetFiles(_sessionDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clear expired session files");
            }
        }

        private void HandleAutoRestart()
        {
            while (_autoRestartTimestamps.Count > 0 && DateTime.UtcNow - _autoRestartTimestamps.Peek() > AutoRestartWindow)
            {
                _autoRestartTimestamps.Dequeue();
            }

            if (_autoRestartTimestamps.Count >= MaxAutoRestartsInWindow)
            {
                _logger.LogWarning("Session expired {Count} times in last {Window} mins. Auto-restart stopped.",
                    _autoRestartTimestamps.Count, AutoRestartWindow.TotalMinutes);
                ErrorMessage = "Session expired repeatedly. Please click Reconnect from settings.";
                State = "Disconnected";
                _ = NotifyStatusChangedAsync();
            }
            else
            {
                _autoRestartTimestamps.Enqueue(DateTime.UtcNow);
                _logger.LogInformation("Session expired. Auto-restart attempt {Attempt}/{Max}.", _autoRestartTimestamps.Count, MaxAutoRestartsInWindow);
                _ = Task.Run(async () =>
                {
                    await DisconnectAsync();
                    await StartAsync();
                });
            }
        }

        private async Task NotifyStatusChangedAsync()
        {
            if (StatusChanged != null)
            {
                try
                {
                    var status = await GetStatusAsync();
                    await StatusChanged.Invoke(status);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_bridgeProcess is { HasExited: false })
                {
                    _bridgeProcess.Kill(entireProcessTree: true);
                    _bridgeProcess.Dispose();
                }
            }
            catch { }
            _processLock.Dispose();
        }
    }
}
