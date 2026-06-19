# Fluentia

Cross-device wireless input — use your phone as a wireless keyboard for your PC.

## Features

- **End-to-end encryption** — X25519 key exchange + XSalsa20-Poly1305, bidirectional ratchet (forward + backward secrecy)
- **QR code pairing** — scan to connect, with reusable sessions (up to 7 days)
- **Voice & text input** — real-time injection at cursor position via SendInput API
- **Clipboard sync** — send text from phone directly to PC clipboard
- **File transfer** — encrypted file sharing between devices (configurable size limit)
- **Auto-reconnect** — persistent connections with exponential backoff, offline input buffering, desktop session recovery
- **BLE-only fallback** — when relay server unreachable but Bluetooth paired, input continues over BLE
- **Apple-inspired UI** — liquid glass design, light/dark theme, SVG icons
- **Privacy first** — no logs by default, optional local history on mobile, zero server-side storage

## Architecture

```
Phone (React + tweetnacl) ←—E2E encrypted—→ Go Relay (Docker) ←—E2E encrypted—→ Windows (WPF + NSec)
```

The relay server only forwards ciphertext. It cannot read any messages.

### Component Stack

| Component | Technology | Location |
|-----------|-----------|----------|
| Mobile | React 18 + TypeScript + Vite + tweetnacl | `mobile/` |
| Server | Go 1.22 + gorilla/websocket | `server/` |
| Client | .NET 8 WPF + Sodium.Core (NSec) + SendInput | `client/` |

### Security Model

- **Key exchange**: X25519 Diffie-Hellman (NaCl `crypto_box`)
- **Symmetric encryption**: XSalsa20-Poly1305 (SecretBox)
- **KDF**: HKDF-SHA-512 (both mobile and client use identical implementation)
  - Salt: `fluentia_v1_salt`
  - Info: `fluentia_chain_v1`
  - Output: 32 bytes
- **Ratchet**: Bidirectional symmetric ratchet — separate send/receive chain keys
  - PC→Mobile ratchet provides backward security (new keys can't derive old ones)
  - Mobile→PC ratchet provides forward security
- **Anti-MITM**: QR-based out-of-band public key verification
- **Key erasure**: Per-message key derivation, immediate key erasure after use
- **Server-side**: Private mode, IP whitelist, secret URL paths

### Crypto Flow

```
1. key_exchange      → Mobile sends X25519 public key via QR
2. ratchet_init      → Mobile generates seed, derives chain key via HKDF, sends seed to PC
3. pc_ratchet_init   → PC derives its own send ratchet for backward security
4. handshake_ack     → Both sides confirm ratchet ready
5. [encrypted messages with per-message key derivation]
```

## Version Management

Version is tracked consistently across **5 files**:

| File | Field |
|------|-------|
| `mobile/package.json` | `"version"` |
| `mobile/src/types.ts` | `PROTOCOL_VERSION` |
| `server/protocol.go` | `ProtocolVersion` |
| `client/Fluentia/Fluentia.csproj` | `<Version>`, `<AssemblyVersion>`, `<FileVersion>` |
| `client/Fluentia/Models/Messages.cs` | `ProtocolVersion` |

**Current version: 1.7.8**

CI enforces version consistency across all files. The `release.yml` workflow automates version bumping via PR.

## CI/CD Pipeline

Three GitHub Actions workflows:

### 1. `ci.yml` — Continuous Integration

Triggers on push/PR to `main`. Path-filtered to only run relevant jobs:

- **changes** — Detects which components changed (server/mobile/client)
- **check-version** — Verifies version consistency across all 5 files
- **heartbeat-invariants** — Ensures heartbeat timing constraints (timeout > interval)
- **lint-go / lint-mobile / lint-client** — Per-component linting
- **test-mobile** (289 tests) / **test-go** / **test-client** (112 tests)
- **build-docker** / **build-mobile** / **build-client**
- **security** — Dependency audit

### 2. `release.yml` — Version Bump

Manual workflow dispatch. Input: version string (e.g., `1.7.8`).

- Bumps version in all 5 files + README MIN_VERSION table
- Creates PR via `peter-evans/create-pull-request`

### 3. `publish.yml` — Build & Release

Triggers on `v*` tag push. Builds all artifacts:

- Docker image → `ghcr.io/wxyu71/fluentia:<version>` (no `v` prefix in tag)
- Windows installer: `Fluentia-Setup.exe` (Velopack)
- Portable ZIP
- Server binaries: linux/darwin/windows × amd64/arm64

## Auto-Update System (Windows Client)

Uses **Velopack 1.2.0** with `GithubSource` pointing to `https://github.com/wxyu71/Fluentia`.

- **Auto-check**: Silent check on startup (`AutoCheckForUpdatesAsync`)
- **Manual check**: User-triggered via menu (`ManualCheckForUpdatesAsync`)
- **Limitation**: `UpdateManager.IsInstalled` returns `false` for portable/ZIP users — only `Fluentia-Setup.exe` installer users can auto-update
- **Error messaging**: Clear bilingual (EN/CN) message when portable user attempts update

## Quick Start

### Docker (Server + Mobile Web)

```bash
docker compose up --build -d
# → http://localhost:8080
```

The relay server is intended to run on a separate server, not on the Windows desktop client machine.

### Windows Client

```bash
cd client
dotnet run --project Fluentia
```

### Usage

1. Launch Windows client → QR code appears
2. Open `https://your-server/` on phone → scan QR
3. Type or speak on phone → text appears at PC cursor

The Windows client does not auto-start or auto-discover a local relay. Enter the deployed server's WebSocket address explicitly before connecting.

## Server Configuration

Environment variables in `docker-compose.yml`:

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `8080` | Server port |
| `PRIVATE_MODE` | `false` | Require secret path to connect |
| `SECRET_PATH` | — | Secret URL path (when private mode enabled) |
| `IP_WHITELIST` | `false` | Enable IP-based access control |
| `ALLOWED_IPS` | — | Comma-separated IPs/CIDRs |
| `ALLOW_EMPTY_ORIGIN` | `true` | Allow WebSocket connections without Origin header (required for native desktop clients) |
| `MIN_VERSION` | `1.7.10` | Minimum compatible client version enforced after handshake |
| `MAX_FILE_MB` | `100` | Max file size (-1=disabled, 0=unlimited) |
| `SESSION_MAX_AGE_DAYS` | `7` | How long a session token remains reusable |
| `SESSION_STORE_PATH` | `./data/sessions.json` | JSON file for session persistence across restarts |
| `MOBILE_EXPIRY_SECS` | `60` | Desktop wait time before surfacing after phone disconnects |

Desktop session state is stored locally at `%AppData%/Fluentia/` and protected with the current Windows user account.

## Deployment (Production)

Production runs as a Podman Quadlet container on the `ccbig` server:

- **Image**: `ghcr.io/wxyu71/fluentia:1.7.8`
- **Domain**: `f.106918.xyz`
- **Port**: `127.0.0.1:8778`
- **Data**: `~/data/fluentia/` → `/app/data`
- **Database**: PostgreSQL `fluentia` on `postgres` host
- **Env file**: `quadlets/env/fluentia.env.enc` (SOPS encrypted)
- **Caddy**: Reverse proxy with Cloudflare DNS-01

### Tag Format

GHCR tags have **NO `v` prefix** (e.g., `1.7.8`, not `v1.7.8`). Renovate handles this correctly.

## Known Pitfalls

1. **ALLOW_EMPTY_ORIGIN=true required**: .NET `ClientWebSocket` does NOT send Origin headers. Server rejects empty Origin by default → desktop client cannot connect without this env var.

2. **Heartbeat timing**: `HEARTBEAT_TIMEOUT_MS` must be > `HEARTBEAT_INTERVAL_MS`. Historical bug: timeout (2500ms) < interval (3000ms) caused constant reconnection. Fixed: timeout now 8000ms.

3. **KDF mismatch**: Mobile (tweetnacl) and Client (NSec) must use identical HKDF parameters. Historical bug: mobile used `SHA-512(key||label)[0:32]` while client used `HKDF.DeriveKey(SHA512, ...)`. Fixed in v1.7.5.

4. **Portable vs Installer**: Velopack auto-update only works with `Fluentia-Setup.exe` installer. Portable/ZIP users see a clear error message.

5. **Browser cache**: After server upgrade, mobile PWA may show "version mismatch" banner. Server sends `Cache-Control: no-cache` headers; client clears Service Worker cache on protocol mismatch.

## License

MIT
