using System.Diagnostics;

namespace BILLIT.WhatsAppConnector.Services
{
    public static class WindowsServiceManager
    {
        public const string ServiceName = "BILLIT_DeviceAgent";
        public const string DisplayName = "BILLIT Device Agent";
        public const string Description = "Provides silent POS thermal receipt printing and optional WhatsApp messaging for BILLIT POS.";

        public static async Task HandleCommandAsync(string command)
        {
            if (!OperatingSystem.IsWindows() && (command.StartsWith("--install") || command.StartsWith("--uninstall") || command.StartsWith("--start") || command.StartsWith("--stop") || command.StartsWith("--status")))
            {
                Console.WriteLine("Error: Windows Service management commands are only supported on Windows.");
                return;
            }

            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "BILLIT_WhatsAppConnector.exe");

            switch (command.ToLowerInvariant())
            {
                case "--install":
                    await InstallServiceAsync(exePath);
                    break;
                case "--uninstall":
                    await UninstallServiceAsync();
                    break;
                case "--start":
                    await RunScAsync($"start \"{ServiceName}\"");
                    break;
                case "--stop":
                    await RunScAsync($"stop \"{ServiceName}\"");
                    break;
                case "--status":
                    await RunScAsync($"query \"{ServiceName}\"");
                    break;
                default:
                    PrintHelp();
                    break;
            }
        }

        private static async Task InstallServiceAsync(string exePath)
        {
            Console.WriteLine($"Installing '{DisplayName}' as a Windows Service...");

            var binPath = $"\"{exePath}\"";
            var createCmd = $"create \"{ServiceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"";
            await RunScAsync(createCmd);

            var descCmd = $"description \"{ServiceName}\" \"{Description}\"";
            await RunScAsync(descCmd);

            // Configure automatic recovery (restart service on failure)
            var failCmd = $"failure \"{ServiceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000";
            await RunScAsync(failCmd);

            Console.WriteLine("Starting service...");
            await RunScAsync($"start \"{ServiceName}\"");

            Console.WriteLine("\n✓ Installation complete! The service will now run automatically on Windows boot.");
            Console.WriteLine("✓ Control panel is accessible at: http://localhost:5050/");
        }

        private static async Task UninstallServiceAsync()
        {
            Console.WriteLine($"Uninstalling '{DisplayName}'...");
            await RunScAsync($"stop \"{ServiceName}\"");
            await RunScAsync($"delete \"{ServiceName}\"");
            Console.WriteLine("✓ Service uninstalled successfully.");
        }

        private static async Task RunScAsync(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return;

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
                if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine(stderr.Trim());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing sc.exe: {ex.Message}");
            }
        }

        public static void PrintHelp()
        {
            Console.WriteLine("BILLIT Device Agent — Command Line Options:");
            Console.WriteLine("  (no args)      Run in normal host mode (as Windows Service or console dev host)");
            Console.WriteLine("  --install      Install as automatic Windows Service with restart recovery");
            Console.WriteLine("  --uninstall    Stop and delete the Windows Service");
            Console.WriteLine("  --start        Start the installed Windows Service");
            Console.WriteLine("  --stop         Stop the running Windows Service");
            Console.WriteLine("  --status       Check Windows Service status");
            Console.WriteLine("  --setup        Run interactive terminal configuration wizard");
            Console.WriteLine("  --help         Show this help information");
        }
    }
}
