# BILLIT Device Agent (WhatsApp & POS Hardware Connector)

A high-performance, offline-first Windows background service for retail and POS counters.

## Key Features
- **Independent POS Printing**:
  - 100% offline-ready. Starts on port `5050` with zero cloud or WhatsApp dependencies.
  - Silent thermal PDF printing via SumatraPDF (`80mm` and `58mm` roll optimization).
  - Raw ESC/POS Windows Spooler integration: cash drawer kick (`\x1Bp\x00`) and paper cut (`\x1DVA\x03`).
  - Concurrency queue (`SemaphoreSlim`) preventing spooler collisions during rapid billing.
- **Flexible WhatsApp Modes**:
  - **Disabled**: POS printing only. 0 Node.js processes.
  - **LocalBridge**: Managed Baileys bridge for WhatsApp Web linking via live QR streaming with self-healing 401 session recovery.
  - **CloudApi**: Direct Meta WhatsApp Business Cloud API integration.
- **Embedded Diagnostic Dashboard**:
  - Open `http://localhost:5050/` in any browser to inspect printers, click "Print Test Page", "Open Drawer", and view live streaming logs.
- **Windows Service CLI**:
  - `BILLIT_WhatsAppConnector.exe --install`
  - `BILLIT_WhatsAppConnector.exe --uninstall`
  - `BILLIT_WhatsAppConnector.exe --start` / `--stop` / `--status`
  - `BILLIT_WhatsAppConnector.exe --setup`

For complete architecture, SignalR contracts, and deployment runbook, see [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md).
