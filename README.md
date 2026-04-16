# Fluentia

Cross-device wireless input — use your phone as a wireless keyboard for your PC.

## Features

- **End-to-end encryption** — X25519 key exchange + XSalsa20-Poly1305, bidirectional ratchet (forward + backward secrecy)
- **QR code pairing** — scan to connect, 5-minute auto-refresh
- **Voice & text input** — real-time injection at cursor position via SendInput API
- **Clipboard sync** — send text from phone directly to PC clipboard
- **File transfer** — encrypted file sharing between devices (configurable size limit)
- **Auto-reconnect** — persistent connections with exponential backoff
- **Apple-inspired UI** — liquid glass design, light/dark theme, SVG icons
- **Privacy first** — no logs by default, optional local history on mobile, zero server-side storage

## Quick Start

### Docker (Server + Mobile Web)

```bash
docker compose up --build -d
# → http://localhost:8080
```

### Windows Client

```bash
cd client
dotnet run --project Fluentia
```

### Usage

1. Launch Windows client → QR code appears
2. Open `https://your-server/` on phone → scan QR
3. Type or speak on phone → text appears at PC cursor

## Server Configuration

All settings via environment variables in `docker-compose.yml`:

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `8080` | Server port |
| `PRIVATE_MODE` | `false` | Require secret path to connect |
| `SECRET_PATH` | — | Secret URL path (when private mode enabled) |
| `IP_WHITELIST` | `false` | Enable IP-based access control |
| `ALLOWED_IPS` | — | Comma-separated IPs/CIDRs |
| `MAX_FILE_MB` | `100` | Max file size (-1=disabled, 0=unlimited) |

## Architecture

```
Phone (React) ←—E2E encrypted—→ Go Relay (Docker) ←—E2E encrypted—→ Windows (WPF)
```

The relay server only forwards ciphertext. It cannot read any messages.

## Security

- X25519 Diffie-Hellman key exchange (NaCl `crypto_box`)
- Bidirectional symmetric ratchet with SHA-512 KDF
- Per-message key derivation, immediate key erasure
- QR-based out-of-band public key verification (anti-MITM)
- Server-side: private mode, IP whitelist, secret URL paths

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Mobile | React 18 + TypeScript + Vite + tweetnacl |
| Server | Go 1.22 + gorilla/websocket |
| Client | .NET 8 WPF + Sodium.Core + SendInput |
| Deploy | Docker multi-stage build |

## License

MIT
