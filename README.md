# Fluentia

> 跨设备无线语音输入系统 — 利用手机作为无线键盘，将语音输入实时注入到 Windows 电脑的光标处。

<p align="center">
  <strong>📱 Mobile Web → ☁️ Relay Server → 💻 Windows Client</strong>
</p>

## 架构概览

```
┌──────────────┐     WebSocket (E2E encrypted)     ┌──────────────┐
│  Mobile Web  │ ◄──────────────────────────────► │  Go Relay    │
│  (React)     │     encrypt(text_diff)            │  Server      │
│  Voice Input │                                    │  (Docker)    │
└──────────────┘                                    └──────┬───────┘
       │                                                    │
       │  Scan QR (server_url + token + public_key)        │ WebSocket
       │                                                    │
       ▼                                            ┌──────▼───────┐
  ┌──────────┐                                      │  Windows     │
  │ QR Code  │ ◄──────────────────────────────────  │  Client      │
  │ on PC    │                                      │  (C# WPF)   │
  └──────────┘                                      │  SendInput() │
                                                    └──────────────┘
```

## 安全模型

采用 **类 Signal 端到端加密** 方案：

- **密钥交换**: X25519 Diffie-Hellman（通过 NaCl `crypto_box`）
- **消息加密**: XSalsa20-Poly1305 (AEAD)
- **PC 公钥通过 QR 码传递**（带外验证，防 MITM）
- **每次刷新房间生成全新密钥对**（前向保密）
- **中继服务器无法解密数据** — 仅转发密文

```
Mobile 扫码获得 PC 公钥 ──► 生成自己的密钥对 ──► 发送公钥给 PC
                                                         │
双方计算共享密钥 = X25519(my_secret, their_public)       │
                                                         ▼
所有后续消息使用 nacl.box (XSalsa20-Poly1305) 加密   ◄──┘
```

## 组件说明

### 1. 中继服务端（Go）

- 路径: `server/`
- 功能: WebSocket 消息转发、房间(Room)管理、动态 Token
- **抢占逻辑**: 新设备扫码时立即断开旧设备
- 同时可提供 Mobile Web 静态文件服务

### 2. Mobile Web 端（React + TypeScript）

- 路径: `mobile/`
- 功能: QR 扫码连接、语音+文本输入、composition 事件精准捕获
- **液态玻璃 UI**: 参考 Apple iOS 26 设计，backdrop-filter 毛玻璃效果
- 匿名 DeviceID（localStorage 持久化）
- 输入历史记录、被抢占时的清晰提示

### 3. Windows 客户端（C# WPF .NET 8）

- 路径: `client/`
- 功能: 系统托盘常驻、QR 码生成显示、文本注入
- **SendInput API**: 将接收的字符以 Unicode 键盘事件注入到当前光标处
- 支持退格操作，适配语音修正场景

## 快速开始

### 前置要求

| 组件 | 要求 |
|------|------|
| Go | 1.22+ |
| Node.js | 20+ |
| .NET SDK | 8.0+ |
| Docker | (可选) 用于一键部署 |

### 方式一：Docker 一键部署（服务端 + Mobile Web）

```bash
# 在项目根目录
docker compose up --build -d

# 服务端运行在 http://localhost:8080
# Mobile Web 运行在 http://localhost:8080/
```

### 方式二：分别启动

#### 启动服务端

```bash
cd server
go mod tidy
go run .
# 默认监听 :8080
```

#### 启动 Mobile Web（开发模式）

```bash
cd mobile
npm install
npm run dev
# 开发服务器: http://localhost:5173
```

#### 启动 Windows 客户端

```bash
cd client
dotnet restore
dotnet run --project Fluentia
```

### 使用流程

1. **启动 Windows 客户端** — 双击 `Fluentia.exe`，主窗口弹出
   - 输入服务端 WebSocket 地址：`wss://f.106918.xyz/ws`（已部署的公网服务器）
   - 或局域网自建：`ws://192.168.x.x:8080/ws`
   - 点击 **Connect & Create Room**，等待 QR 码出现
2. **关闭窗口后最小化到托盘** — 右下角系统托盘会出现紫色圆形 **F** 图标，左键点击召回窗口
3. **手机打开 Mobile Web** — 访问 `https://f.106918.xyz/`，切换到 **Scan** 标签，扫描 PC 上显示的 QR 码
4. **开始输入** — 切换到 **Input** 标签，在任意 PC 应用中定位光标，手机端打字或使用语音输入，文字实时注入到当前光标处

> **QR 码扫描说明**: 扫码需要摄像头权限，且页面必须在 HTTPS 或 localhost 下运行。已部署的 `https://f.106918.xyz` 已满足此条件。

> ⚠️ **局域网自建 TLS**: `wss://` 要求服务端有 TLS 证书；局域网可通过 Nginx 反向代理 + Let's Encrypt，或使用 `ws://` 搭配本地 HTTP 页面（手机需关闭"安全 WebSocket"限制）。

## 通信协议

### 控制消息（客户端 ↔ 服务端，明文）

| 消息类型 | 方向 | 说明 |
|----------|------|------|
| `create_room` | PC → Server | 创建新房间 |
| `room_created` | Server → PC | 返回房间 Token |
| `join_room` | Mobile → Server | 加入房间（含 token + deviceId） |
| `joined` | Server → Mobile | 加入成功确认 |
| `peer_joined` | Server → PC | 通知手机已连接 |
| `peer_left` | Server → Both | 对端断开通知 |
| `preempted` | Server → Mobile | 被新设备抢占 |

### 数据消息（Mobile ↔ PC，E2E 加密）

| 消息类型 | 说明 |
|----------|------|
| `key_exchange` | 交换公钥 |
| `encrypted` | 加密载荷（含 payload + nonce） |

加密内部 payload 格式：

```json
{"type": "text_commit", "text": "hello"}
{"type": "backspace", "count": 3}
{"type": "composition_update", "text": "hel"}
{"type": "clear"}
```

## 项目结构

```
Fluentia/
├── CLAUDE.md              # 项目需求文档
├── README.md              # 本文件
├── Dockerfile             # 多阶段构建（Mobile + Server）
├── docker-compose.yml     # 一键部署配置
├── server/                # Go 中继服务端
│   ├── main.go            # 入口 + HTTP/WS 路由
│   ├── hub.go             # 房间管理 + 消息分发
│   ├── client.go          # WebSocket 客户端连接
│   ├── room.go            # 房间数据结构
│   ├── protocol.go        # 消息类型定义
│   └── go.mod
├── mobile/                # React Mobile Web
│   ├── src/
│   │   ├── App.tsx        # 主应用
│   │   ├── components/    # UI 组件
│   │   │   ├── Header.tsx
│   │   │   ├── InputArea.tsx
│   │   │   ├── QRScanner.tsx
│   │   │   └── History.tsx
│   │   ├── hooks/         # React Hooks
│   │   │   ├── useWebSocket.ts
│   │   │   └── useDeviceId.ts
│   │   ├── utils/         # 工具函数
│   │   │   ├── crypto.ts  # NaCl 加密
│   │   │   └── diff.ts    # 文本 Diff
│   │   ├── styles/
│   │   │   └── glass.css  # 液态玻璃样式
│   │   └── types.ts       # TypeScript 类型
│   ├── package.json
│   └── vite.config.ts
└── client/                # C# Windows 客户端
    ├── Fluentia.sln
    └── Fluentia/
        ├── MainWindow.xaml      # 主窗口 UI
        ├── MainWindow.xaml.cs   # 窗口逻辑
        ├── App.xaml             # 应用资源 + 主题
        ├── Services/
        │   ├── WebSocketService.cs  # WS 客户端
        │   ├── CryptoService.cs     # NaCl 加密
        │   ├── TextInjector.cs      # SendInput 文本注入
        │   └── RoomManager.cs       # 房间生命周期管理
        └── Models/
            └── Messages.cs          # 消息模型
```

## 技术栈

| 层 | 技术 | 加密 |
|----|------|------|
| Mobile Web | React 18 + TypeScript + Vite | `tweetnacl` (NaCl) |
| Relay Server | Go 1.22 + gorilla/websocket | 仅转发密文 |
| Windows Client | .NET 8 WPF + SendInput | `Sodium.Core` (libsodium) |
| 部署 | Docker multi-stage build | TLS (可选) |

## License

MIT
