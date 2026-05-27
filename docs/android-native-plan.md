# Fluentia 原生 Android 应用开发计划

> **协议版本:** 1.7.0 | **目标平台:** API 35 / **最低 API 34** (Android 14) | **语言:** Kotlin | **分发:** APK (GitHub Release)

---

## 一、项目目标与背景

Fluentia 当前移动端为 React PWA，存在以下痛点：
- 浏览器环境中 NaCl 加密操作开销大，发热明显
- 后台生命周期不稳定，BLE 和 WebSocket 在页面挂起后频繁断开
- Web Bluetooth 权限模型脆弱，依赖浏览器 UI

原生 Android 应用的核心价值：**后台 BLE 连接稳定性 + 低功耗加密**。

> **PWA 与原生共存：** 网页版（PWA）继续保留并维护，Android 原生版本作为并行的高级选项。两者共享核心协议层（加密、消息格式、Diff 算法），但 UI 和平台特定逻辑各自独立。用户可按需选择使用网页版或原生版。

---

## 二、架构设计

### 分层架构

```
┌─────────────────────────────────────────────────┐
│                   UI 层 (Compose)                 │
│  InputScreen / HistoryScreen / SettingsScreen     │
│  ScannerScreen / BlePairingScreen                 │
├─────────────────────────────────────────────────┤
│                ViewModel 层                       │
│  ConnectionViewModel / InputViewModel             │
│  FileTransferViewModel / SettingsViewModel        │
├─────────────────────────────────────────────────┤
│               Service 层                          │
│  ┌─────────────┐  ┌──────────────┐  ┌──────────┐│
│  │ WebSocket    │  │ BLE Transport│  │ Foreground││
│  │ Manager      │  │ Manager      │  │ Service   ││
│  └─────────────┘  └──────────────┘  └──────────┘│
│  ┌─────────────┐  ┌──────────────┐              │
│  │ Transport    │  │ Crypto       │              │
│  │ Health       │  │ Service      │              │
│  │ Monitor      │  │              │              │
│  └─────────────┘  └──────────────┘              │
├─────────────────────────────────────────────────┤
│                Data 层                            │
│  SessionStore / DeviceIdStore / HistoryStore      │
│  CryptoSessionStore / SettingsStore               │
└─────────────────────────────────────────────────┘
```

### 层职责

| 层 | 职责 | Android 组件 |
|---|---|---|
| UI | 渲染界面、手势处理、动画 | Jetpack Compose + Material 3 |
| ViewModel | 状态管理、业务逻辑协调 | AndroidX ViewModel + StateFlow |
| Service | 网络/蓝牙通信、加密、前台服务 | Kotlin coroutines + Service |
| Data | 持久化存储 | DataStore / Room |

### 与现有端的对应关系

| 现有 PWA 模块 | Android 对应 | 备注 |
|---|---|---|
| `useWebSocket.ts` | `WebSocketManager` | 重写为协程驱动 |
| `crypto.ts` | `CryptoService` | Kotlin 移植，算法完全一致 |
| `bleTransport.ts` | `BleTransportManager` | 使用 Android BLE API |
| `transportHealth.ts` | `TransportHealthMonitor` | 逻辑移植 |
| `diff.ts` | `DiffEngine` | 算法移植，使用 ICU grapheme |
| `InputArea.tsx` | `InputViewModel` + Compose | 状态管理用 StateFlow |
| `useBlePairing.ts` | `BlePairingManager` | 使用 Android GATT API |
| `fileChunk.worker.ts` | Kotlin coroutine dispatcher | 不需要 Worker，用 IO dispatcher |

---

## 三、协议兼容性清单

以下部分必须与现有 TypeScript/Go 实现 **严格一致**，任何偏差都会导致互操作失败。

### 3.1 加密协议（必须完全一致）

| 组件 | 实现细节 |
|---|---|
| **密钥交换** | X25519，公钥 base64 编码，`key_exchange` 消息 |
| **对称加密** | XSalsa20-Poly1305（`nacl.box` 用于初始，`nacl.secretbox` 用于棘轮） |
| **KDF** | SHA-512 前 32 字节：`hash(key ‖ label)` |
| **棘轮标签** | 发送链：`'fluentia_chain_v1'` 初始化，`'msg'` 派生消息密钥，`'chain'` 派生下一链密钥 |
| **双向棘轮** | 独立的 send/recv 链，每条消息推进一步 |
| **消息密钥销毁** | 使用后 `fill(0)` |
| **序列号** | `seq` 字段（整数），用于乱序消息处理 |
| **回退逻辑** | `expectedSeq < seq` 时快速推进 recv 链 |

### 3.2 消息格式（必须完全一致）

**外层传输消息（WsMessage）：**
```json
{
  "type": "join_session|joined|peer_joined|peer_left|key_exchange|encrypted|ping|pong|preempted|error|device_code_join|device_code_pending",
  "token": "hex string",
  "deviceId": "hex string",
  "role": "pc|mobile",
  "publicKey": "base64",
  "payload": "base64",
  "nonce": "base64",
  "seq": 0,
  "version": "1.7.0",
  "error": "string",
  "approved": true,
  "deviceCode": "8-char alphanumeric",
  "verifyId": "string",
  "userAgent": "string"
}
```

**内层加密命令（InputCommand）：**
```json
{"type": "diff", "count": 3, "text": "hello"}
{"type": "enter"}
{"type": "backspace", "count": 1}
{"type": "clear"}
{"type": "clipboard", "text": "..."}
{"type": "regex_config", "text": "markdown..."}
{"type": "ratchet_init", "seed": "base64"}
{"type": "pc_ratchet_init", "seed": "base64"}
{"type": "handshake_ack"}
{"type": "ble_auth", "publicKey": "base64"}
{"type": "ble_auth_ok", "publicKey": "base64"}
{"type": "file_start", "transferId": "...", "fileName": "...", "fileSize": 0, "mimeType": "..."}
{"type": "file_chunk", "transferId": "...", "chunkIndex": 0, "chunkData": "base64", "isLast": false}
{"type": "file_abort", "transferId": "..."}
```

**BLE 信封格式（BleEnvelope）：**
```json
{
  "type": "notify_ready|client_hello|verified|encrypted|poll|error",
  "publicKey": "base64",
  "token": "hex",
  "serverUrl": "wss://...",
  "payload": "base64",
  "nonce": "base64",
  "seq": 0,
  "code": "6-digit",
  "approved": true,
  "version": "1.7.0"
}
```

### 3.3 QR 码格式

- 紧凑格式：`F1|<token>|<pcPublicKey>[|<serverUrl>]`
- 遗留 JSON：`{"s": "wss://...", "t": "...", "k": "..."}`

### 3.4 BLE GATT 服务定义

```
Service UUID:        21e2f7d4-4dc0-4b0d-a145-5f9b6459be10
Notify Characteristic: 21e2f7d4-4dc0-4b0d-a145-5f9b6459be11  (PC → 手机)
Write Characteristic:  21e2f7d4-4dc0-4b0d-a145-5f9b6459be12  (手机 → PC)
```

- 手机作为 **BLE Central**（扫描/连接方）— 已确认，不添加反向角色
- PC 作为 **BLE Peripheral**（广播方）

### 3.5 Diff 算法

- **仅前缀匹配**，不使用后缀匹配（因为 PC 端通过 `SendInput` 退格从末尾删除）
- **Grapheme 感知**：使用 ICU `BreakIterator` 计算可见字符数（对应 PWA 的 `Intl.Segmenter`）
- 消息格式：`{ type: "diff", count: <退格数>, text: "<插入文本>" }`

### 3.6 连接参数

| 参数 | 值 |
|---|---|
| 协议版本 | `1.7.0` |
| 重连延迟 | 2000ms 固定 |
| 最大重连次数 | 300 |
| 连接超时 | 8000ms |
| 握手超时 | 12000ms |
| 离线缓冲窗口 | 10000ms |
| 心跳间隔 | 1000ms |
| 心跳超时 | 2500ms |
| 文件分块大小 | 64 KB |
| 并发分块数 | 3 |
| BLE 轮询间隔 | 500ms |
| BLE 连续失败阈值 | 3 |

---

## 四、技术选型

### 4.1 核心依赖

| 模块 | 推荐方案 | 选择理由 |
|---|---|---|
| **UI 框架** | Jetpack Compose + Material 3 | Android 15 原生支持，声明式 UI，动画流畅 |
| **加密库** | [LibSodium (libsodium-jna)](https://github.com/joshjdevl/libsodium-jna) 或 [Tink](https://developers.google.com/tink) | X25519 + XSalsa20-Poly1305 原生支持。首选 LibSodium 因为与 tweetnacl 算法完全一致；Tink 不直接支持 XSalsa20 |
| **WebSocket** | [OkHttp WebSocket](https://square.github.io/okhttp/) | 成熟稳定，支持 ping/pong、重连、拦截器，Android 生态标准 |
| **BLE** | Android 原生 `BluetoothGatt` API | 无需第三方库，API 31+ 蓝牙权限模型稳定，Android 15 无变更 |
| **QR 扫描** | [ML Kit Barcode Scanning](https://developers.google.com/ml-kit/vision/barcode-scanning) | Google 官方，离线运行，性能优秀 |
| **依赖注入** | [Hilt](https://dagger.dev/hilt/) | Android 官方推荐，与 ViewModel/Service 深度集成 |
| **异步框架** | Kotlin Coroutines + Flow | 原生支持，生命周期感知，替代 PWA 中的回调/ref 模式 |
| **持久化** | DataStore (Preferences) | 轻量级，协程友好，适合存储连接信息、设置、设备 ID |
| **序列化** | [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization) | Kotlin 原生，编译时生成，JSON 字段名可控 |
| **导航** | Compose Navigation | 官方方案，与 Compose 深度集成 |

### 4.2 加密库选型（已确认）

**已决定采用方案 A：LibSodium JNI wrapper。**

| 方案 | 优点 | 缺点 |
|---|---|---|
| **A: libsodium-jna (JNI wrapper)** | 与 tweetnacl 算法完全一致（XSalsa20-Poly1305），零改动移植 | JNI 依赖，APK 体积增加 ~1MB |
| B: pure-Kotlin 实现 | 无 JNI，体积小，完全可控 | 需要自行实现或找到可靠实现 |
| C: Tink + 自定义 | Google 维护，安全性高 | Tink 不支持 XSalsa20，复杂度高 |

选择理由：与现有协议的加密实现完全一致是最高优先级，任何算法差异都会导致互操作失败。1MB 的体积增加可以接受。

---

## 五、Android 15 适配方案

### 5.1 前台服务策略

**选择 `connectedDevice` 类型** — 这是 Android 15 下最适合 Fluentia 的前台服务类型。

| 特性 | `connectedDevice` | `dataSync` | `location` |
|---|---|---|---|
| BLE 权限要求 | BLUETOOTH_CONNECT/SCAN/ADVERTISE | 无 | 无 |
| 后台启动限制 | 无 | Android 15 限制 | 无 |
| BOOT_COMPLETED | 允许 | 禁止 (API 35) | 允许 |
| 超时限制 | 无 | 6 小时/24 小时 | 无 |
| 适用场景 | BLE + WebSocket 双通道 | 纯数据同步 | 位置相关 |

**Manifest 声明（API 34+，无需旧版 BLUETOOTH/BLUETOOTH_ADMIN）：**
```xml
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE" />
<uses-permission android:name="android.permission.BLUETOOTH_SCAN"
    android:usesPermissionFlags="neverForLocation" />
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
<uses-permission android:name="android.permission.BLUETOOTH_ADVERTISE" />

<service
    android:name=".service.FluentiaForegroundService"
    android:foregroundServiceType="connectedDevice"
    android:exported="false" />
```

### 5.2 蓝牙权限模型

Android 12-15 权限模型一致，无需特殊适配：

```kotlin
// 运行时权限请求顺序
val permissions = arrayOf(
    Manifest.permission.BLUETOOTH_SCAN,      // 扫描 BLE 设备
    Manifest.permission.BLUETOOTH_CONNECT,   // 连接已配对设备
    Manifest.permission.BLUETOOTH_ADVERTISE, // BLE 广播（如果需要）
)
```

**注意事项：**
- `neverForLocation` 标志：Fluentia 不从 BLE 推导位置，应添加此标志以避免位置权限要求
- Companion Device Manager：可选方案，提供系统级配对 UI，不需要位置权限

### 5.3 后台保活策略

```
用户打开 App
    │
    ├─ 连接建立 → 启动 ForegroundService (connectedDevice)
    │   ├─ 通知栏显示连接状态
    │   ├─ BLE 传输 + WebSocket 心跳在 Service 中运行
    │   └─ Service 生命周期独立于 Activity
    │
    ├─ 用户切到后台
    │   ├─ Service 继续运行（connectedDevice 无超时限制）
    │   ├─ BLE 连接保持（Android BLE API 后台兼容）
    │   └─ WebSocket 心跳在协程中持续
    │
    ├─ 屏幕关闭
    │   ├─ Service 仍在运行
    │   ├─ BLE 连接通过系统维护
    │   └─ WebSocket 依赖 Service 进程存活
    │
    └─ 系统杀死进程（低内存）
        ├─ 用户回到 App → 检测断开 → 自动重连
        └─ BLE 配对信息持久化 → 重连无需重新配对
```

**关键保障：**
1. `connectedDevice` 前台服务无 Android 15 超时限制
2. 可从 `BOOT_COMPLETED` 启动（不在限制列表中）
3. 通知栏常驻确保进程优先级
4. BLE 连接状态驱动 Service 生命周期

### 5.4 Android 15 行为变更适配

| 变更 | 影响 | 适配方案 |
|---|---|---|
| Edge-to-edge 强制 | 状态栏/导航栏透明 | 使用 `enableEdgeToEdge()`，Compose `WindowInsets` 处理 |
| 预测性返回动画 | 全面启用 | 使用 `BackHandler` + Compose 动画系统 |
| 后台网络限制 | 后台请求可能失败 | 网络操作绑定到前台服务或活跃生命周期 |
| TLS 1.0/1.1 禁用 | 连接失败 | 确保 WebSocket 使用 TLS 1.2+（OkHttp 默认支持） |
| `SYSTEM_ALERT_WINDOW` 限制 | 后台 FGS 启动受限 | 不依赖此权限，使用正常 FGS 启动路径 |

### 5.5 小米 HyperOS 3 专项适配

HyperOS 3（基于 Android 15）在原生 Android 后台限制之上叠加了更激进的电池优化策略。**即使正确实现了 `connectedDevice` 前台服务，HyperOS 仍可能在灭屏后杀死进程。** 必须在开发初期纳入适配。

#### 5.5.1 HyperOS 后台管理机制

| 机制 | 说明 | 影响 |
|---|---|---|
| 自启动权限 | 默认禁止，用户需手动开启 | 应用无法在开机后自动恢复连接 |
| Power Keeper（神隐模式） | `com.miui.powerkeeper` 叠加在 Doze 之上，额外冻结后台应用 | 前台服务可能被降级或杀死 |
| App Standby Buckets | 比原生更激进，Rare/Restricted 桶的应用直接冻结 | WebSocket 心跳被阻断 |
| 最近任务锁定 | 用户可下拉卡片锁定应用，防止清理时被杀 | 解锁后仍可能被 Power Keeper 杀死 |

#### 5.5.2 前台服务在 HyperOS 上的行为差异

| 行为 | 原生 Android 15 | HyperOS 3 |
|---|---|---|
| `connectedDevice` FGS 超时 | 无限制 | 无官方限制，但 Power Keeper 可能强制终止 |
| FGS 通知可见性 | 始终可见 | 可能被自动折叠或隐藏 |
| 通知频道重要性 | `IMPORTANCE_MIN` 即可 | 必须 `IMPORTANCE_LOW` 以上，否则可能完全隐藏 |
| 灭屏后 FGS 存活 | 通常保持 | 长时间灭屏后可能被杀 |

#### 5.5.3 BLE 后台稳定性问题

- BLE GATT 连接在应用进入后台后可能被系统主动断开
- 后台 BLE 扫描被限流或完全阻断
- `BluetoothGatt` 回调在进程被杀后丢失
- **必须使用 `connectGatt(autoConnect=true)`** 以支持系统级自动重连

#### 5.5.4 用户引导方案

首次连接成功后，检测设备是否为小米/HyperOS，弹出引导页提示用户完成以下设置：

| 设置项 | 路径 | 必要性 |
|---|---|---|
| 自启动 | 设置 > 应用 > [应用] > 应用权限 > 后台自启动 | **必须** |
| 电池策略 | 设置 > 电池 > 应用省电策略 > [应用] > 无限制 | **必须** |
| 通知权限 | 确保通知频道未被手动关闭 | **必须** |
| 最近任务锁定 | 最近任务界面 > 下拉应用卡片锁定 | 建议 |

#### 5.5.5 设备检测代码

```kotlin
object OemDetector {
    fun isMiuiOrHyperOS(): Boolean = try {
        val clazz = Class.forName("android.os.SystemProperties")
        val get = clazz.getMethod("get", String::class.java)
        val v = get.invoke(null, "ro.miui.ui.version.name") as? String
        !v.isNullOrEmpty()
    } catch (_: Exception) { false }

    fun isHyperOS(): Boolean = try {
        val clazz = Class.forName("android.os.SystemProperties")
        val get = clazz.getMethod("get", String::class.java)
        val v = get.invoke(null, "ro.mi.os.version.name") as? String
        !v.isNullOrEmpty()
    } catch (_: Exception) { false }

    fun getHyperOSVersion(): String? = try {
        val clazz = Class.forName("android.os.SystemProperties")
        val get = clazz.getMethod("get", String::class.java)
        get.invoke(null, "ro.mi.os.version.name") as? String
    } catch (_: Exception) { null }
}
```

系统属性：
- `ro.miui.ui.version.name` — HyperOS 返回 `"OS3.0"`，旧 MIUI 返回 `"V14"`
- `ro.mi.os.version.name` — HyperOS 专属，返回 `"3.0"`

#### 5.5.6 跳转小米设置页的 Intent

```kotlin
object MiuiSettings {
    /** 跳转自启动管理，依次尝试多个已知 Activity */
    fun openAutoStart(context: Context): Boolean {
        val candidates = listOf(
            ComponentName("com.miui.securitycenter",
                "com.miui.permcenter.autostart.AutoStartManagementActivity"),
            ComponentName("com.miui.securitycenter",
                "com.miui.securitycenter.permission.AutoStartManagementActivity"),
            ComponentName("com.miui.permcenter",
                "com.miui.permcenter.autostart.AutoStartManagementActivity"),
        )
        for (cn in candidates) {
            try {
                context.startActivity(Intent().setComponent(cn)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return true
            } catch (_: Exception) { }
        }
        // 兜底：通用 MIUI 权限编辑器
        try {
            context.startActivity(Intent("miui.intent.action.APP_PERM_EDITOR")
                .putExtra("extra_pkgname", context.packageName)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return true
        } catch (_: Exception) { }
        return false
    }

    /** 跳转电池省电策略设置 */
    fun openBatterySettings(context: Context): Boolean {
        val candidates = listOf(
            Intent().setComponent(ComponentName(
                "com.miui.powerkeeper",
                "com.miui.powerkeeper.ui.HiddenAppsConfigActivity"
            )).putExtra("package_name", context.packageName),
            // 标准 Android 电池优化豁免（所有设备通用）
            Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                .setData(Uri.parse("package:${context.packageName}"))
        )
        for (intent in candidates) {
            try {
                context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return true
            } catch (_: Exception) { }
        }
        return false
    }
}
```

#### 5.5.7 通知频道配置

```kotlin
val channel = NotificationChannel(
    "fluentia_ble_service",
    "Fluentia 设备连接",
    NotificationManager.IMPORTANCE_LOW  // 必须 LOW 以上，MIN 会被 HyperOS 隐藏
).apply {
    description = "保持 BLE 和 WebSocket 连接"
    setShowBadge(false)
}
```

运行时检查通知是否被关闭：
```kotlin
fun checkNotificationEnabled(context: Context): Boolean {
    val nm = context.getSystemService(NotificationManager::class.java)
    if (!nm.areNotificationsEnabled()) return false
    val channel = nm.getNotificationChannel("fluentia_ble_service")
    return channel?.importance != NotificationManager.IMPORTANCE_NONE
}
```

#### 5.5.8 BLE 连接加固策略

```kotlin
// 必须使用 autoConnect=true，让系统在设备可用时自动重连
bluetoothDevice.connectGatt(context, true, gattCallback, TRANSPORT_LE)

// 在 onConnectionStateChange 中处理断开
override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
    when (newState) {
        BluetoothProfile.STATE_DISCONNECTED -> {
            // 不要立即 close()，保留 GATT 对象供 autoConnect 重连
            if (status != GATT_SUCCESS) {
                // 非用户主动断开，触发重连逻辑
                scheduleReconnect()
            }
        }
    }
}

// 使用 WakeLock 保持 BLE 操作期间 CPU 不休眠
val wakeLock = powerManager.newWakeLock(
    PowerManager.PARTIAL_WAKE_LOCK,
    "fluentia:ble_keepalive"
).apply { acquire(10 * 60 * 1000L) }  // 10 分钟超时保护
```

#### 5.5.9 WebSocket 后台保活加固

- 前台服务中使用 `connectedDevice` 类型维持进程优先级
- 心跳间隔 1000ms（与现有协议一致），配合前台服务的进程优先级
- 断线后 2s 固定重连，最多 300 次
- 若 HyperOS 杀死进程：用户回到 App → 检测断开 → 自动重连（加密会话从 DataStore 恢复）

---

## 六、模块详细设计

### 6.1 加密模块 (`CryptoService`)

```kotlin
class CryptoService {
    // X25519 密钥对
    private var keyPair: KeyPair
    private var peerPublicKey: ByteArray?

    // 双向棘轮状态
    private var sendChainKey: ByteArray?
    private var recvChainKey: ByteArray?
    private var sendSeq: Int = 0
    private var expectedSeq: Int = 0

    // 初始化：生成 X25519 密钥对
    fun generateKeyPair(): String  // 返回 base64 公钥

    // 设置对端公钥
    fun setPeerPublicKey(base64Key: String)

    // 棘轮初始化：生成种子，加密发送
    fun initRatchet(): String  // 返回 base64 种子
    fun initRecvRatchet(seedBase64: String)

    // 加密/解密
    fun encrypt(plaintext: String): EncryptedPayload       // nacl.box
    fun encryptRatcheted(plaintext: String): EncryptedPayload  // nacl.secretbox + seq
    fun decrypt(payload: String, nonce: String): String?    // nacl.box.open
    fun decryptRatcheted(payload: String, nonce: String, seq: Int): String?  // nacl.secretbox.open

    // KDF：SHA-512(key ‖ label) 取前 32 字节
    private fun kdf(key: ByteArray, label: String): ByteArray

    // 棘轮步进：messageKey = kdf(chain, "msg"), nextChain = kdf(chain, "chain")
    private fun ratchetStep(chainKey: ByteArray): Pair<ByteArray, ByteArray>
}
```

### 6.2 WebSocket 模块 (`WebSocketManager`)

```kotlin
class WebSocketManager(
    private val okHttpClient: OkHttpClient,
    private val cryptoService: CryptoService,
    private val transportHealth: TransportHealthMonitor,
) {
    private var webSocket: WebSocket? = null
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    // 连接
    suspend fun connect(info: ConnectionInfo)
    fun disconnect()

    // 发送加密消息
    suspend fun sendEncrypted(command: InputCommand)

    // 心跳：1000ms 间隔，2500ms 超时
    private fun startHeartbeat()
    private fun stopHeartbeat()

    // 重连：2000ms 固定延迟，最多 300 次
    private fun scheduleReconnect()

    // 离线缓冲：10000ms 窗口内输入排队
    private val commandQueue = mutableListOf<InputCommand>()
    private fun flushQueue()
}
```

### 6.3 BLE 模块 (`BleTransportManager`)

```kotlin
class BleTransportManager(
    private val context: Context,
    private val cryptoService: CryptoService,
) {
    // 扫描并连接 PC 的 BLE 外设
    suspend fun scanAndConnect()

    // GATT 操作
    private var bluetoothGatt: BluetoothGatt?
    private var notifyCharacteristic: BluetoothGattCharacteristic?
    private var writeCharacteristic: BluetoothGattCharacteristic?

    // 发送 BleEnvelope
    suspend fun sendEnvelope(envelope: BleEnvelope)

    // 轮询：每 500ms 发送 { type: "poll" }
    private fun startPolling()

    // 自动重连：最多 5 次，指数退避 1-5 秒
    private fun handleDisconnect()

    // 配对流程
    suspend fun initiatePairing()
    suspend fun sendClientHello()
}
```

### 6.4 Transport Health Monitor

```kotlin
class TransportHealthMonitor {
    // BLE 评分：基准 50，RSSI 加减分，连续失败 -15/次
    private var bleScore: Int = 50

    // WS 评分：基准 60，RTT 加减分
    private var wsScore: Int = 60

    // 电池感知：< 20% 禁并发，< 10% 强制 BLE
    fun updateBatteryLevel(level: Float)

    // 路由决策
    fun selectTransport(messageType: MessageType): Transport
    // input → BLE (score >= 40)，file → WS，handshake/control → 高分者

    // 恢复：每 30 秒重置已宕通道
    private fun startRecoveryTimer()
}
```

### 6.5 Diff 引擎

```kotlin
object DiffEngine {
    // 前缀匹配 diff
    fun computeDiff(oldText: String, newText: String): TextDiff
    // TextDiff(backspaceCount: Int, insertText: String)

    // Grapheme 长度计算（ICU BreakIterator）
    private fun graphemeLength(text: String): Int

    // 防抖：60ms 窗口（在 ViewModel 中实现）
}
```

### 6.6 前台服务

```kotlin
class FluentiaForegroundService : Service() {
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = createNotification(channelId, "Fluentia 已连接")
        startForeground(NOTIFICATION_ID, notification,
            FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        return START_STICKY
    }

    // 通知栏显示：连接状态、BLE 状态、加密状态
    private fun updateNotification(state: ConnectionState)

    // 生命周期与连接状态绑定
    // 连接断开 → 停止服务
    // 新连接 → 启动服务
}
```

---

## 七、功能清单与优先级

### P0 — 首版必须实现

| 功能 | 说明 |
|---|---|
| QR 码扫描 | ML Kit，支持 `F1|...` 紧凑格式和 JSON 格式 |
| 设备码手动输入 | 8 字母数字输入 |
| WebSocket 连接 | OkHttp，完整握手流程 |
| 加密协议 | X25519 + XSalsa20-Poly1305 + 双向棘轮 |
| 文本输入 + Diff | 前缀 diff、grapheme 感知、60ms 防抖 |
| Enter / Backspace / Clear | 基础输入命令 |
| 前台服务 | connectedDevice 类型，通知栏状态 |
| 自动重连 | 2s 延迟，300 次上限 |
| 离线输入缓冲 | 10s 窗口，重连后重放 |
| 会话持久化 | DataStore 存储连接信息和加密会话 |

### P1 — 第二版

| 功能 | 说明 |
|---|---|
| BLE Central 模式 | 扫描、连接、GATT 读写 |
| BLE 配对流程 | notify_ready → ble_auth → client_hello → verified |
| BLE 验证码 | 6 位数字，基于 X25519 共享密钥 |
| 双通道传输健康 | BLE/WS 评分路由 |
| 历史记录 | 最近 100 条，DataStore 存储 |
| 剪贴板发送 | 复制到 PC 剪贴板 |

### P2 — 后续迭代

| 功能 | 说明 |
|---|---|
| 文件传输 | 分块 64KB，并发 3，IO 协程 |
| 正则过滤 | Markdown 格式解析，规则应用 |
| BLE 重连优化 | 后台 BLE 连接维护 |
| 深色模式 | Material 3 动态主题 |
| 电池优化 | 低电量模式切换 |
| 小组件 | 快速输入 Widget |

---

## 八、项目结构

```
android/
├── app/
│   ├── src/main/
│   │   ├── java/com/fluentia/app/
│   │   │   ├── FluentiaApp.kt              # Application (Hilt)
│   │   │   ├── MainActivity.kt
│   │   │   │
│   │   │   ├── ui/                          # Compose UI
│   │   │   │   ├── theme/
│   │   │   │   ├── screen/
│   │   │   │   │   ├── InputScreen.kt
│   │   │   │   │   ├── ScannerScreen.kt
│   │   │   │   │   ├── HistoryScreen.kt
│   │   │   │   │   └── SettingsScreen.kt
│   │   │   │   ├── component/
│   │   │   │   │   ├── Header.kt
│   │   │   │   │   ├── InputArea.kt
│   │   │   │   │   └── BlePairingCard.kt
│   │   │   │   └── navigation/
│   │   │   │
│   │   │   ├── viewmodel/                   # ViewModel 层
│   │   │   │   ├── ConnectionViewModel.kt
│   │   │   │   ├── InputViewModel.kt
│   │   │   │   ├── FileTransferViewModel.kt
│   │   │   │   └── SettingsViewModel.kt
│   │   │   │
│   │   │   ├── service/                     # 服务层
│   │   │   │   ├── websocket/
│   │   │   │   │   ├── WebSocketManager.kt
│   │   │   │   │   └── WebSocketMessage.kt
│   │   │   │   ├── ble/
│   │   │   │   │   ├── BleTransportManager.kt
│   │   │   │   │   ├── BlePairingManager.kt
│   │   │   │   │   └── BleConstants.kt
│   │   │   │   ├── crypto/
│   │   │   │   │   └── CryptoService.kt
│   │   │   │   ├── transport/
│   │   │   │   │   ├── TransportHealthMonitor.kt
│   │   │   │   │   └── TransportConnection.kt
│   │   │   │   ├── diff/
│   │   │   │   │   └── DiffEngine.kt
│   │   │   │   ├── oem/                     # 厂商兼容层
│   │   │   │   │   ├── OemDetector.kt       # 设备检测（HyperOS/MIUI/三星等）
│   │   │   │   │   ├── MiuiSettings.kt      # 小米设置页跳转
│   │   │   │   │   └── WhitelistManager.kt  # 后台白名单引导
│   │   │   │   ├── FluentiaForegroundService.kt
│   │   │   │   └── NotificationHelper.kt
│   │   │   │
│   │   │   ├── data/                        # 数据层
│   │   │   │   ├── SessionStore.kt
│   │   │   │   ├── DeviceIdStore.kt
│   │   │   │   ├── HistoryStore.kt
│   │   │   │   ├── CryptoSessionStore.kt
│   │   │   │   ├── SettingsStore.kt
│   │   │   │   └── model/
│   │   │   │       ├── ConnectionInfo.kt
│   │   │   │       ├── WsMessage.kt
│   │   │   │       ├── InputCommand.kt
│   │   │   │       └── BleEnvelope.kt
│   │   │   │
│   │   │   └── di/                          # Hilt 模块
│   │   │       ├── AppModule.kt
│   │   │       ├── NetworkModule.kt
│   │   │       └── BleModule.kt
│   │   │
│   │   ├── res/
│   │   └── AndroidManifest.xml
│   │
│   └── build.gradle.kts
│
├── gradle/
│   └── libs.versions.toml                   # Version catalog
│
└── build.gradle.kts
```

---

## 九、实施阶段

### 阶段一：基础框架 + 加密通信（2-3 周）

**目标：** 能通过 WebSocket 与 PC 建立加密连接并发送 diff。

1. 项目初始化：Android Studio 项目、Gradle 配置、Hilt 设置
2. Data 层：DataStore 实现（设备 ID、连接信息、加密会话持久化）
3. 加密模块：`CryptoService` Kotlin 移植（LibSodium JNI）
4. WebSocket 模块：OkHttp WebSocket + 完整握手流程
5. Diff 引擎：前缀 diff + ICU grapheme
6. 基础 UI：输入界面 + 连接状态显示
7. QR 扫描：ML Kit 集成
8. 前台服务：connectedDevice 类型 + 通知频道（IMPORTANCE_LOW 以上）
9. 厂商兼容层：`OemDetector` + `WhitelistManager` + 首次连接引导页
10. **验证：** 与现有 PC 端完成端到端加密 diff 传输

### 阶段二：BLE 传输 + 双通道（2-3 周）

**目标：** BLE 低延迟输入 + 自动通道切换。

1. BLE Central 扫描/连接（`connectGatt(autoConnect=true)`）
2. BLE 配对流程（完整的 auth 流程）
3. BLE 传输层（GATT 读写）
4. Transport Health Monitor 移植
5. 双通道路由
6. BLE 验证码 UI
7. HyperOS BLE 加固：WakeLock、重连状态机、后台断连处理
8. **验证：** BLE 和 WS 双通道切换，BLE 断开自动回退 WS，小米设备灭屏后连接存活

### 阶段三：完善功能 + 优化（2 周）

**目标：** 功能完整性 + 用户体验。

1. 历史记录
2. 剪贴板命令
3. 文件传输（分块、并发、重试）
4. 正则过滤
5. 设备码手动连接
6. 电池感知 + 低电量模式

### 阶段四：打磨 + 发布（1-2 周）

**目标：** 稳定性 + 发布准备。

1. 边界情况处理（网络切换、蓝牙开关、系统休眠）
2. 性能优化（内存、CPU、电池）
3. UI 打磨（动画、过渡、错误提示）
4. 权限请求流程优化
5. ProGuard/R8 配置
6. 测试补全
7. APK 签名 + GitHub Release 发布流程

---

## 十、测试策略

### 10.1 单元测试

| 模块 | 测试内容 | 工具 |
|---|---|---|
| CryptoService | 加密/解密、棘轮推进、密钥派生 | JUnit 5 + LibSodium test vectors |
| DiffEngine | 前缀匹配、grapheme 计算、边界情况 | JUnit 5 |
| TransportHealthMonitor | 评分计算、路由决策、电池阈值 | JUnit 5 + MockK |
| WsMessage | JSON 序列化/反序列化 | kotlinx.serialization test |

### 10.2 集成测试

| 场景 | 测试方式 |
|---|---|
| WebSocket 握手 + 加密 | MockWebServer 模拟服务器 |
| BLE GATT 操作 | Android Instrumented Test + 蓝牙模拟器 |
| 加密协议互操作性 | 与现有 TS 实现的 test vector 交叉验证 |

### 10.3 CI/CD 集成（GitHub Actions）

Android 模块集成到现有 GitHub Actions 流水线中，与 PWA 和 Go server 共存于同一仓库。

```yaml
# .github/workflows/android.yml
name: Android CI
on:
  push:
    paths: ['android/**']
  pull_request:
    paths: ['android/**']

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-java@v4
        with:
          distribution: 'temurin'
          java-version: '17'

      - name: Build
        run: cd android && ./gradlew assembleDebug

      - name: Unit Tests
        run: cd android && ./gradlew test

      - name: Lint
        run: cd android && ./gradlew lint

      - name: Upload APK
        uses: actions/upload-artifact@v4
        with:
          name: debug-apk
          path: android/app/build/outputs/apk/debug/*.apk

  instrumented-test:
    runs-on: ubuntu-latest
    needs: build-and-test
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-java@v4
        with:
          distribution: 'temurin'
          java-version: '17'

      - name: AVD Cache
        uses: actions/cache@v4
        with:
          path: |
            ~/.android/avd/*
            ~/.android/adb*
          key: avd-api34

      - name: Run Instrumented Tests
        uses: reactivecircus/android-emulator-runner@v2
        with:
          api-level: 34
          script: cd android && ./gradlew connectedAndroidTest

  release:
    if: startsWith(github.ref, 'refs/tags/android-v')
    needs: [build-and-test, instrumented-test]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-java@v4
        with:
          distribution: 'temurin'
          java-version: '17'

      - name: Build Release APK
        run: cd android && ./gradlew assembleRelease
        env:
          KEYSTORE_PASSWORD: ${{ secrets.ANDROID_KEYSTORE_PASSWORD }}
          KEY_ALIAS: ${{ secrets.ANDROID_KEY_ALIAS }}
          KEY_PASSWORD: ${{ secrets.ANDROID_KEY_PASSWORD }}

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          files: android/app/build/outputs/apk/release/*.apk
          generate_release_notes: true
```

**BLE 测试策略：**
- 单元测试覆盖 BLE 协议逻辑（信封构造/解析、配对流程状态机、GATT 数据拼装）
- 集成测试使用 Android 模拟器的虚拟蓝牙（有限功能，主要验证 API 调用流程）
- 真机 BLE 测试作为手动验证环节（不在 CI 中自动化）

---

## 十一、风险评估

| 风险 | 影响 | 缓解措施 |
|---|---|---|
| **HyperOS 杀死前台服务** | BLE 和 WS 连接中断 | 自启动引导 + 电池策略设置 + 通知频道 IMPORTANCE_LOW + `autoConnect=true` |
| **HyperOS 隐藏 FGS 通知** | 前台服务被系统视为无效 | 运行时检查通知状态，引导用户开启 |
| **HyperOS 断开后台 BLE** | GATT 连接丢失 | `connectGatt(autoConnect=true)` + WakeLock + 重连状态机 |
| LibSodium JNI 兼容性 | 加密失败 | 充分的设备兼容性测试，备选 pure-Kotlin 实现 |
| BLE 厂商行为差异 | 连接不稳定 | 优先测试小米/三星/华为，完善的重连逻辑 |
| Android 15 后台网络限制 | WebSocket 后台断开 | 前台服务 + 生命周期绑定，确保网络请求在有效进程内 |
| 加密协议移植错误 | 互操作失败 | 与现有 TS/Go 实现的 test vector 交叉验证，逐字节对比 |
| BLE MTU 限制 | 大消息传输失败 | 消息分片，假设小 payload 设计，大消息走 WS |

---

## 十二、官方文档引用

| 主题 | 链接 |
|---|---|
| 前台服务类型 | https://developer.android.com/develop/background-work/services/fgs/service-types |
| 蓝牙权限 | https://developer.android.com/develop/connectivity/bluetooth/bt-permissions |
| BLE 概述 | https://developer.android.com/develop/connectivity/bluetooth/ble/ble-overview |
| Android 15 行为变更 | https://developer.android.com/about/versions/15/behavior-changes-15 |
| Android 15 所有应用行为变更 | https://developer.android.com/about/versions/15/behavior-changes-all |
| Compose BOM 映射 | https://developer.android.com/jetpack/compose/bom/bom-mapping |
| Edge-to-edge | https://developer.android.com/develop/ui/views/layout/edge-to-edge |
| 小米后台限制参考 | https://dontkillmyapp.com/xiaomi |
| MIUI 自启动检测库 | https://github.com/XomaDev/MIUI-autostart |

---

## 十三、已确认的决策

| 决策项 | 决定 |
|---|---|
| 最低 API 版本 | API 34 (Android 14) |
| 目标 API 版本 | API 35 (Android 15) |
| 加密库 | **LibSodium JNI wrapper** — 与 tweetnacl 算法完全一致，跨语言协议一致性为最高优先级 |
| BLE 角色 | 手机作为 **Central**，PC 作为 **Peripheral**，与 PWA 一致，不添加反向角色 |
| 首版功能范围 | 聚焦**文本输入 + BLE + WebSocket**，文件传输/正则过滤延后至 P2 |
| 语音识别 | 不搭载，文本输入仅通过系统键盘 |
| 分发方式 | APK 文件，通过 GitHub Release 分发 |
| PWA 共存 | 保留并维护 PWA，原生版作为并行高级选项 |
| CI/CD | 集成到 GitHub Actions，包含编译、测试、lint、APK 发布 |
| 厂商适配 | 开发初期纳入小米 HyperOS 3 专项适配（自启动引导、通知频道、BLE 加固） |
