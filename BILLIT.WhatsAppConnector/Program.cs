using BILLIT.WhatsAppConnector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

// First run: no config file yet — prompt interactively, save it, then exit
// so the operator can install the service and start it fresh (a Windows
// Service has no console to prompt through, so this step only ever runs
// when someone double-clicks the exe directly during setup).
if (!ConnectorConfig.Exists())
{
    ConnectorConfig.RunInteractiveSetup();
    Console.WriteLine("Press Enter to exit. Then install and start the Windows Service.");
    Console.ReadLine();
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Ensure it listens on a local port for direct printing
builder.WebHost.UseUrls("http://localhost:5050");

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BILLIT WhatsApp Connector";
});

builder.Services.AddSingleton(_ => ConnectorConfig.Load());
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<PrinterManager>(); // PRINTING ADDITIONS
builder.Services.AddHostedService<ConnectorWorker>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("AllowAll");

app.MapGet("/api/printers", (PrinterManager pm) =>
{
    var printers = pm.GetInstalledPrinters();
    var defaultPrinter = pm.GetDefaultPrinterName();
    return Results.Ok(new { DefaultPrinter = defaultPrinter, Printers = printers });
});

app.MapPost("/api/print", async (PrintRequest req, PrinterManager pm) =>
{
    var bytes = Convert.FromBase64String(req.FileBase64);
    var (success, error) = await pm.PrintPdfAsync(bytes, req.PrinterName, req.Copies <= 0 ? 1 : req.Copies);
    if (success) return Results.Ok(new { Success = true });
    return Results.BadRequest(new { Success = false, Error = error });
});

await app.RunAsync();

public class PrintRequest
{
    public string? PrinterName { get; set; }
    public int Copies { get; set; }
    public string FileBase64 { get; set; } = "";
}
