namespace BILLIT.WhatsAppConnector.Services
{
    public static class AppPaths
    {
        private static readonly string _dataDir;
        private static readonly string _configFilePath;
        private static readonly string _sessionDir;
        private static readonly string _logsDir;
        private static readonly string _tempDir;

        static AppPaths()
        {
            // Determine writable data directory (ProgramData for Windows Service, or BaseDirectory for portable/dev)
            string candidateDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "BILLIT",
                "WhatsAppConnector");

            try
            {
                Directory.CreateDirectory(candidateDataDir);
                _dataDir = candidateDataDir;
            }
            catch
            {
                _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(_dataDir);
            }

            // Check if local dev/portable config exists
            string localConfig = Path.Combine(AppContext.BaseDirectory, "connector.config.json");
            string prodConfig = Path.Combine(_dataDir, "connector.config.json");

            if (File.Exists(localConfig) && !File.Exists(prodConfig))
            {
                _configFilePath = localConfig;
            }
            else
            {
                _configFilePath = prodConfig;
            }

            _sessionDir = Path.Combine(_dataDir, "session");
            _logsDir = Path.Combine(_dataDir, "logs");
            _tempDir = Path.Combine(_dataDir, "temp");

            Directory.CreateDirectory(_sessionDir);
            Directory.CreateDirectory(_logsDir);
            Directory.CreateDirectory(_tempDir);

            // Clean up stale temp print files older than 2 hours on startup
            CleanupStaleTempFiles();
        }

        public static string DataDirectory => _dataDir;
        public static string ConfigFilePath => _configFilePath;
        public static string SessionDirectory => _sessionDir;
        public static string LogsDirectory => _logsDir;
        public static string TempDirectory => _tempDir;

        public static string ResolveSumatraPdfPath(string? configuredPath = null)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            var searchList = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.exe"),
                Path.Combine(AppContext.BaseDirectory, "SumatraPDF.exe"),
                Path.Combine(Directory.GetCurrentDirectory(), "tools", "SumatraPDF.exe"),
                Path.Combine(Directory.GetCurrentDirectory(), "SumatraPDF.exe")
            };

            if (!string.IsNullOrEmpty(localAppData))
                searchList.Add(Path.Combine(localAppData, "SumatraPDF", "SumatraPDF.exe"));
            if (!string.IsNullOrEmpty(programFiles))
                searchList.Add(Path.Combine(programFiles, "SumatraPDF", "SumatraPDF.exe"));
            if (!string.IsNullOrEmpty(programFilesX86))
                searchList.Add(Path.Combine(programFilesX86, "SumatraPDF", "SumatraPDF.exe"));

            searchList.Add(@"C:\Program Files\SumatraPDF\SumatraPDF.exe");
            searchList.Add(@"C:\Program Files (x86)\SumatraPDF\SumatraPDF.exe");

            foreach (var path in searchList)
            {
                if (File.Exists(path)) return path;
            }

            return "SumatraPDF.exe"; // Fallback to PATH
        }

        public static string ResolveNodeExecutablePath()
        {
            var searchList = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "tools", "node", "node.exe"),
                Path.Combine(AppContext.BaseDirectory, "bridge", "node.exe"),
                @"C:\nvm4w\nodejs\node.exe",
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe"
            };

            foreach (var path in searchList)
            {
                if (File.Exists(path)) return path;
            }

            // Also check PATH directories explicitly for Windows Service compatibility
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), "node.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }

            return "node"; // Fallback to PATH
        }

        public static string ResolveBridgeScriptPath(string? configuredPath = null)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            var local = Path.Combine(AppContext.BaseDirectory, "bridge", "billit-wa-bridge.js");
            if (File.Exists(local)) return local;

            return Path.Combine(Directory.GetCurrentDirectory(), "bridge", "billit-wa-bridge.js");
        }

        private static void CleanupStaleTempFiles()
        {
            try
            {
                var dir = new DirectoryInfo(_tempDir);
                if (!dir.Exists) return;

                var cutoff = DateTime.UtcNow.AddHours(-2);
                foreach (var file in dir.GetFiles("billit-print-*.pdf"))
                {
                    if (file.LastWriteTimeUtc < cutoff)
                    {
                        try { file.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
