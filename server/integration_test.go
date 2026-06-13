package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

// dial connects to the test server and returns a *websocket.Conn.
func dial(t *testing.T, ts *httptest.Server) *websocket.Conn {
	t.Helper()
	url := "ws" + strings.TrimPrefix(ts.URL, "http") + "/ws"
	ws, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		t.Fatalf("dial failed: %v", err)
	}
	return ws
}

// newTestServer starts an httptest server with a Hub.
func newTestServer() (*Hub, *httptest.Server) {
	hub := NewHub(1)
	// Set srvGlobals so tests work with the new origin validation.
	// In tests, websocket dialer doesn't send Origin header, so AllowEmptyOrigin must be true.
	if srvGlobals == nil {
		srvGlobals = &ServerGlobals{
			AllowEmptyOrigin: true,
		}
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/ws", func(w http.ResponseWriter, r *http.Request) {
		serveWs(hub, &ServerConfig{}, w, r)
	})
	ts := httptest.NewServer(mux)
	return hub, ts
}

func wsSend(t *testing.T, ws *websocket.Conn, msg Message) {
	t.Helper()
	if err := ws.WriteJSON(msg); err != nil {
		t.Fatalf("write failed: %v", err)
	}
}

func wsRecv(t *testing.T, ws *websocket.Conn) Message {
	t.Helper()
	ws.SetReadDeadline(time.Now().Add(2 * time.Second))
	var msg Message
	if err := ws.ReadJSON(&msg); err != nil {
		t.Fatalf("read failed: %v", err)
	}
	return msg
}

// === P0: End-to-end session lifecycle ===

func TestIntegration_SessionLifecycle(t *testing.T) {
	hub, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// PC creates session
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)
	if created.Type != MsgSessionCreated {
		t.Fatalf("expected session_created, got %s", created.Type)
	}
	if created.Token == "" {
		t.Fatal("expected token")
	}

	// Mobile joins
	mobile := dial(t, ts)
	defer mobile.Close()
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "mobile-1"})

	// Mobile should get joined + peer_joined
	joined := wsRecv(t, mobile)
	if joined.Type != MsgJoined {
		t.Fatalf("expected joined, got %s", joined.Type)
	}
	peerJoined := wsRecv(t, mobile)
	if peerJoined.Type != MsgPeerJoined {
		t.Fatalf("expected peer_joined, got %s", peerJoined.Type)
	}

	// PC should get peer_joined
	pcPeer := wsRecv(t, pc)
	if pcPeer.Type != MsgPeerJoined {
		t.Fatalf("expected peer_joined on PC, got %s", pcPeer.Type)
	}

	// Verify hub state
	hub.mu.RLock()
	sessionCount := len(hub.sessions)
	hub.mu.RUnlock()
	if sessionCount != 1 {
		t.Errorf("expected 1 session, got %d", sessionCount)
	}
}

// === P0: Message relay between peers ===

func TestIntegration_MessageRelay(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()
	mobile := dial(t, ts)
	defer mobile.Close()

	// Setup session
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	wsRecv(t, mobile) // joined
	wsRecv(t, mobile) // peer_joined
	wsRecv(t, pc)     // peer_joined

	// PC sends key_exchange → mobile receives it
	wsSend(t, pc, Message{Type: MsgKeyExchange, PublicKey: "test-key-123"})
	relayed := wsRecv(t, mobile)
	if relayed.Type != MsgKeyExchange {
		t.Fatalf("expected key_exchange, got %s", relayed.Type)
	}
	if relayed.PublicKey != "test-key-123" {
		t.Errorf("expected publicKey test-key-123, got %s", relayed.PublicKey)
	}

	// Mobile sends encrypted → PC receives it
	wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: "encrypted-data", Nonce: "nonce-1"})
	enc := wsRecv(t, pc)
	if enc.Type != MsgEncrypted {
		t.Fatalf("expected encrypted, got %s", enc.Type)
	}
	if enc.Payload != "encrypted-data" {
		t.Errorf("expected payload encrypted-data, got %s", enc.Payload)
	}
}

// === P0: Expired session rejection ===

func TestIntegration_ExpiredSessionRejected(t *testing.T) {
	hub, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)

	// Expire the session
	hub.mu.Lock()
	hub.sessions[created.Token].ExpiresAt = time.Now().Add(-1 * time.Hour)
	hub.mu.Unlock()

	// Mobile tries to join
	mobile := dial(t, ts)
	defer mobile.Close()
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	err := wsRecv(t, mobile)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P0: Peer disconnect notification ===

func TestIntegration_PeerDisconnect(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()
	mobile := dial(t, ts)

	// Setup session
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	wsRecv(t, mobile) // joined
	wsRecv(t, mobile) // peer_joined
	wsRecv(t, pc)     // peer_joined

	// Mobile disconnects
	mobile.Close()

	// PC should receive peer_left
	left := wsRecv(t, pc)
	if left.Type != MsgPeerLeft {
		t.Fatalf("expected peer_left, got %s", left.Type)
	}
}

// === P0: Mobile preemption ===

func TestIntegration_MobilePreemption(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// Setup session
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)

	// Mobile 1 joins
	m1 := dial(t, ts)
	defer m1.Close()
	wsSend(t, m1, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "device-A"})
	wsRecv(t, m1) // joined
	wsRecv(t, m1) // peer_joined
	wsRecv(t, pc) // peer_joined

	// Mobile 2 joins with same device ID → preempts mobile 1
	m2 := dial(t, ts)
	defer m2.Close()
	wsSend(t, m2, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "device-A"})

	// Mobile 1 should get preempted
	preempted := wsRecv(t, m1)
	if preempted.Type != MsgPreempted {
		t.Fatalf("expected preempted, got %s", preempted.Type)
	}

	// Mobile 2 should get joined
	joined := wsRecv(t, m2)
	if joined.Type != MsgJoined {
		t.Fatalf("expected joined, got %s", joined.Type)
	}
}

// === P0: Version mismatch ===

func TestIntegration_VersionMismatch(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgCreateSession, Version: "0.0.0"})
	err := wsRecv(t, pc)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P0: Session rejoin after disconnect ===

func TestIntegration_SessionRejoin(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)

	// Mobile joins
	mobile := dial(t, ts)
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	wsRecv(t, mobile) // joined
	wsRecv(t, mobile) // peer_joined
	wsRecv(t, pc)     // peer_joined

	// PC disconnects
	pc.Close()

	// Wait for unregister
	time.Sleep(100 * time.Millisecond)

	// New PC rejoins
	pc2 := dial(t, ts)
	defer pc2.Close()
	wsSend(t, pc2, Message{Type: MsgRejoinSession, Token: created.Token})
	rejoined := wsRecv(t, pc2)
	if rejoined.Type != MsgRejoined {
		t.Fatalf("expected rejoined, got %s", rejoined.Type)
	}

	// Mobile may get peer_left (from old PC) before peer_joined (from new PC)
	mobile.SetReadDeadline(time.Now().Add(2 * time.Second))
	for {
		msg := wsRecv(t, mobile)
		if msg.Type == MsgPeerJoined {
			break
		}
		if msg.Type == MsgPeerLeft {
			continue // skip stale peer_left
		}
		t.Fatalf("unexpected message: %s", msg.Type)
	}
	mobile.Close()
}

// === P0: Device code flow ===

func TestIntegration_DeviceCodeFlow(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// PC creates session
	wsSend(t, pc, Message{Type: MsgCreateSession})
	wsRecv(t, pc)

	// PC requests device code
	wsSend(t, pc, Message{Type: MsgDeviceCodeRequest})
	codeMsg := wsRecv(t, pc)
	if codeMsg.Type != MsgDeviceCodeCreated {
		t.Fatalf("expected device_code_created, got %s", codeMsg.Type)
	}
	if len(codeMsg.DeviceCode) != 8 {
		t.Errorf("expected 8-char code, got %d", len(codeMsg.DeviceCode))
	}

	// Mobile submits device code
	mobile := dial(t, ts)
	defer mobile.Close()
	wsSend(t, mobile, Message{Type: MsgDeviceCodeJoin, DeviceCode: codeMsg.DeviceCode, DeviceID: "mobile-1"})

	// Mobile gets pending with verify ID
	pending := wsRecv(t, mobile)
	if pending.Type != MsgDeviceCodePending {
		t.Fatalf("expected pending, got %s", pending.Type)
	}
	if pending.VerifyID == "" {
		t.Error("expected verify ID")
	}

	// PC gets pending with mobile info
	pcPending := wsRecv(t, pc)
	if pcPending.Type != MsgDeviceCodePending {
		t.Fatalf("expected pending on PC, got %s", pcPending.Type)
	}

	// PC confirms
	wsSend(t, pc, Message{Type: MsgDeviceCodeConfirm, DeviceCode: codeMsg.DeviceCode, PublicKey: "pc-key"})

	// Mobile gets joined
	joined := wsRecv(t, mobile)
	if joined.Type != MsgJoined {
		t.Fatalf("expected joined, got %s", joined.Type)
	}
	if !joined.Approved {
		t.Error("expected approved=true")
	}
}

// === P0: Concurrent sessions ===

func TestIntegration_ConcurrentSessions(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	const n = 10
	var wg sync.WaitGroup
	errors := make(chan string, n*2)

	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(idx int) {
			defer wg.Done()

			pc := dial(t, ts)
			defer pc.Close()

			wsSend(t, pc, Message{Type: MsgCreateSession})
			created := wsRecv(t, pc)
			if created.Type != MsgSessionCreated {
				errors <- "expected session_created"
				return
			}

			mobile := dial(t, ts)
			defer mobile.Close()
			wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d"})
			joined := wsRecv(t, mobile)
			if joined.Type != MsgJoined {
				errors <- "expected joined"
				return
			}
		}(i)
	}

	wg.Wait()
	close(errors)
	for e := range errors {
		t.Error(e)
	}
}

// === P0: Ping/pong ===

func TestIntegration_PingPong(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgPing})
	pong := wsRecv(t, pc)
	if pong.Type != MsgPong {
		t.Fatalf("expected pong, got %s", pong.Type)
	}
}

// === P0: Unknown message type ===

func TestIntegration_UnknownMessage(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: "nonexistent_xyz"})
	err := wsRecv(t, pc)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P0: Session expiry while connected ===

func TestIntegration_SessionExpiryWhileConnected(t *testing.T) {
	hub, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)

	// Expire session
	hub.mu.Lock()
	hub.sessions[created.Token].ExpiresAt = time.Now().Add(-1 * time.Second)
	hub.mu.Unlock()

	// Try rejoin
	pc2 := dial(t, ts)
	defer pc2.Close()
	wsSend(t, pc2, Message{Type: MsgRejoinSession, Token: created.Token})
	err := wsRecv(t, pc2)
	if err.Type != MsgError {
		t.Fatalf("expected error for expired rejoin, got %s", err.Type)
	}
}

// === P0: Large message relay ===

func TestIntegration_LargeMessageRelay(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()
	mobile := dial(t, ts)
	defer mobile.Close()

	// Setup
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	wsRecv(t, mobile)
	wsRecv(t, mobile)
	wsRecv(t, pc)

	// Send large payload (32KB - well within 64KB limit after JSON wrapping)
	largePayload := strings.Repeat("A", 32*1024)
	wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: largePayload, Nonce: "n1"})
	relayed := wsRecv(t, pc)
	if relayed.Payload != largePayload {
		t.Errorf("large payload mismatch: got %d bytes, expected %d", len(relayed.Payload), len(largePayload))
	}
}

// === P0: Multiple rapid messages ===

func TestIntegration_RapidMessages(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()
	mobile := dial(t, ts)
	defer mobile.Close()

	// Setup
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	wsRecv(t, mobile)
	wsRecv(t, mobile)
	wsRecv(t, pc)

	// Send 50 messages rapidly from mobile to PC
	const count = 50
	go func() {
		for i := 0; i < count; i++ {
			wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: "msg", Nonce: "n"})
		}
	}()

	// PC should receive all 50
	received := 0
	pc.SetReadDeadline(time.Now().Add(5 * time.Second))
	for received < count {
		var msg Message
		if err := pc.ReadJSON(&msg); err != nil {
			t.Fatalf("read failed at message %d: %v", received, err)
		}
		if msg.Type == MsgEncrypted {
			received++
		}
	}
	if received != count {
		t.Errorf("expected %d messages, got %d", count, received)
	}
}

// === P1: Message serialization roundtrip under load ===

func TestIntegration_MessageSerializationRoundtrip(t *testing.T) {
	msgs := []Message{
		{Type: MsgCreateSession, Version: ProtocolVersion},
		{Type: MsgSessionCreated, Token: "abc", Version: ProtocolVersion, MinVersion: "1.0.0", ExpiresAt: time.Now().Format(time.RFC3339)},
		{Type: MsgJoinSession, Token: "abc", DeviceID: "d1", Role: "mobile"},
		{Type: MsgKeyExchange, PublicKey: "base64key=="},
		{Type: MsgEncrypted, Payload: "encrypted", Nonce: "nonce", Seq: intPtr(42)},
		{Type: MsgPeerJoined, Role: "pc", DeviceID: "device-1"},
		{Type: MsgError, Error: "something went wrong"},
		{Type: MsgDeviceCodeCreated, DeviceCode: "ABCD1234"},
	}

	for i, msg := range msgs {
		data, err := json.Marshal(msg)
		if err != nil {
			t.Fatalf("msg %d: marshal error: %v", i, err)
		}
		var roundtrip Message
		if err := json.Unmarshal(data, &roundtrip); err != nil {
			t.Fatalf("msg %d: unmarshal error: %v", i, err)
		}
		if roundtrip.Type != msg.Type {
			t.Errorf("msg %d: type mismatch", i)
		}
	}
}
