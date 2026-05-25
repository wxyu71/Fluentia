# Fluentia Roadmap Analysis

## 1. Desktop Occasionally Stuck on Waiting for PC

### Likely control path
- The desktop injector only sends input when `EnsureInputTarget()` accepts the current foreground window.
- If Fluentia itself remains foreground, it tries `TryRestorePreviousExternalWindow()`.
- That restore depends on `SetForegroundWindow`, which Windows may refuse unless the thread already owns foreground focus.
- When that call fails, Fluentia keeps waiting until the user manually clicks another desktop window.

### Reproduction hypothesis
1. Pair the phone while Fluentia is still the active foreground window.
2. Finish key exchange and let the desktop auto-hide.
3. If Windows refuses the foreground restore, `_inputTargetWindow` is never rebound to a real external app.
4. The next phone input stalls until the user clicks a desktop app, which refreshes foreground ownership.

### Recommended next diagnostic step
- Log the result of `SetForegroundWindow` and the window handles around `EnsureInputTarget()`.
- Capture whether `_lastExternalForegroundWindow` is stale, invisible, or equal to Fluentia itself.
- If confirmed, the least invasive fix is to retry restore on a short timer while the window is hidden, instead of depending on a single foreground transition.

## 2. Mobile Browser Heat and Native Android Assessment

### Current hotspots in this codebase
- Repeated NaCl operations in `mobile/src/utils/crypto.ts` during reconnect and ratchet setup.
- High-frequency diff generation and websocket traffic in `mobile/src/components/InputArea.tsx` and `mobile/src/hooks/useWebSocket.ts`.
- File transfer preprocessing and base64 conversion, even after moving chunk encoding to a worker.
- Browser lifecycle churn when the page is backgrounded and resumed.

### When native Android becomes worth it
- Long-running voice dictation in the background.
- Persistent low-latency input while the screen is off.
- BLE pairing or transport without relying on browser permission prompts.
- Stronger thermal control via native crypto, foreground services, and backpressure tuning.

### Recommended Android direction
- Keep the current mobile web app as the fast iteration surface.
- Plan a native Android companion only after the current web transport is stabilized.
- Use Kotlin with a libsodium-backed implementation so the Android client can stay protocol-compatible with the current desktop and server.
- Reserve the native app for background reliability, audio capture, BLE, and lower thermal overhead.

## 3. Saved Key Forgetfulness Root Cause

### Root cause
- Desktop trust material was previously stored in a single protected payload.
- If that payload is missing or cannot be unprotected, the app has no secondary recovery source.
- The relay server also used in-memory sessions only, so a server restart forced a fresh pairing even if the desktop keypair still existed.

### What has now been strengthened
- Desktop now writes a protected backup copy of the trusted session.
- Desktop shows an explicit prompt when the trusted session cannot be restored.
- Server now persists session token metadata so restart does not immediately invalidate all reusable sessions.

## 4. Bluetooth Strategy: Secure and Efficient Hybrid Transport

### Recommended transport split
- BLE for trust bootstrap and low-latency text control when the phone is physically nearby.
- WebSocket relay for file transfer, long-range access, reconnect after the phone leaves BLE range, and all WAN scenarios.

### Why hybrid is better than BLE-only
- BLE MTU and throughput are poor for large files.
- BLE reliability varies heavily across Android vendors and power modes.
- BLE-only remote access is impossible outside short physical range.
- BLE audio coexistence and aggressive background throttling make sustained heavy transport fragile.

### Security model
1. Use BLE only to exchange authenticated bootstrap material.
2. Exchange X25519 public keys over BLE.
3. Show a short out-of-band verification code on both devices before trust is persisted.
4. Derive transport-specific symmetric keys from a shared root secret with explicit labels.
5. Keep end-to-end encryption above the transport layer so BLE and WebSocket are interchangeable carriers.

### Suggested key derivation layout
- Root secret: established once after the verified X25519 exchange.
- `control/ws`: websocket text and clipboard traffic.
- `control/ble`: BLE low-latency text traffic.
- `file/ws`: file transfer only.
- `session/recovery`: persisted trust metadata.

### Recommended packet policy
- BLE sends only compact control frames: text diffs, enter, backspace, clipboard metadata, keepalive.
- WebSocket continues to carry file chunks and recovery traffic.
- If BLE is available and healthy, prefer BLE for text input.
- If BLE degrades or disconnects, fail over to WebSocket without rotating trust.

### Android implementation notes
- Use a foreground service for BLE control when active.
- Use notifications for downstream desktop status.
- Negotiate the largest safe MTU but design packets assuming small payloads.
- Avoid streaming files over BLE except as a debugging fallback.

## 5. Practical Next Step Order
1. Stabilize offline buffering and reconnection metrics in the current web client.
2. Add diagnostics for desktop foreground restore and confirm the waiting-for-PC hypothesis.
3. Keep BLE at the protocol-draft stage until the current transport is stable.
4. Start native Android only when background voice and BLE become priority requirements.
