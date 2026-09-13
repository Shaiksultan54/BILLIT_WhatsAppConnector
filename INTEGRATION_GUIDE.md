# BILLIT Professional Integration Guide: Device Agent, Backend & Frontend

> **Target Codebases**:
> - **Frontend (Angular)**: `D:\NEW_MYTECHIN_File\Repository_UI\BILLIT_V2\BILLIT\BILLITUI`
> - **Backend (ASP.NET Core)**: `D:\MyTechin_Ecosystem\src\Apps\BILLIT`
> - **Device Agent (Windows Service / Host)**: `BILLIT.WhatsAppConnector` (Runs on shop POS counter)

---

## 1. Architectural Blueprint & URL Topology

```
+-----------------------------------------------------------------------------+
|                           CLIENT BROWSERS                                   |
|   Development: http://localhost:4200   |   Production: https://bistore.online|
+-----------------------------------------------------------------------------+
               |                                            |
      (1) Direct Local REST                         (2) HTTPS API / WSS
      (0ms Printing, Status)                        (Orders, Invoices, Sync)
               |                                            |
               v                                            v
+------------------------------------+      +----------------------------------+
|      LOCAL BILLIT DEVICE AGENT     |      |          BILLIT BACKEND          |
|      (Shop Counter PC :5050)       |      |          (Cloud Server)          |
|                                    |      |                                  |
|  [POS Printing Engine]  <--- ALWAYS|      |  WhatsAppConnectorHub            |
|    - SumatraPDF Silent Engine      |      |  (/hubs/whatsapp-connector)      |
|    - Windows Spooler Thermal Print |      |                                  |
|    - ESC/POS Cash Drawer & Cut     |      |  Shop Authentication             |
|                                    |      |  (master_shop_id JWT)            |
|  [WhatsApp Engine]     <--- DEAD   |      +----------------------------------+
|    - Baileys Node.js Bridge        |                      ^
|    - Starts ONLY on UI Enable      |                      |
|    - Auto-kills to Dead state      |                      |
+------------------------------------+                      |
               ^                                            |
               +=================== SignalR Relay ==========+
                                (WSS / Reconnecting)
```

### Core Architecture Principles:
1. **Frontend Origins**:
   - **Development**: `http://localhost:4200`
   - **Production**: `https://bistore.online` (and `https://www.bistore.online`)
   - **Legacy Port 5500 is completely removed**. All CORS headers on the Agent (`:5050`) are configured to allow `http://localhost:4200` and `https://bistore.online` with full credentials support.
2. **Shop-Wise Isolation**:
   - Every physical counter/shop runs one instance of `BILLIT Device Agent`.
   - The Agent authenticates to the Cloud Hub with a cryptographic `ConnectorToken` containing `master_shop_id`.
   - All print jobs and WhatsApp messages are strictly routed to the corresponding shop's hardware.
3. **WhatsApp Lifecycle: DEAD by Default**:
   - When the Device Agent starts, **WhatsApp is 100% DEAD** (`WhatsAppMode.Disabled`).
   - Zero Node.js processes run, 0% CPU consumption, no background socket connection attempts.
   - **POS Thermal Printing runs 100% offline-ready** from second 1.
   - WhatsApp is **awakened only when explicitly requested** by the frontend or backend (e.g. user toggles WhatsApp on in `bistore.online` or clicks "Connect WhatsApp").
   - When stopped or disconnected, the Node.js bridge process is **force-terminated**, immediately returning WhatsApp to the dead state.

---

## 2. Frontend Integration Guide (`BILLITUI`)

### A. Environment Configuration

In `src/environments/environment.ts` (Development):
```typescript
export const environment = {
  production: false,
  apiUrl: 'https://localhost:7237/api',
  agentUrl: 'http://localhost:5050', // Local BILLIT Device Agent
  appUrl: 'http://localhost:4200'
};
```

In `src/environments/environment.prod.ts` (Production):
```typescript
export const environment = {
  production: true,
  apiUrl: 'https://api.bistore.online/api',
  agentUrl: 'http://localhost:5050', // Cashier counter PC
  appUrl: 'https://bistore.online'
};
```

---

### B. Direct Local Printing Service (`device-agent.service.ts`)

Create or update a dedicated service in Angular to interact with the local agent for sub-millisecond silent printing:

```typescript
import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { environment } from '../../../environments/environment';

export interface AgentStatus {
  status: string;
  printing: {
    defaultPrinter: string | null;
    printers: string[];
  };
  whatsApp: {
    mode: 'Disabled' | 'LocalBridge' | 'CloudApi';
    state: 'Disabled' | 'PendingQr' | 'Connected' | 'Expired' | 'Disconnected';
    isDead: boolean;
    connectedNumber?: string;
    qrCodeBase64?: string;
    errorMessage?: string;
  };
  cloudHub: {
    isConfigured: boolean;
    url: string;
    hasToken: boolean;
  };
}

@Injectable({
  providedIn: 'root'
})
export class DeviceAgentService {
  private readonly agentBase = environment.agentUrl; // http://localhost:5050

  constructor(private http: HttpClient) {}

  /** Check if the local Device Agent is running on the counter PC */
  checkAgentHealth(): Observable<boolean> {
    return this.http.get<{ status: string }>(`${this.agentBase}/api/health`).pipe(
      catchError(() => of(false))
    );
  }

  /** Get complete hardware status (Printers + WhatsApp state) */
  getStatus(): Observable<AgentStatus> {
    return this.http.get<AgentStatus>(`${this.agentBase}/api/status`);
  }

  /** List installed thermal receipt printers */
  getPrinters(): Observable<{ defaultPrinter: string; printers: string[] }> {
    return this.http.get<{ defaultPrinter: string; printers: string[] }>(`${this.agentBase}/api/printers`);
  }

  /**
   * Print PDF directly to local thermal printer (0ms latency, works offline)
   * @param fileBase64 Base64 encoded PDF receipt
   * @param printerName Optional printer name (omit for system default)
   * @param paperSize "80mm" or "58mm"
   */
  printPdf(fileBase64: string, printerName?: string, copies: number = 1, paperSize: string = '80mm'): Observable<{ success: boolean; error?: string }> {
    return this.http.post<{ success: boolean; error?: string }>(`${this.agentBase}/api/print`, {
      fileBase64,
      printerName,
      copies,
      paperSize
    });
  }

  /** Pulse Cash Drawer to open */
  openCashDrawer(printerName?: string): Observable<{ success: boolean; error?: string }> {
    return this.http.post<{ success: boolean; error?: string }>(`${this.agentBase}/api/print/drawer`, { printerName });
  }

  /** Trigger automatic paper cut */
  cutPaper(printerName?: string): Observable<{ success: boolean; error?: string }> {
    return this.http.post<{ success: boolean; error?: string }>(`${this.agentBase}/api/print/cut`, { printerName });
  }

  /** Print built-in test alignment receipt */
  printTest(printerName?: string): Observable<{ success: boolean; error?: string }> {
    return this.http.post<{ success: boolean; error?: string }>(`${this.agentBase}/api/print/test`, { printerName });
  }

  // ===================== WHATSAPP ON-DEMAND LIFECYCLE =====================

  /**
   * Awaken WhatsApp service from DEAD state.
   * Spawns the Baileys Node.js bridge process and starts QR generation.
   */
  awakenWhatsApp(): Observable<{ success: boolean; message: string; mode: string; state: string; isDead: boolean }> {
    return this.http.post<any>(`${this.agentBase}/api/whatsapp/start`, {});
  }

  /**
   * Stop WhatsApp and put it into DEAD state.
   * Completely terminates the Node.js process and releases all memory.
   */
  killWhatsApp(): Observable<{ success: boolean; message: string; mode: string; state: string; isDead: boolean }> {
    return this.http.post<any>(`${this.agentBase}/api/whatsapp/stop`, {});
  }

  /** Get live WhatsApp status (includes QR code base64 if state is 'PendingQr') */
  getWhatsAppStatus(): Observable<{
    mode: string;
    state: string;
    isDead: boolean;
    connectedNumber?: string;
    qrCodeBase64?: string;
    errorMessage?: string;
  }> {
    return this.http.get<any>(`${this.agentBase}/api/whatsapp/status`);
  }
}
```

---

### C. Cashier POS Component: Direct Print Button

In your POS checkout component (e.g. `pos-checkout.component.ts`):

```typescript
import { Component } from '@angular/core';
import { DeviceAgentService } from '../../core/services/device-agent.service';

@Component({
  selector: 'app-pos-checkout',
  template: `
    <button class="btn btn-primary" (click)="onCompleteSaleAndPrint(receiptPdfBase64)">
      Complete Sale & Print Receipt
    </button>
  `
})
export class PosCheckoutComponent {
  constructor(private agent: DeviceAgentService) {}

  onCompleteSaleAndPrint(pdfBase64: string) {
    this.agent.printPdf(pdfBase64).subscribe({
      next: (res) => {
        if (res.success) {
          console.log('Receipt printed immediately via local agent');
          this.agent.openCashDrawer().subscribe(); // Kick drawer
        } else {
          console.warn('Local print error:', res.error);
        }
      },
      error: (err) => {
        // Fallback: If local agent is not running on this PC, prompt user or use browser window.print()
        console.warn('Device agent unreachable on localhost:5050. Falling back to browser print dialog.');
        window.print();
      }
    });
  }
}
```

---

### D. WhatsApp Management Component: Dead vs Active State

In your WhatsApp settings page (e.g. `whatsapp-dashboard.component.ts`):

```typescript
// Check status on load
loadWhatsAppStatus() {
  this.agent.getWhatsAppStatus().subscribe({
    next: (status) => {
      this.isDead = status.isDead; // true by default!
      this.statusState = status.state;
      this.qrCode = status.qrCodeBase64 ? 'data:image/png;base64,' + status.qrCodeBase64 : null;
      this.connectedNumber = status.connectedNumber;
    }
  });
}

// User toggles WhatsApp switch ON
onEnableWhatsApp() {
  this.loading = true;
  this.agent.awakenWhatsApp().subscribe({
    next: (res) => {
      this.loading = false;
      this.isDead = false;
      this.pollQrCode(); // Poll every 2s until connected
    }
  });
}

// User toggles WhatsApp switch OFF
onDisableWhatsApp() {
  this.loading = true;
  this.agent.killWhatsApp().subscribe({
    next: (res) => {
      this.loading = false;
      this.isDead = true;
      this.qrCode = null;
      this.connectedNumber = null;
    }
  });
}
```

---

## 3. Backend Integration Guide (`BILLIT API`)

In `D:\MyTechin_Ecosystem\src\Apps\BILLIT`, the backend communicates with the shop's Device Agent via the SignalR Hub:

### A. Hub Definition (`WhatsAppConnectorHub.cs`)

Ensure your backend hub registers shop groups based on the authenticated JWT:

```csharp
[Authorize]
public class WhatsAppConnectorHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Extract master_shop_id from ConnectorToken claims
        var shopIdClaim = Context.User?.FindFirst("master_shop_id")?.Value;
        if (!string.IsNullOrEmpty(shopIdClaim))
        {
            var groupName = $"shop-{shopIdClaim}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        }
        await base.OnConnectedAsync();
    }

    // Called by Device Agent to report installed printers
    public async Task ReportPrinters(string? defaultPrinter, List<string> printers)
    {
        var shopId = Context.User?.FindFirst("master_shop_id")?.Value;
        // Store in DB for this shop
    }

    // Called by Device Agent when QR code changes
    public async Task ReportQr(string qrCodeBase64)
    {
        var shopId = Context.User?.FindFirst("master_shop_id")?.Value;
        // Forward QR code via WebSocket to Angular frontend (bistore.online)
    }

    // Called by Device Agent when connected
    public async Task ReportConnected(string phoneNumber)
    {
        var shopId = Context.User?.FindFirst("master_shop_id")?.Value;
        // Update Shop WhatsApp settings: ConnectedNumber = phoneNumber
    }

    // Called by Device Agent when disconnected or killed
    public async Task ReportDisconnected(string reason)
    {
        var shopId = Context.User?.FindFirst("master_shop_id")?.Value;
        // Update Shop status: Disconnected
    }
}
```

---

### B. Remote Commands Sent by Backend to Device Agent

To dispatch commands to a specific shop (`master_shop_id`):

```csharp
public class ShopDeviceDispatcher
{
    private readonly IHubContext<WhatsAppConnectorHub> _hubContext;

    public ShopDeviceDispatcher(IHubContext<WhatsAppConnectorHub> hubContext)
    {
        _hubContext = hubContext;
    }

    // 1. Silent Remote Printing
    public async Task PrintReceiptAsync(int shopId, long printJobLogId, string fileName, byte[] pdfBytes, string? printerName = null)
    {
        var group = $"shop-{shopId}";
        await _hubContext.Clients.Group(group).SendAsync("PrintDocument", new
        {
            PrintJobLogId = printJobLogId,
            FileName = fileName,
            PrinterName = printerName,
            Copies = 1,
            FileBase64 = Convert.ToBase64String(pdfBytes),
            PaperSize = "80mm"
        });
    }

    // 2. Awaken WhatsApp (User clicked 'Connect' on bistore.online)
    public async Task StartWhatsAppSessionAsync(int shopId)
    {
        var group = $"shop-{shopId}";
        // Device Agent will auto-switch mode to LocalBridge and launch the bridge process
        await _hubContext.Clients.Group(group).SendAsync("StartSession");
    }

    // 3. Kill WhatsApp (User clicked 'Disconnect' or disabled in settings)
    public async Task StopWhatsAppSessionAsync(int shopId)
    {
        var group = $"shop-{shopId}";
        // Device Agent will kill the Node process and return to DEAD state
        await _hubContext.Clients.Group(group).SendAsync("Disconnect");
    }

    // 4. Send Message via Shop's Device
    public async Task SendWhatsAppMessageAsync(int shopId, long messageLogId, string recipientPhone, string message)
    {
        var group = $"shop-{shopId}";
        await _hubContext.Clients.Group(group).SendAsync("SendText", new
        {
            MessageLogId = messageLogId,
            To = recipientPhone,
            Message = message
        });
    }
}
```

---

## 4. Local Agent Endpoints Reference (`http://localhost:5050`)

| Endpoint | Method | Purpose | WhatsApp Status |
| :--- | :--- | :--- | :--- |
| `/` | `GET` | HTML Diagnostics Control Panel & Live Logs | Safe |
| `/api/health` | `GET` | Health check (`{ status: "UP" }`) | Safe |
| `/api/status` | `GET` | Full status (Default Printer, Printer list, WhatsApp `isDead`, CloudHub status) | Safe |
| `/api/printers` | `GET` | Get list of Windows installed printers | Safe |
| `/api/print` | `POST` | Print Base64 PDF to thermal printer (`FileBase64`, `PrinterName`, `PaperSize`) | Safe (Offline ready) |
| `/api/print/drawer` | `POST` | Kick cash drawer pulse | Safe |
| `/api/print/cut` | `POST` | Send paper cut command | Safe |
| `/api/print/test` | `POST` | Print diagnostic test receipt | Safe |
| `/api/whatsapp/status` | `GET` | Returns WhatsApp state (`isDead: true`, `mode: "Disabled"`) | Safe |
| `/api/whatsapp/start` | `POST` | **Awakens** WhatsApp from dead state and launches bridge | Transitions to Alive |
| `/api/whatsapp/stop` | `POST` | **Kills** bridge process and puts WhatsApp into DEAD state | Transitions to Dead |
| `/api/whatsapp/disconnect` | `POST` | Same as stop (terminates process, dead state) | Transitions to Dead |
| `/api/whatsapp/config` | `POST` | Set Mode: `"Disabled"` (Dead), `"LocalBridge"`, or `"CloudApi"` | Configurable |
| `/api/cloudhub/config` | `POST` | Pair with BILLIT Cloud (`HubUrl`, `ConnectorToken`) | Safe |

---

## 5. Verification Checklist

1. **Verify Startup State**:
   - Start the Device Agent (`BILLIT_WhatsAppConnector.exe` or `dotnet run`).
   - Open Task Manager: Confirm **no `node.exe`** or `billit-wa-bridge.js` process is running.
   - Open `http://localhost:5050/`: Confirm the WhatsApp pill displays **`DEAD / INACTIVE`**.
   - Thermal Printing status shows **`100% Offline Ready`**.
2. **Verify Printing**:
   - On `http://localhost:5050/`, click **Print Test Page**. Thermal printer immediately feeds and prints receipt.
   - No WhatsApp initialization occurs during printing.
3. **Verify On-Demand Awakening**:
   - From Angular frontend (`http://localhost:4200` or `https://bistore.online`) or local panel, click **Awaken & Start WhatsApp**.
   - Node process launches in background, QR code appears within 3-5 seconds.
   - Scan with WhatsApp -> Pill turns **`Connected (+91...)`**.
4. **Verify Kill / Deactivation**:
   - Click **Kill / Stop WhatsApp**.
   - Node process is instantly killed, status pill returns to **`DEAD / INACTIVE`**.
   - Test printing again: Printing continues to work flawlessly.
