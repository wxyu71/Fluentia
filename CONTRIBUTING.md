# Contributing to Fluentia

## Commit Message Format

We use [Conventional Commits](https://www.conventionalcommits.org/). All commit messages must follow this format:

```
<type>(<scope>): <subject>

[optional body - Chinese or English]

[optional footer]
```

### Types

| Type | Description |
|------|-------------|
| `feat` | New feature |
| `fix` | Bug fix |
| `chore` | Maintenance tasks (deps, config, build) |
| `refactor` | Code restructuring without behavior change |
| `test` | Adding or updating tests |
| `docs` | Documentation changes |
| `ci` | CI/CD configuration changes |
| `style` | Code style (formatting, linting) |
| `perf` | Performance improvements |
| `revert` | Reverting a previous commit |

### Scopes

`server`, `mobile`, `client`, `shared`, `ci`

### Examples

```
feat(mobile): add BLE fallback for offline input delivery
```

```
fix(server): handle session expiry race condition in handleRejoinSession

当 PC 断线后 session 过期，rejoin 时应该返回明确的错误而不是 panic。

Closes #42
```

```
chore(ci): add GitHub Actions lint workflow
```

## Branch Strategy

We use **trunk-based development**:

- `main` is always deployable
- Feature branches: `feat/short-description` or `fix/short-description`
- Keep branches short-lived (merge within a few days)
- All changes go through PR to `main`

## Pull Request Checklist

Before submitting a PR, ensure:

- [ ] Commit messages follow Conventional Commits format
- [ ] Code passes all linters (`golangci-lint`, `eslint`, `dotnet format`)
- [ ] New code has test coverage
- [ ] No secrets or credentials in the code
- [ ] Version is bumped if this is a protocol change (use the release workflow)

## Development Setup

### Prerequisites

- **Go 1.22+** (server)
- **Node.js 20+** (mobile)
- **.NET 8.0 SDK** (client)
- **Docker** (optional, for containerized deployment)

### Running Locally

```bash
# Server
cd server && go run .

# Mobile (dev server)
cd mobile && npm install && npm run dev

# Client (Visual Studio or dotnet)
cd client && dotnet run
```

### Running Tests

```bash
# Server
cd server && go test -v -race ./...

# Mobile
cd mobile && npm test

# Client
cd client && dotnet test
```

### Pre-commit Hooks

Install [lefthook](https://github.com/evilmartians/lefthook):

```bash
npm install -g @evilmartians/lefthook
lefthook install
```

This sets up automatic formatting and linting on commit.

## Project Structure

```
Fluentia/
  server/        Go relay server (WebSocket relay, E2E encrypted)
  mobile/        React mobile web frontend (PWA)
  client/        Windows WPF desktop client
  docs/          Documentation
```

## Dual-Channel Architecture (BLE + WebSocket)

Fluentia supports simultaneous BLE and WebSocket transport. The routing is health-score-based:

**Key principle:** Encrypt once, route to best channel. Never encrypt the same message twice.

**Message routing rules:**
- **Input messages** (diff, enter, backspace): prefer BLE for low latency
- **File transfer** (file_start, file_chunk): always use WS (bandwidth)
- **Handshake** (key_exchange, ratchet_init): single most reliable channel
- **Control** (clipboard, ble_auth): best available

**Health scoring (0-100):**
- BLE: RSSI, consecutive failures, battery level
- WS: heartbeat RTT, connection state, battery level
- Battery <20%: prefer BLE (lower power), <10%: BLE only

**Key files:**
- `mobile/src/utils/transportHealth.ts` — Mobile health monitor
- `mobile/src/services/bleTransport.ts` — Mobile BLE transport
- `client/Fluentia/Services/DesktopTransportHealth.cs` — Desktop health monitor
- `client/Fluentia/Services/BleTransport.cs` — Desktop BLE transport
- `client/Fluentia/Services/RoomManager.cs` — Desktop dual-transport routing

**Testing:**
- `mobile/src/utils/dualChannelRouting.test.ts` — E2E routing smoke tests
- `client/Fluentia.Tests/DesktopTransportHealthTests.cs` — Desktop health tests

## Version Management

Version is managed via the GitHub Actions `release` workflow. Do not manually edit version numbers. Use:

1. Go to Actions > Release > Run workflow
2. Enter the new version (e.g., `1.6.0`)
3. The workflow creates a PR with all version bumps
