using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BILLIT.WhatsAppConnector.Services;
using BILLIT.WhatsAppConnector.Services.WhatsApp;

namespace BILLIT.WhatsAppConnector
{
    public class ConnectorConfig
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WhatsAppMode Mode { get; set; } = WhatsAppMode.LocalBridge;

        public string HubUrl { get; set; } = string.Empty;
        public string ConnectorToken { get; set; } = string.Empty;

        // Meta WhatsApp Business Cloud API Settings
        public string? CloudApiPhoneNumberId { get; set; }
        public string? CloudApiAccessToken { get; set; }
        public string? CloudApiBusinessAccountId { get; set; }

        // Local Printing & Server Settings
        public string? SumatraPdfPath { get; set; }
        public int Port { get; set; } = 5050;

        public static string ConfigFilePath => AppPaths.ConfigFilePath;

        public static bool Exists()
        {
            return File.Exists(ConfigFilePath);
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to save configuration: {ex.Message}");
            }
        }

        public static ConnectorConfig Load()
        {
            if (!Exists())
            {
                // Non-blocking default: Allows printing service to start 100% offline without pairing
                return new ConnectorConfig
                {
                    Mode = WhatsAppMode.Disabled
                };
            }

            try
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<ConnectorConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return config ?? new ConnectorConfig { Mode = WhatsAppMode.Disabled };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Error reading {ConfigFilePath}: {ex.Message}. Using defaults.");
                return new ConnectorConfig { Mode = WhatsAppMode.Disabled };
            }
        }

        public bool IsCloudHubConfigured()
        {
            return !string.IsNullOrWhiteSpace(HubUrl) && !string.IsNullOrWhiteSpace(ConnectorToken);
        }

        /// <summary>
        /// Interactive setup for terminal use (only runs if `--setup` flag is passed).
        /// Never called automatically in service mode.
        /// </summary>
        public static async Task RunInteractiveSetupAsync()
        {
            await Task.Yield();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            BILLIT DEVICE AGENT — INTERACTIVE SETUP               ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("Select setup mode:");
            Console.WriteLine("  1. Connect to BILLIT Cloud (Pair Shop with Hub)");
            Console.WriteLine("  2. Configure Official Meta WhatsApp Cloud API");
            Console.WriteLine("  3. Run Printing-Only Mode (Offline / No WhatsApp)");
            Console.Write("\nChoice (1-3) [Default 1]: ");

            var choice = Console.ReadLine()?.Trim();
            if (choice == "3")
            {
                var cfg = Load();
                cfg.Mode = WhatsAppMode.Disabled;
                cfg.Save();
                Console.WriteLine("✓ Configured in Printing-Only Mode.");
                return;
            }

            if (choice == "2")
            {
                var cfg = Load();
                cfg.Mode = WhatsAppMode.CloudApi;
                Console.Write("Enter Meta Phone Number ID: ");
                cfg.CloudApiPhoneNumberId = Console.ReadLine()?.Trim();
                Console.Write("Enter Meta Permanent Access Token: ");
                cfg.CloudApiAccessToken = Console.ReadLine()?.Trim();
                cfg.Save();
                Console.WriteLine("✓ Meta WhatsApp Cloud API credentials saved.");
                return;
            }

            // Mode 1: Cloud Hub Pairing (via Setup Key from BILLIT UI or manual entry)
            Console.WriteLine("\nPair with BILLIT Cloud Hub:");
            Console.WriteLine("In BILLIT UI Settings -> WhatsApp Settings -> click 'Generate Connector Setup Key'.");
            Console.Write("\nPaste Setup Key JSON or press Enter to input manually: ");
            var input = Console.ReadLine()?.Trim();

            string hubUrl = "";
            string token = "";

            if (!string.IsNullOrWhiteSpace(input) && input.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(input);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("data", out var data))
                    {
                        token = data.TryGetProperty("connectorToken", out var t) ? t.GetString() ?? "" : "";
                        hubUrl = data.TryGetProperty("hubUrl", out var h) ? h.GetString() ?? "" : "";
                    }
                    else
                    {
                        token = root.TryGetProperty("connectorToken", out var t) ? t.GetString() ?? "" : "";
                        hubUrl = root.TryGetProperty("hubUrl", out var h) ? h.GetString() ?? "" : "";
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warning: could not parse JSON: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(hubUrl))
            {
                Console.Write("Enter Hub URL (e.g. https://yourdomain.com/hubs/whatsapp-connector): ");
                hubUrl = Console.ReadLine()?.Trim() ?? "";
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Write("Enter Connector Token: ");
                token = Console.ReadLine()?.Trim() ?? "";
            }

            if (!string.IsNullOrWhiteSpace(hubUrl) && !string.IsNullOrWhiteSpace(token))
            {
                var cfg = Load();
                cfg.HubUrl = hubUrl;
                cfg.ConnectorToken = token;
                cfg.Mode = WhatsAppMode.LocalBridge;
                cfg.Save();

                Console.WriteLine("\n  ✓ Pairing configuration saved successfully!");
                Console.WriteLine($"  ✓ Config file: {ConfigFilePath}");
            }
            else
            {
                Console.WriteLine("\n  ERROR: Both Hub URL and Connector Token are required.");
            }
        }
    }
}