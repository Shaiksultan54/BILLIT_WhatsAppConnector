using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BILLIT.WhatsAppConnector.Services.WhatsApp
{
    public class CloudApiWhatsAppService : IWhatsAppService
    {
        private readonly ILogger<CloudApiWhatsAppService> _logger;
        private readonly HttpClient _httpClient;
        private readonly ConnectorConfig _config;

        public WhatsAppMode Mode => WhatsAppMode.CloudApi;
        public string State { get; private set; } = "Disconnected";
        public string? ConnectedNumber { get; private set; }
        public string? ErrorMessage { get; private set; }

        public event Func<WhatsAppStatus, Task>? StatusChanged;

        public CloudApiWhatsAppService(ILogger<CloudApiWhatsAppService> logger, ConnectorConfig config)
        {
            _logger = logger;
            _config = config;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://graph.facebook.com/v19.0/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public Task<WhatsAppStatus> GetStatusAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.CloudApiPhoneNumberId) ||
                string.IsNullOrWhiteSpace(_config.CloudApiAccessToken))
            {
                State = "Disconnected";
                ErrorMessage = "Meta WhatsApp Cloud API credentials (Phone Number ID / Access Token) are not set.";
                return Task.FromResult(new WhatsAppStatus(Mode, State, null, null, ErrorMessage));
            }

            return Task.FromResult(new WhatsAppStatus(Mode, State, ConnectedNumber, null, ErrorMessage));
        }

        public async Task StartAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.CloudApiPhoneNumberId) ||
                string.IsNullOrWhiteSpace(_config.CloudApiAccessToken))
            {
                State = "Disconnected";
                ErrorMessage = "Missing Phone Number ID or Access Token.";
                await NotifyStatusChangedAsync();
                return;
            }

            try
            {
                // Verify credentials with Meta Graph API
                using var request = new HttpRequestMessage(HttpMethod.Get, _config.CloudApiPhoneNumberId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CloudApiAccessToken);

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(content);
                    ConnectedNumber = doc.RootElement.TryGetProperty("display_phone_number", out var p) ? p.GetString() : _config.CloudApiPhoneNumberId;
                    State = "Connected";
                    ErrorMessage = null;
                    _logger.LogInformation("Meta WhatsApp Cloud API verified successfully for number: {Number}", ConnectedNumber);
                }
                else
                {
                    State = "Error";
                    ErrorMessage = $"Meta API verification failed: {response.StatusCode} - {content}";
                    _logger.LogWarning("Meta API verification failed: {Content}", content);
                }
            }
            catch (Exception ex)
            {
                State = "Error";
                ErrorMessage = ex.Message;
                _logger.LogError(ex, "Failed to connect to Meta Cloud API");
            }

            await NotifyStatusChangedAsync();
        }

        public Task DisconnectAsync()
        {
            State = "Disconnected";
            ConnectedNumber = null;
            ErrorMessage = null;
            return NotifyStatusChangedAsync();
        }

        public async Task<(bool Success, string? MessageId, string? Error)> SendTextAsync(long messageLogId, string to, string message)
        {
            if (State != "Connected" || string.IsNullOrWhiteSpace(_config.CloudApiPhoneNumberId))
            {
                return (false, null, "Meta WhatsApp Cloud API is not connected or configured.");
            }

            var cleanTo = to.Replace("+", "").Replace(" ", "").Replace("-", "");

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = cleanTo,
                type = "text",
                text = new { preview_url = false, body = message }
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.CloudApiPhoneNumberId}/messages")
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CloudApiAccessToken);

                var response = await _httpClient.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseJson);
                    string? msgId = null;
                    if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
                    {
                        msgId = msgs[0].GetProperty("id").GetString();
                    }
                    return (true, msgId, null);
                }

                _logger.LogWarning("Cloud API send text failed: {Json}", responseJson);
                return (false, null, responseJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception sending Cloud API text message");
                return (false, null, ex.Message);
            }
        }

        public async Task<(bool Success, string? MessageId, string? Error)> SendDocumentAsync(long messageLogId, string to, string caption, byte[] fileBytes, string fileName)
        {
            if (State != "Connected" || string.IsNullOrWhiteSpace(_config.CloudApiPhoneNumberId))
            {
                return (false, null, "Meta WhatsApp Cloud API is not connected or configured.");
            }

            var cleanTo = to.Replace("+", "").Replace(" ", "").Replace("-", "");

            try
            {
                // Step 1: Upload media to Meta
                using var uploadContent = new MultipartFormDataContent();
                uploadContent.Add(new StringContent("whatsapp"), "messaging_product");
                uploadContent.Add(new ByteArrayContent(fileBytes), "file", fileName);
                uploadContent.Add(new StringContent("application/pdf"), "type");

                using var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"{_config.CloudApiPhoneNumberId}/media")
                {
                    Content = uploadContent
                };
                uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CloudApiAccessToken);

                var uploadResp = await _httpClient.SendAsync(uploadReq);
                var uploadJson = await uploadResp.Content.ReadAsStringAsync();

                if (!uploadResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Meta media upload failed: {Json}", uploadJson);
                    return (false, null, $"Media upload failed: {uploadJson}");
                }

                using var doc = JsonDocument.Parse(uploadJson);
                var mediaId = doc.RootElement.GetProperty("id").GetString();

                // Step 2: Send message with uploaded media ID
                var sendPayload = new
                {
                    messaging_product = "whatsapp",
                    recipient_type = "individual",
                    to = cleanTo,
                    type = "document",
                    document = new
                    {
                        id = mediaId,
                        caption = caption,
                        filename = fileName
                    }
                };

                using var sendReq = new HttpRequestMessage(HttpMethod.Post, $"{_config.CloudApiPhoneNumberId}/messages")
                {
                    Content = JsonContent.Create(sendPayload)
                };
                sendReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CloudApiAccessToken);

                var sendResp = await _httpClient.SendAsync(sendReq);
                var sendJson = await sendResp.Content.ReadAsStringAsync();

                if (sendResp.IsSuccessStatusCode)
                {
                    using var sendDoc = JsonDocument.Parse(sendJson);
                    string? msgId = null;
                    if (sendDoc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
                    {
                        msgId = msgs[0].GetProperty("id").GetString();
                    }
                    return (true, msgId, null);
                }

                return (false, null, sendJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception sending Cloud API document");
                return (false, null, ex.Message);
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
    }
}
