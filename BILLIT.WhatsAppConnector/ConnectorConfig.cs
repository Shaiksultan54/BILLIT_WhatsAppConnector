using System.Net.Http.Json;
using System.Text.Json;

namespace BILLIT.WhatsAppConnector
{
    public class ConnectorConfig
    {
        public string HubUrl { get; set; } = string.Empty;
        public string ConnectorToken { get; set; } = string.Empty;

        private static readonly string ConfigFilePath = "connector.config.json";

        public static bool Exists()
        {
            return File.Exists(ConfigFilePath);
        }

        /// <summary>
        /// Interactive first-run setup. Authenticates with the shop's own
        /// credentials (the same ones used to log in to the BILLIT UI) and
        /// auto-generates the connector token + hub URL — the shop owner
        /// never sees or handles a raw JWT token.
        /// </summary>
        public static async Task RunInteractiveSetupAsync()
        {
            Console.WriteLine("╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║    BILLIT WhatsApp Connector — First-Time Setup  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");
            Console.WriteLine();

            Console.Write("  Enter your BILLIT API URL (e.g. https://yourdomain.com): ");
            var apiBaseUrl = Console.ReadLine()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                Console.WriteLine("  ERROR: API URL is required.");
                return;
            }
            apiBaseUrl = apiBaseUrl.TrimEnd('/');

            Console.Write("  Enter your Shop Code: ");
            var shopCode = Console.ReadLine()?.Trim() ?? string.Empty;

            Console.Write("  Enter your Username: ");
            var userName = Console.ReadLine()?.Trim() ?? string.Empty;

            Console.Write("  Enter your Password: ");
            var password = ReadPasswordMasked();

            if (string.IsNullOrWhiteSpace(shopCode) || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            {
                Console.WriteLine("  ERROR: All fields are required.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("  Connecting to BILLIT...");

            try
            {
                // In development, the API may use a self-signed certificate.
                // For production, a proper certificate should be used.
                using var handler = new HttpClientHandler();
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

                using var http = new HttpClient(handler) { BaseAddress = new Uri(apiBaseUrl) };

                var pairResponse = await http.PostAsJsonAsync("/api/auth/whatsapp-connector/pair", new
                {
                    shopCode,
                    userName,
                    password
                });

                var json = await pairResponse.Content.ReadAsStringAsync();

                if (!pairResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  ERROR: Server returned {(int)pairResponse.StatusCode}.");
                    // Try to extract the error message from the response
                    try
                    {
                        using var errorDoc = JsonDocument.Parse(json);
                        if (errorDoc.RootElement.TryGetProperty("message", out var msg))
                            Console.WriteLine($"         {msg.GetString()}");
                    }
                    catch { Console.WriteLine($"         {json}"); }
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                {
                    var errorMsg = root.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error.";
                    Console.WriteLine($"  ERROR: {errorMsg}");
                    return;
                }

                var data = root.GetProperty("data");
                var connectorToken = data.GetProperty("connectorToken").GetString() ?? string.Empty;
                var hubUrl = data.GetProperty("hubUrl").GetString() ?? string.Empty;

                if (string.IsNullOrEmpty(connectorToken) || string.IsNullOrEmpty(hubUrl))
                {
                    Console.WriteLine("  ERROR: Server returned empty token or hub URL.");
                    return;
                }

                var config = new ConnectorConfig
                {
                    HubUrl = hubUrl,
                    ConnectorToken = connectorToken
                };

                var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, configJson);

                Console.WriteLine();
                Console.WriteLine("  ✓ Pairing successful!");
                Console.WriteLine($"  ✓ Configuration saved to {ConfigFilePath}");
                Console.WriteLine();
                Console.WriteLine("  The service will now start automatically.");
                Console.WriteLine("  Your WhatsApp QR code will appear in the BILLIT settings page.");
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"  ERROR: Could not connect to BILLIT API at {apiBaseUrl}");
                Console.WriteLine($"         {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("  Make sure:");
                Console.WriteLine("    1. The BILLIT API is running and accessible from this machine");
                Console.WriteLine("    2. The URL is correct (include https://)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy interactive setup: manually enter Hub URL and token.
        /// Kept as a fallback but not the default path.
        /// </summary>
        public static void RunManualSetup()
        {
            Console.WriteLine("BILLIT WhatsApp Connector — Manual Setup");
            Console.WriteLine("-----------------------------------------");

            var config = new ConnectorConfig();

            Console.Write("Enter Cloud Hub URL (e.g., https://your_cloud/hubs/whatsapp-connector): ");
            config.HubUrl = Console.ReadLine()?.Trim() ?? string.Empty;

            Console.Write("Enter Connector Token: ");
            config.ConnectorToken = Console.ReadLine()?.Trim() ?? string.Empty;

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);

            Console.WriteLine("\nConfiguration saved to " + ConfigFilePath);
        }

        public static ConnectorConfig Load()
        {
            if (!Exists())
                throw new FileNotFoundException($"Configuration file not found: {ConfigFilePath}");

            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<ConnectorConfig>(json);

            if (config == null || string.IsNullOrEmpty(config.HubUrl) || string.IsNullOrEmpty(config.ConnectorToken))
            {
                throw new InvalidOperationException("Invalid configuration format or missing required values.");
            }

            return config;
        }

        /// <summary>Reads password from console with * masking.</summary>
        private static string ReadPasswordMasked()
        {
            var password = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password.Length--;
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            return password.ToString();
        }
    }
}