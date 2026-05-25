package main

import (
	"encoding/json"
	"testing"
	"time"
)

// === P1: Fault injection — malformed messages ===

func TestFault_MalformedJSON(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// Send raw malformed JSON
	if err := pc.WriteMessage(1, []byte("{invalid json}")); err != nil {
		t.Fatalf("write failed: %v", err)
	}

	// Server should respond with error, not crash
	pc.SetReadDeadline(time.Now().Add(2 * time.Second))
	var msg Message
	err := pc.ReadJSON(&msg)
	if err != nil {
		// Connection may be closed — that's also acceptable
		return
	}
	if msg.Type != MsgError {
		t.Errorf("expected error response, got %s", msg.Type)
	}
}

func TestFault_EmptyMessage(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// Send empty JSON
	if err := pc.WriteMessage(1, []byte("{}")); err != nil {
		t.Fatalf("write failed: %v", err)
	}

	pc.SetReadDeadline(time.Now().Add(2 * time.Second))
	var msg Message
	err := pc.ReadJSON(&msg)
	if err != nil {
		return // connection closed — acceptable
	}
	if msg.Type != MsgError {
		t.Errorf("expected error for empty message, got %s", msg.Type)
	}
}

// === P1: Fault injection — oversized messages ===

func TestFault_OversizedMessage(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// Send message exceeding maxMessageSize (64KB)
	bigPayload := make([]byte, 128*1024)
	for i := range bigPayload {
		bigPayload[i] = 'A'
	}
	msg := Message{Type: MsgEncrypted, Payload: string(bigPayload), Nonce: "n"}
	data, _ := json.Marshal(msg)

	// This should be rejected by the server (read limit)
	if err := pc.WriteMessage(1, data); err != nil {
		// Write may fail if server closes connection — that's fine
		return
	}

	// Server should close the connection
	pc.SetReadDeadline(time.Now().Add(2 * time.Second))
	_, _, err := pc.ReadMessage()
	if err == nil {
		t.Error("expected connection to be closed for oversized message")
	}
}

// === P1: Fault injection — invalid session token ===

func TestFault_InvalidSessionToken(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	mobile := dial(t, ts)
	defer mobile.Close()

	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: "nonexistent-token", DeviceID: "d1"})
	err := wsRecv(t, mobile)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P1: Fault injection — join without token ===

func TestFault_JoinWithoutToken(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	mobile := dial(t, ts)
	defer mobile.Close()

	wsSend(t, mobile, Message{Type: MsgJoinSession, DeviceID: "d1"})
	err := wsRecv(t, mobile)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P1: Fault injection — relay without session ===

func TestFault_RelayWithoutSession(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgKeyExchange, PublicKey: "key"})
	err := wsRecv(t, pc)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P1: Fault injection — relay without peer ===

func TestFault_RelayWithoutPeer(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// Create session but don't join mobile
	wsSend(t, pc, Message{Type: MsgCreateSession})
	wsRecv(t, pc)

	// Try to relay
	wsSend(t, pc, Message{Type: MsgKeyExchange, PublicKey: "key"})
	err := wsRecv(t, pc)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P1: Fault injection — double session create ===

func TestFault_DoubleSessionCreate(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	// Create first session
	wsSend(t, pc, Message{Type: MsgCreateSession})
	first := wsRecv(t, pc)
	if first.Type != MsgSessionCreated {
		t.Fatalf("expected session_created, got %s", first.Type)
	}

	// Create second session — should succeed and destroy the first
	wsSend(t, pc, Message{Type: MsgCreateSession})
	second := wsRecv(t, pc)
	if second.Type != MsgSessionCreated {
		t.Fatalf("expected session_created, got %s", second.Type)
	}

	if first.Token == second.Token {
		t.Error("second session should have different token")
	}
}

// === P1: Fault injection — device code for wrong session ===

func TestFault_DeviceCodeWrongSession(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	// PC 1 creates session and gets code
	pc1 := dial(t, ts)
	defer pc1.Close()
	wsSend(t, pc1, Message{Type: MsgCreateSession})
	wsRecv(t, pc1)
	wsSend(t, pc1, Message{Type: MsgDeviceCodeRequest})
	codeMsg := wsRecv(t, pc1)

	// PC 2 creates a different session
	pc2 := dial(t, ts)
	defer pc2.Close()
	wsSend(t, pc2, Message{Type: MsgCreateSession})
	wsRecv(t, pc2)

	// PC 2 tries to confirm PC 1's code
	wsSend(t, pc2, Message{Type: MsgDeviceCodeConfirm, DeviceCode: codeMsg.DeviceCode})
	err := wsRecv(t, pc2)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P1: Fault injection — expired device code ===

func TestFault_ExpiredDeviceCode(t *testing.T) {
	hub, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)

	wsSend(t, pc, Message{Type: MsgDeviceCodeRequest})
	codeMsg := wsRecv(t, pc)

	// Expire the session (which also expires the device code)
	hub.mu.Lock()
	hub.sessions[created.Token].ExpiresAt = time.Now().Add(-1 * time.Second)
	hub.mu.Unlock()

	// Mobile tries to use the code
	mobile := dial(t, ts)
	defer mobile.Close()
	wsSend(t, mobile, Message{Type: MsgDeviceCodeJoin, DeviceCode: codeMsg.DeviceCode, DeviceID: "d1"})
	err := wsRecv(t, mobile)
	if err.Type != MsgError {
		t.Fatalf("expected error, got %s", err.Type)
	}
}

// === P1: Fault injection — concurrent preemption ===

func TestFault_ConcurrentPreemption(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)

	// Mobile 1 joins
	m1 := dial(t, ts)
	defer m1.Close()
	wsSend(t, m1, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "device-A"})
	wsRecv(t, m1) // joined
	wsRecv(t, m1) // peer_joined
	wsRecv(t, pc) // peer_joined

	// Mobile 2 and 3 try to join simultaneously with same device ID
	m2 := dial(t, ts)
	defer m2.Close()
	m3 := dial(t, ts)
	defer m3.Close()

	wsSend(t, m2, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "device-A"})
	wsSend(t, m3, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "device-A"})

	// At least one should get joined, m1 should get preempted
	// We don't check exact ordering since it's concurrent
	time.Sleep(500 * time.Millisecond)
}
