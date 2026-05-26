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

> **⚠️ 禁止手动修改版本号。** 版本号分散在 6 个文件中（package.json、types.ts、protocol.go、protocol_test.go、Fluentia.csproj、Messages.cs、README.md），手动修改极易遗漏或不一致。必须通过 CI workflow 一键更新。

### 升级版本

```bash
# 一条命令完成所有组件的版本升级
gh workflow run release.yml -f version=1.6.0
```

该 workflow 会自动：
1. 更新所有 6 个文件中的版本号
2. 更新 README.md 中的 MIN_VERSION
3. 创建一个 PR 到 main 分支

### 版本一致性检查

CI 中的 `check-version` job 会在每次 push 时自动校验所有组件版本是否一致。如果手动修改了某个文件的版本号而遗漏了其他文件，CI 会报错：

```
::error::Version mismatch detected! All components must have the same version.
```

### 为什么不能手动改？

版本号存在于以下位置，手动修改极易遗漏：

| 文件 | 字段 |
|------|------|
| `mobile/package.json` | `version` |
| `mobile/src/types.ts` | `PROTOCOL_VERSION` |
| `server/protocol.go` | `ProtocolVersion` |
| `server/protocol_test.go` | 版本断言 |
| `client/Fluentia/Fluentia.csproj` | `Version` / `AssemblyVersion` / `FileVersion` |
| `client/Fluentia/Models/Messages.cs` | `ProtocolVersion` |
| `README.md` | `MIN_VERSION` |
