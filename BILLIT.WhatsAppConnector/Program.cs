using BILLIT.WhatsAppConnector;
using BILLIT.WhatsAppConnector.Services;
using BILLIT.WhatsAppConnector.Services.WhatsApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;

// 1. Check for CLI Service Management & Help Arguments
if (args.Length > 0)
{
    var firstArg = args[0].ToLowerInvariant();
    if (firstArg is "--install" or "--uninstall" or "--start" or "--stop" or "--status" or "--help" or "-h")
    {
        await WindowsServiceManager.HandleCommandAsync(firstArg);
        return;
    }

    if (firstArg is "--setup")
    {
        await ConnectorConfig.RunInteractiveSetupAsync();
        return;
    }
}

// 2. Configure Production Logging with Serilog (Console + Daily Rolling File)
var logPathPattern = Path.Combine(AppPaths.LogsDirectory, "agent-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        logPathPattern,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting BILLIT Device Agent Host...");
    Log.Information("Data Directory: {DataDir}", AppPaths.DataDirectory);
    Log.Information("Config File: {ConfigFile}", AppPaths.ConfigFilePath);

    // 3. Load Configuration (Non-blocking default if unconfigured)
    var config = ConnectorConfig.Load();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    builder.Host.UseSerilog();

    if (OperatingSystem.IsWindows())
    {
        builder.Host.UseWindowsService();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = WindowsServiceManager.DisplayName;
        });
    }

    // Configure Host Port
    builder.WebHost.UseUrls($"http://localhost:{config.Port}");

    // DI Registrations
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<IPrinterService, PrinterService>();
    builder.Services.AddSingleton<PrinterManager>();

    builder.Services.AddSingleton<DisabledWhatsAppService>();
    builder.Services.AddSingleton<BaileysBridgeWhatsAppService>();
    builder.Services.AddSingleton<CloudApiWhatsAppService>();
    builder.Services.AddSingleton<WhatsAppCoordinator>();
    builder.Services.AddSingleton<IWhatsAppService>(sp => sp.GetRequiredService<WhatsAppCoordinator>());

    builder.Services.AddHostedService<CloudHubRelay>();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("BillitOrigins", policy =>
        {
            policy.SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrWhiteSpace(origin)) return false;
                try
                {
                    var uri = new Uri(origin);
                    // Development: Angular UI at localhost:4200, local control panel at :5050, or any local loopback port
                    if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    // Production: bistore.online, www.bistore.online, *.bistore.online
                    if (uri.Host.Equals("bistore.online", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".bistore.online", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return false;
                }
                catch
                {
                    return false;
                }
            })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
        });

        options.AddPolicy("AllowAll", policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());
    });

    var app = builder.Build();

    app.UseCors("BillitOrigins");

    // ---------------- LOCAL CONTROL PANEL ----------------
    app.MapGet("/", () => Results.Content(DiagnosticsDashboard.HtmlContent, "text/html; charset=utf-8"));

    // ---------------- HEALTH & SYSTEM STATUS ----------------
    app.MapGet("/api/health", () => Results.Ok(new { Status = "UP", Timestamp = DateTime.UtcNow }));

    app.MapGet("/api/status", async (IPrinterService printerService, IWhatsAppService whatsAppService, ConnectorConfig cfg) =>
    {
        var waStatus = await whatsAppService.GetStatusAsync();
        return Results.Ok(new
        {
            Status = "Healthy",
            Printing = new
            {
                DefaultPrinter = printerService.GetDefaultPrinterName(),
                Printers = printerService.GetInstalledPrinters()
            },
            WhatsApp = new
            {
                Mode = waStatus.Mode.ToString(),
                State = waStatus.State,
                IsDead = waStatus.Mode == WhatsAppMode.Disabled,
                ConnectedNumber = waStatus.ConnectedNumber,
                QrCodeBase64 = waStatus.QrCodeBase64,
                ErrorMessage = waStatus.ErrorMessage
            },
            CloudHub = new
            {
                IsConfigured = cfg.IsCloudHubConfigured(),
                Url = cfg.HubUrl,
                HasToken = !string.IsNullOrWhiteSpace(cfg.ConnectorToken)
            }
        });
    });

    // ---------------- RECENT LOGS VIEWER ----------------
    app.MapGet("/api/logs/recent", () =>
    {
        try
        {
            var todayLog = Path.Combine(AppPaths.LogsDirectory, $"agent-{DateTime.UtcNow:yyyyMMdd}.log");
            if (!File.Exists(todayLog))
            {
                var files = new DirectoryInfo(AppPaths.LogsDirectory).GetFiles("agent-*.log");
                if (files.Length > 0)
                {
                    todayLog = files.OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
                }
                else
                {
                    return Results.Text("No log files recorded yet.", "text/plain");
                }
            }

            using var fs = new FileStream(todayLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
                if (lines.Count > 200) lines.RemoveAt(0);
            }

            return Results.Text(string.Join(Environment.NewLine, lines), "text/plain");
        }
        catch (Exception ex)
        {
            return Results.Text($"Could not read log file: {ex.Message}", "text/plain");
        }
    });

    // ---------------- PRINTER ENDPOINTS ----------------
    app.MapGet("/api/printers", (IPrinterService pm) =>
    {
        var printers = pm.GetInstalledPrinters();
        var defaultPrinter = pm.GetDefaultPrinterName();
        return Results.Ok(new { DefaultPrinter = defaultPrinter, Printers = printers });
    });

    app.MapPost("/api/print", async (PrintRequest req, IPrinterService pm) =>
    {
        if (string.IsNullOrWhiteSpace(req.FileBase64))
        {
            return Results.BadRequest(new { Success = false, Error = "FileBase64 is required." });
        }

        try
        {
            var bytes = Convert.FromBase64String(req.FileBase64);
            var (success, error) = await pm.PrintPdfAsync(bytes, req.PrinterName, req.Copies <= 0 ? 1 : req.Copies, req.PaperSize);
            if (success) return Results.Ok(new { Success = true });
            return Results.BadRequest(new { Success = false, Error = error });
        }
        catch (FormatException ex)
        {
            return Results.BadRequest(new { Success = false, Error = "Invalid Base64 string: " + ex.Message });
        }
    });

    app.MapPost("/api/print/test", async (HttpRequest request, IPrinterService pm) =>
    {
        string? printerName = null;
        if (request.HasJsonContentType())
        {
            try { var req = await request.ReadFromJsonAsync<PrinterActionRequest>(); printerName = req?.PrinterName; } catch { }
        }
        var (success, error) = await pm.PrintTestReceiptAsync(printerName);
        if (success) return Results.Ok(new { Success = true });
        return Results.BadRequest(new { Success = false, Error = error });
    });

    app.MapPost("/api/print/raw", async (RawPrintRequest req, IPrinterService pm) =>
    {
        if (string.IsNullOrWhiteSpace(req.DataBase64))
        {
            return Results.BadRequest(new { Success = false, Error = "DataBase64 is required." });
        }

        try
        {
            var bytes = Convert.FromBase64String(req.DataBase64);
            var (success, error) = await pm.PrintRawAsync(bytes, req.PrinterName);
            if (success) return Results.Ok(new { Success = true });
            return Results.BadRequest(new { Success = false, Error = error });
        }
        catch (FormatException ex)
        {
            return Results.BadRequest(new { Success = false, Error = "Invalid Base64 string: " + ex.Message });
        }
    });

    app.MapPost("/api/print/drawer", async (HttpRequest request, IPrinterService pm) =>
    {
        string? printerName = null;
        if (request.HasJsonContentType())
        {
            try { var req = await request.ReadFromJsonAsync<PrinterActionRequest>(); printerName = req?.PrinterName; } catch { }
        }
        var (success, error) = await pm.OpenCashDrawerAsync(printerName);
        if (success) return Results.Ok(new { Success = true });
        return Results.BadRequest(new { Success = false, Error = error });
    });

    app.MapPost("/api/print/cut", async (HttpRequest request, IPrinterService pm) =>
    {
        string? printerName = null;
        if (request.HasJsonContentType())
        {
            try { var req = await request.ReadFromJsonAsync<PrinterActionRequest>(); printerName = req?.PrinterName; } catch { }
        }
        var (success, error) = await pm.CutPaperAsync(printerName);
        if (success) return Results.Ok(new { Success = true });
        return Results.BadRequest(new { Success = false, Error = error });
    });

    // ---------------- CLOUD HUB PAIRING ENDPOINT ----------------
    app.MapPost("/api/cloudhub/config", (CloudHubConfigRequest req, ConnectorConfig cfg) =>
    {
        if (string.IsNullOrWhiteSpace(req.HubUrl) || string.IsNullOrWhiteSpace(req.ConnectorToken))
        {
            return Results.BadRequest(new { Success = false, Error = "HubUrl and ConnectorToken are required." });
        }

        cfg.HubUrl = req.HubUrl.Trim();
        cfg.ConnectorToken = req.ConnectorToken.Trim();
        cfg.Save();

        Log.Information("Cloud Hub credentials updated. New Hub URL: {Url}", cfg.HubUrl);
        return Results.Ok(new { Success = true, Message = "Cloud Hub settings saved. Connection will re-affirm automatically." });
    });

    // ---------------- WHATSAPP ENDPOINTS (DEAD BY DEFAULT — ON DEMAND) ----------------
    app.MapGet("/api/whatsapp/status", async (IWhatsAppService wa) =>
    {
        var status = await wa.GetStatusAsync();
        return Results.Ok(new
        {
            Mode = status.Mode.ToString(),
            State = status.State,
            IsDead = status.Mode == WhatsAppMode.Disabled,
            ConnectedNumber = status.ConnectedNumber,
            QrCodeBase64 = status.QrCodeBase64,
            ErrorMessage = status.ErrorMessage
        });
    });

    app.MapPost("/api/whatsapp/config", async (WhatsAppConfigRequest req, WhatsAppCoordinator coordinator, ConnectorConfig cfg) =>
    {
        if (Enum.TryParse<WhatsAppMode>(req.Mode, true, out var mode))
        {
            if (!string.IsNullOrWhiteSpace(req.CloudApiPhoneNumberId))
                cfg.CloudApiPhoneNumberId = req.CloudApiPhoneNumberId;
            if (!string.IsNullOrWhiteSpace(req.CloudApiAccessToken))
                cfg.CloudApiAccessToken = req.CloudApiAccessToken;

            await coordinator.SwitchModeAsync(mode);
            return Results.Ok(new { Success = true, Mode = mode.ToString(), IsDead = mode == WhatsAppMode.Disabled });
        }
        return Results.BadRequest(new { Success = false, Error = $"Invalid mode '{req.Mode}'. Allowed: Disabled, LocalBridge, CloudApi" });
    });

    // Bring WhatsApp alive from dead state
    app.MapPost("/api/whatsapp/start", async (IWhatsAppService wa) =>
    {
        await wa.StartAsync();
        var status = await wa.GetStatusAsync();
        return Results.Ok(new
        {
            Success = true,
            Message = "WhatsApp service awakened and starting",
            Mode = status.Mode.ToString(),
            State = status.State,
            IsDead = status.Mode == WhatsAppMode.Disabled
        });
    });

    app.MapPost("/api/whatsapp/enable", async (IWhatsAppService wa) =>
    {
        await wa.StartAsync();
        var status = await wa.GetStatusAsync();
        return Results.Ok(new
        {
            Success = true,
            Message = "WhatsApp service enabled and starting",
            Mode = status.Mode.ToString(),
            State = status.State,
            IsDead = status.Mode == WhatsAppMode.Disabled
        });
    });

    // Return WhatsApp to dead state (kills Node.js bridge process)
    app.MapPost("/api/whatsapp/stop", async (IWhatsAppService wa) =>
    {
        await wa.DisconnectAsync();
        return Results.Ok(new
        {
            Success = true,
            Message = "WhatsApp service stopped and returned to dead state (Disabled)",
            Mode = "Disabled",
            State = "Disabled",
            IsDead = true
        });
    });

    app.MapPost("/api/whatsapp/disconnect", async (IWhatsAppService wa) =>
    {
        await wa.DisconnectAsync();
        return Results.Ok(new
        {
            Success = true,
            Message = "WhatsApp service disconnected and returned to dead state",
            Mode = "Disabled",
            State = "Disabled",
            IsDead = true
        });
    });

    app.MapPost("/api/whatsapp/send", async (SendDirectMessageRequest req, IWhatsAppService wa) =>
    {
        if (string.IsNullOrWhiteSpace(req.To) || string.IsNullOrWhiteSpace(req.Message))
        {
            return Results.BadRequest(new { Success = false, Error = "'To' and 'Message' are required." });
        }

        var logId = DateTime.UtcNow.Ticks;
        var (success, messageId, error) = await wa.SendTextAsync(logId, req.To, req.Message);
        if (success)
        {
            return Results.Ok(new { Success = true, MessageId = messageId });
        }
        return Results.BadRequest(new { Success = false, Error = error });
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// ---------------- DTO Request Models ----------------
public class PrintRequest
{
    public string? PrinterName { get; set; }
    public int Copies { get; set; } = 1;
    public string? PaperSize { get; set; } = "80mm";
    public string FileBase64 { get; set; } = "";
}

public class PrinterActionRequest
{
    public string? PrinterName { get; set; }
}

public class WhatsAppConfigRequest
{
    public string Mode { get; set; } = "Disabled";
    public string? CloudApiPhoneNumberId { get; set; }
    public string? CloudApiAccessToken { get; set; }
}

public class SendDirectMessageRequest
{
    public string To { get; set; } = "";
    public string Message { get; set; } = "";
}

public class CloudHubConfigRequest
{
    public string HubUrl { get; set; } = "";
    public string ConnectorToken { get; set; } = "";
}
