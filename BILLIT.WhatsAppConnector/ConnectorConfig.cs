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

        public static void RunInteractiveSetup()
        {
            Console.WriteLine("BILLIT WhatsApp Connector Setup");
            Console.WriteLine("-------------------------------");

            var config = new ConnectorConfig();

            Console.Write("Enter Cloud Hub URL (e.g., https://your_cloud/hub): ");
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
    }
}