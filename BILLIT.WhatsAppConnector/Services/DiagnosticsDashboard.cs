namespace BILLIT.WhatsAppConnector.Services
{
    public static class DiagnosticsDashboard
    {
        public const string HtmlContent = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>BILLIT Device Agent — Local Control Panel</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet">
    <style>
        :root {
            --bg: #090d16;
            --surface: rgba(19, 27, 44, 0.7);
            --surface-border: rgba(255, 255, 255, 0.08);
            --primary: #3b82f6;
            --primary-glow: rgba(59, 130, 246, 0.25);
            --success: #10b981;
            --success-glow: rgba(16, 185, 129, 0.25);
            --warning: #f59e0b;
            --danger: #ef4444;
            --text: #f8fafc;
            --text-muted: #94a3b8;
            --radius: 16px;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            font-family: 'Plus Jakarta Sans', sans-serif;
        }

        body {
            background-color: var(--bg);
            background-image: 
                radial-gradient(circle at 10% 20%, rgba(59, 130, 246, 0.15), transparent 30%),
                radial-gradient(circle at 90% 80%, rgba(16, 185, 129, 0.12), transparent 35%);
            color: var(--text);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            align-items: center;
            padding: 40px 20px;
        }

        .container {
            max-width: 960px;
            width: 100%;
        }

        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 36px;
            padding-bottom: 20px;
            border-bottom: 1px solid var(--surface-border);
        }

        .brand {
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .brand-logo {
            width: 44px;
            height: 44px;
            background: linear-gradient(135deg, #2563eb, #38bdf8);
            border-radius: 12px;
            display: flex;
            align-items: center;
            justify-content: center;
            font-weight: 800;
            font-size: 20px;
            box-shadow: 0 8px 24px var(--primary-glow);
        }

        .brand-text h1 {
            font-size: 20px;
            font-weight: 700;
            letter-spacing: -0.5px;
        }

        .brand-text p {
            font-size: 13px;
            color: var(--text-muted);
        }

        .badge {
            padding: 6px 14px;
            border-radius: 20px;
            font-size: 12px;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            display: inline-flex;
            align-items: center;
            gap: 6px;
        }

        .badge-live {
            background: rgba(16, 185, 129, 0.15);
            color: var(--success);
            border: 1px solid rgba(16, 185, 129, 0.3);
        }

        .badge-pulse {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: var(--success);
            box-shadow: 0 0 10px var(--success);
            animation: pulse 2s infinite;
        }

        @keyframes pulse {
            0% { opacity: 1; transform: scale(1); }
            50% { opacity: 0.4; transform: scale(1.3); }
            100% { opacity: 1; transform: scale(1); }
        }

        .grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
            gap: 24px;
            margin-bottom: 24px;
        }

        .card {
            background: var(--surface);
            backdrop-filter: blur(16px);
            border: 1px solid var(--surface-border);
            border-radius: var(--radius);
            padding: 24px;
            display: flex;
            flex-direction: column;
            box-shadow: 0 16px 36px rgba(0, 0, 0, 0.25);
            transition: transform 0.2s ease, border-color 0.2s ease;
        }

        .card:hover {
            border-color: rgba(255, 255, 255, 0.15);
        }

        .card-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 18px;
        }

        .card-title {
            font-size: 16px;
            font-weight: 600;
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .icon {
            font-size: 20px;
        }

        .card-body {
            flex: 1;
            font-size: 14px;
            color: var(--text-muted);
            line-height: 1.6;
        }

        .data-row {
            display: flex;
            justify-content: space-between;
            padding: 8px 0;
            border-bottom: 1px solid rgba(255, 255, 255, 0.04);
        }

        .data-label {
            color: var(--text-muted);
        }

        .data-value {
            color: var(--text);
            font-weight: 500;
            font-family: 'JetBrains Mono', monospace;
            font-size: 13px;
        }

        .card-footer {
            margin-top: 20px;
            padding-top: 16px;
            border-top: 1px solid rgba(255, 255, 255, 0.05);
            display: flex;
            gap: 8px;
            flex-wrap: wrap;
        }

        button, select {
            background: rgba(255, 255, 255, 0.05);
            border: 1px solid var(--surface-border);
            color: var(--text);
            padding: 10px 14px;
            border-radius: 10px;
            font-size: 13px;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.2s ease;
            outline: none;
        }

        button:hover {
            background: rgba(255, 255, 255, 0.1);
            border-color: rgba(255, 255, 255, 0.2);
        }

        button.btn-primary {
            background: linear-gradient(135deg, #2563eb, #1d4ed8);
            border-color: #3b82f6;
            box-shadow: 0 4px 14px var(--primary-glow);
            color: white;
            flex: 1;
        }

        button.btn-primary:hover {
            background: linear-gradient(135deg, #3b82f6, #2563eb);
            transform: translateY(-1px);
        }

        button.btn-secondary {
            background: rgba(255, 255, 255, 0.06);
            border-color: rgba(255, 255, 255, 0.12);
        }

        .qr-box {
            background: white;
            padding: 14px;
            border-radius: 12px;
            display: inline-block;
            margin: 16px auto;
            text-align: center;
        }

        .qr-box img {
            width: 180px;
            height: 180px;
            display: block;
        }

        .status-pill {
            display: inline-block;
            padding: 3px 10px;
            border-radius: 12px;
            font-size: 12px;
            font-weight: 600;
        }

        .pill-connected { background: rgba(16, 185, 129, 0.15); color: #34d399; }
        .pill-disconnected { background: rgba(239, 68, 68, 0.15); color: #f87171; }
        .pill-pending { background: rgba(245, 158, 11, 0.15); color: #fbbf24; }
        .pill-disabled { background: rgba(148, 163, 184, 0.15); color: #94a3b8; }

        #alert-box {
            display: none;
            margin-bottom: 20px;
            padding: 14px 20px;
            border-radius: 12px;
            font-size: 14px;
            font-weight: 500;
        }

        .alert-success { background: rgba(16, 185, 129, 0.2); border: 1px solid var(--success); color: #a7f3d0; }
        .alert-error { background: rgba(239, 68, 68, 0.2); border: 1px solid var(--danger); color: #fecaca; }

        .log-viewer {
            background: #050811;
            border: 1px solid var(--surface-border);
            border-radius: 12px;
            padding: 16px;
            font-family: 'JetBrains Mono', monospace;
            font-size: 12px;
            line-height: 1.5;
            color: #cbd5e1;
            max-height: 260px;
            overflow-y: auto;
            white-space: pre-wrap;
            word-break: break-all;
            margin-top: 14px;
        }
    </style>
</head>
<body>
    <div class="container">
        <header>
            <div class="brand">
                <div class="brand-logo">B</div>
                <div class="brand-text">
                    <h1>BILLIT Device Agent</h1>
                    <p>Independent Printing & Multi-Scenario Messaging Hub</p>
                </div>
            </div>
            <div class="badge badge-live">
                <div class="badge-pulse"></div>
                Service Active: Port 5050
            </div>
        </header>

        <div id="alert-box"></div>

        <div class="grid">
            <!-- Printing Subsystem Card -->
            <div class="card">
                <div class="card-header">
                    <div class="card-title">
                        <span class="icon">🖨️</span>
                        <span>POS Printing Engine</span>
                    </div>
                    <span class="status-pill pill-connected">100% Offline Ready</span>
                </div>
                <div class="card-body">
                    <p style="margin-bottom: 14px;">Silent POS printing runs directly on this machine via Windows Spooler and SumatraPDF. Independent of WhatsApp or internet connectivity.</p>
                    
                    <div class="data-row">
                        <span class="data-label">Default Printer</span>
                        <span class="data-value" id="val-default-printer">Detecting...</span>
                    </div>
                    <div class="data-row">
                        <span class="data-label">Installed Printers</span>
                        <span class="data-value" id="val-printer-count">0 found</span>
                    </div>
                    <div class="data-row">
                        <span class="data-label">Direct API</span>
                        <span class="data-value">POST /api/print</span>
                    </div>

                    <div style="margin-top: 14px;">
                        <label style="font-size: 12px; color: var(--text-muted); display: block; margin-bottom: 6px;">Target Printer:</label>
                        <select id="printer-select" style="width: 100%;"></select>
                    </div>
                </div>
                <div class="card-footer">
                    <button class="btn-primary" onclick="printTestReceipt()">Print Test Page</button>
                    <button class="btn-secondary" onclick="openDrawer()">Open Drawer</button>
                    <button class="btn-secondary" onclick="cutPaper()">Cut Paper</button>
                </div>
            </div>

            <!-- WhatsApp Subsystem Card -->
            <div class="card">
                <div class="card-header">
                    <div class="card-title">
                        <span class="icon">💬</span>
                        <span>WhatsApp Engine</span>
                    </div>
                    <span class="status-pill" id="wa-pill">Checking...</span>
                </div>
                <div class="card-body">
                    <div class="data-row">
                        <span class="data-label">Active Mode</span>
                        <span class="data-value" id="val-wa-mode">-</span>
                    </div>
                    <div class="data-row">
                        <span class="data-label">Linked Number</span>
                        <span class="data-value" id="val-wa-number">None</span>
                    </div>
                    
                    <div style="margin-top: 14px;">
                        <label style="font-size: 12px; color: var(--text-muted); display: block; margin-bottom: 6px;">Change Mode:</label>
                        <select id="mode-select" onchange="changeMode(this.value)" style="width: 100%;">
                            <option value="Disabled">Disabled (Printing Only - Zero Overhead)</option>
                            <option value="LocalBridge">Local Bridge (Baileys / Scan QR)</option>
                            <option value="CloudApi">Meta WhatsApp Cloud API (Official)</option>
                        </select>
                    </div>

                    <div id="qr-container" style="display: none; text-align: center;">
                        <p style="font-size: 12px; margin-top: 10px; color: var(--warning);">Scan QR Code with WhatsApp (Linked Devices):</p>
                        <div class="qr-box">
                            <img id="qr-image" src="" alt="WhatsApp QR Code">
                        </div>
                    </div>

                    <div id="wa-error" style="display:none; margin-top: 10px; font-size: 12px; color: var(--danger);"></div>
                </div>
                <div class="card-footer">
                    <button id="btn-wa-toggle" class="btn-primary" onclick="toggleWhatsApp()">Start / Reconnect</button>
                </div>
            </div>
        </div>

        <!-- Cloud Hub Status & Recent Logs Card -->
        <div class="card">
            <div class="card-header">
                <div class="card-title">
                    <span class="icon">📋</span>
                    <span>System Status & Diagnostics</span>
                </div>
                <button class="btn-secondary" onclick="toggleLogs()" style="width: auto; padding: 6px 14px; font-size: 12px;">Toggle Live Logs</button>
            </div>
            <div class="card-body">
                <div class="data-row">
                    <span class="data-label">Cloud Hub Relay</span>
                    <span class="data-value" id="val-hub-url">-</span>
                </div>
                <div class="data-row">
                    <span class="data-label">Service Mode</span>
                    <span class="data-value" id="val-service-mode">Port 5050 (Active)</span>
                </div>

                <div id="log-container" style="display: none;">
                    <div style="display: flex; justify-content: space-between; align-items: center; margin-top: 14px;">
                        <span style="font-size: 12px; color: var(--text-muted);">Recent Server Logs (Auto-Refreshing):</span>
                        <button class="btn-secondary" onclick="fetchLogs()" style="width: auto; padding: 4px 10px; font-size: 11px;">Refresh</button>
                    </div>
                    <div class="log-viewer" id="log-viewer">Loading logs...</div>
                </div>
            </div>
        </div>
    </div>

    <script>
        let logsVisible = false;

        async function fetchStatus() {
            try {
                const res = await fetch('/api/status');
                if (!res.ok) return;
                const data = await res.json();

                // Printers
                document.getElementById('val-default-printer').innerText = data.printing.defaultPrinter || 'None';
                document.getElementById('val-printer-count').innerText = data.printing.printers.length + ' detected';

                const select = document.getElementById('printer-select');
                if (select.children.length === 0) {
                    data.printing.printers.forEach(p => {
                        const opt = document.createElement('option');
                        opt.value = p;
                        opt.innerText = p + (p === data.printing.defaultPrinter ? ' (Default)' : '');
                        select.appendChild(opt);
                    });
                }

                // WhatsApp
                const wa = data.whatsapp;
                document.getElementById('val-wa-mode').innerText = wa.mode;
                document.getElementById('val-wa-number').innerText = wa.connectedNumber || 'None';
                document.getElementById('mode-select').value = wa.mode;

                const waPill = document.getElementById('wa-pill');
                waPill.className = 'status-pill';
                if (wa.state === 'Connected') {
                    waPill.classList.add('pill-connected');
                    waPill.innerText = 'Connected';
                } else if (wa.state === 'PendingQr') {
                    waPill.classList.add('pill-pending');
                    waPill.innerText = 'Scan QR';
                } else if (wa.state === 'Disabled') {
                    waPill.classList.add('pill-disabled');
                    waPill.innerText = 'Disabled';
                } else {
                    waPill.classList.add('pill-disconnected');
                    waPill.innerText = wa.state;
                }

                const qrContainer = document.getElementById('qr-container');
                if (wa.state === 'PendingQr' && wa.qrCodeBase64) {
                    qrContainer.style.display = 'block';
                    document.getElementById('qr-image').src = 'data:image/png;base64,' + wa.qrCodeBase64;
                } else {
                    qrContainer.style.display = 'none';
                }

                const errBox = document.getElementById('wa-error');
                if (wa.errorMessage) {
                    errBox.style.display = 'block';
                    errBox.innerText = wa.errorMessage;
                } else {
                    errBox.style.display = 'none';
                }

                // Cloud Hub
                document.getElementById('val-hub-url').innerText = data.cloudHub.isConfigured ? data.cloudHub.url : 'Local Standalone Mode';

                if (logsVisible) fetchLogs();
            } catch (err) {
                console.warn('Status poll failed', err);
            }
        }

        async function printTestReceipt() {
            const printer = document.getElementById('printer-select').value;
            showAlert('Sending test receipt to ' + (printer || 'default printer') + '...', 'alert-success');
            try {
                const res = await fetch('/api/print/test', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ printerName: printer })
                });
                const result = await res.json();
                if (result.success) {
                    showAlert('✓ Test receipt printed successfully!', 'alert-success');
                } else {
                    showAlert('Print test failed: ' + result.error, 'alert-error');
                }
            } catch (err) {
                showAlert('Error sending print request: ' + err.message, 'alert-error');
            }
        }

        async function openDrawer() {
            const printer = document.getElementById('printer-select').value;
            try {
                const res = await fetch('/api/print/drawer', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ printerName: printer })
                });
                const result = await res.json();
                if (result.success) {
                    showAlert('✓ Cash drawer pulse sent!', 'alert-success');
                } else {
                    showAlert('Drawer kick error: ' + result.error, 'alert-error');
                }
            } catch (err) {
                showAlert('Error: ' + err.message, 'alert-error');
            }
        }

        async function cutPaper() {
            const printer = document.getElementById('printer-select').value;
            try {
                const res = await fetch('/api/print/cut', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ printerName: printer })
                });
                const result = await res.json();
                if (result.success) {
                    showAlert('✓ Paper cut command sent!', 'alert-success');
                } else {
                    showAlert('Paper cut error: ' + result.error, 'alert-error');
                }
            } catch (err) {
                showAlert('Error: ' + err.message, 'alert-error');
            }
        }

        async function changeMode(newMode) {
            showAlert('Switching WhatsApp mode to ' + newMode + '...', 'alert-success');
            try {
                await fetch('/api/whatsapp/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ mode: newMode })
                });
                await fetchStatus();
            } catch (err) {
                showAlert('Failed to switch mode: ' + err.message, 'alert-error');
            }
        }

        async function toggleWhatsApp() {
            try {
                await fetch('/api/whatsapp/start', { method: 'POST' });
                showAlert('Connecting WhatsApp...', 'alert-success');
                await fetchStatus();
            } catch (err) {
                showAlert('Error: ' + err.message, 'alert-error');
            }
        }

        async function fetchLogs() {
            try {
                const res = await fetch('/api/logs/recent');
                if (res.ok) {
                    const text = await res.text();
                    document.getElementById('log-viewer').innerText = text || 'No logs recorded yet.';
                    const v = document.getElementById('log-viewer');
                    v.scrollTop = v.scrollHeight;
                }
            } catch (e) { }
        }

        function toggleLogs() {
            logsVisible = !logsVisible;
            document.getElementById('log-container').style.display = logsVisible ? 'block' : 'none';
            if (logsVisible) fetchLogs();
        }

        function showAlert(msg, className) {
            const box = document.getElementById('alert-box');
            box.className = className;
            box.innerText = msg;
            box.style.display = 'block';
            setTimeout(() => { box.style.display = 'none'; }, 5000);
        }

        fetchStatus();
        setInterval(fetchStatus, 3000);
    </script>
</body>
</html>
""";
    }
}
