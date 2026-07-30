const fs = require('fs');
const readline = require('readline');
const { makeWASocket, useMultiFileAuthState, DisconnectReason, Browsers } = require('@whiskeysockets/baileys');
const pino = require('pino');

// Parse --session-dir argument
const args = process.argv.slice(2);
const sessionDirArg = args.find(a => a.startsWith('--session-dir='));
const sessionDir = sessionDirArg ? sessionDirArg.split('=')[1] : './session';

let sock;
let isStarted = false;

// We use pino logger but set level to silent so it doesn't corrupt stdout JSON
const logger = pino({ level: 'silent' });

function sendJson(obj) {
    // Write out the JSON response on a single line
    console.log(JSON.stringify(obj));
}

async function startSession() {
    if (isStarted) return;
    isStarted = true;

    try {
        const { state, saveCreds } = await useMultiFileAuthState(sessionDir);

        sock = makeWASocket({
            auth: state,
            printQRInTerminal: false,
            logger: logger,
            browser: Browsers.windows('Desktop'),
        });

        sock.ev.on('creds.update', saveCreds);

        sock.ev.on('connection.update', (update) => {
            const { connection, lastDisconnect, qr } = update;

            if (qr) {
                // Return QR as base64. Baileys gives us a raw string, we can use qrcode library to convert to base64 data URI
                require('qrcode').toDataURL(qr, (err, url) => {
                    if (!err) {
                        // The url is something like "data:image/png;base64,iVBOR..."
                        // We strip the prefix to match the old bridge behavior if necessary, or just send it as is.
                        // Assuming C# can handle standard data URI or expects just base64 string. 
                        // The original C# assumes base64 string directly, let's strip the prefix.
                        const b64 = url.replace(/^data:image\/png;base64,/, "");
                        sendJson({ event: 'qr', qrBase64: b64 });
                    }
                });
            }

            if (connection === 'close') {
                const shouldReconnect = (lastDisconnect?.error?.output?.statusCode !== DisconnectReason.loggedOut);
                if (shouldReconnect) {
                    sendJson({ event: 'disconnected', reason: 'reconnecting' });
                    // Restart connection after 2 seconds
                    isStarted = false;
                    setTimeout(startSession, 2000);
                } else {
                    sendJson({ event: 'expired' });
                }
            } else if (connection === 'open') {
                const number = sock.user.id.split(':')[0] + '@s.whatsapp.net';
                sendJson({ event: 'connected', number: '+' + number.split('@')[0] });
            }
        });

    } catch (err) {
        sendJson({ event: 'error', message: err.message });
    }
}

async function sendText(messageLogId, to, message) {
    if (!sock) {
        sendJson({ event: 'send-result', messageLogId, success: false, error: 'Socket not initialized' });
        return;
    }
    
    // Ensure `to` is in correct jid format (e.g. 9198989898@s.whatsapp.net)
    const jid = to.replace(/[^0-9]/g, '') + '@s.whatsapp.net';

    try {
        const msg = await sock.sendMessage(jid, { text: message });
        sendJson({ event: 'send-result', messageLogId, success: true, messageId: msg.key.id });
    } catch (err) {
        sendJson({ event: 'send-result', messageLogId, success: false, error: err.message });
    }
}

async function sendDocument(messageLogId, to, caption, fileBase64, fileName) {
    if (!sock) {
        sendJson({ event: 'send-result', messageLogId, success: false, error: 'Socket not initialized' });
        return;
    }

    const jid = to.replace(/[^0-9]/g, '') + '@s.whatsapp.net';
    
    try {
        const buffer = Buffer.from(fileBase64, 'base64');
        const msg = await sock.sendMessage(jid, { 
            document: buffer, 
            mimetype: 'application/pdf', 
            fileName: fileName,
            caption: caption 
        });
        sendJson({ event: 'send-result', messageLogId, success: true, messageId: msg.key.id });
    } catch (err) {
        sendJson({ event: 'send-result', messageLogId, success: false, error: err.message });
    }
}

// Listen to stdin
const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
    terminal: false
});

rl.on('line', async (line) => {
    if (!line || line.trim() === '') return;
    
    try {
        const payload = JSON.parse(line);
        switch (payload.cmd) {
            case 'start':
                startSession();
                break;
            case 'send-text':
                await sendText(payload.messageLogId, payload.to, payload.message);
                break;
            case 'send-document':
                await sendDocument(payload.messageLogId, payload.to, payload.caption, payload.fileBase64, payload.fileName);
                break;
            case 'disconnect':
                if (sock) {
                    sock.logout();
                }
                process.exit(0);
                break;
        }
    } catch (err) {
        sendJson({ event: 'error', message: `Failed to process command: ${err.message}` });
    }
});

// Start immediately if you prefer, or wait for 'start' command from C#
// SessionManager.cs sends 'start' immediately after process launch.
