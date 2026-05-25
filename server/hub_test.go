package main

import (
	"encoding/json"
	"testing"
	"time"
)

// newTestHub creates a Hub for testing with a short session max age.
func newTestHub() *Hub {
	h := NewHub(1) // 1 day
	h.SessionStorePath = "" // disable persistence for unit tests
	return h
}

// newTestClient creates a Client with a buffered send channel for inspecting sent messages.
func newTestClient(hub *Hub, role string) *Client {
	c := &Client{
		hub:  hub,
		send: make(chan []byte, 64),
	}
	hub.Register(c)
	return c
}

// readMessages drains the client's send channel and returns all messages.
func readMessages(c *Client) []Message {
	var msgs []Message
	for {
		select {
		case data, ok := <-c.send:
			if !ok {
				return msgs
			}
			var msg Message
			json.Unmarshal(data, &msg)
			msgs = append(msgs, msg)
		default:
			return msgs
		}
	}
}

// lastMessage returns the last message sent to a client, or a zero Message.
// It uses a short blocking read to handle messages that arrive slightly late.
func lastMessage(c *Client) Message {
	msgs := readMessages(c)
	if len(msgs) == 0 {
		// Try blocking briefly for async delivery
		select {
		case data, ok := <-c.send:
			if !ok {
				return Message{}
			}
			var msg Message
			json.Unmarshal(data, &msg)
			return msg
		case <-time.After(100 * time.Millisecond):
			return Message{}
		}
	}
	return msgs[len(msgs)-1]
}

func TestNewHub_DefaultSessionMaxAge(t *testing.T) {
	h := NewHub(0) // should default to 7
	if h.SessionMaxAge != 7*24*time.Hour {
		t.Errorf("expected 7 days, got %v", h.SessionMaxAge)
	}

	h2 := NewHub(-1)
	if h2.SessionMaxAge != 7*24*time.Hour {
		t.Errorf("expected 7 days for negative input, got %v", h2.SessionMaxAge)
	}

	h3 := NewHub(30)
	if h3.SessionMaxAge != 30*24*time.Hour {
		t.Errorf("expected 30 days, got %v", h3.SessionMaxAge)
	}
}

func TestGenerateDeviceCode(t *testing.T) {
	h := newTestHub()
	session := &Session{
		Token:     "test-token",
		ExpiresAt: time.Now().Add(1 * time.Hour),
	}
	pc := newTestClient(h, "pc")

	code := h.GenerateDeviceCode(session, pc)

	if len(code) != 8 {
		t.Errorf("expected 8-char code, got %d chars: %s", len(code), code)
	}

	// Verify it's stored
	h.mu.RLock()
	entry, ok := h.deviceCodes[code]
	h.mu.RUnlock()
	if !ok {
		t.Fatal("device code not stored in hub")
	}
	if entry.Session != session {
		t.Error("device code entry points to wrong session")
	}
	if entry.PC != pc {
		t.Error("device code entry points to wrong PC")
	}
}

func TestGenerateDeviceCode_ReplacesExistingForSameSession(t *testing.T) {
	h := newTestHub()
	session := &Session{
		Token:     "test-token",
		ExpiresAt: time.Now().Add(1 * time.Hour),
	}
	pc := newTestClient(h, "pc")

	code1 := h.GenerateDeviceCode(session, pc)
	code2 := h.GenerateDeviceCode(session, pc)

	if code1 == code2 {
		// Technically possible but astronomically unlikely
		t.Log("codes happened to be the same (very unlikely)")
	}

	// Old code should be deleted
	h.mu.RLock()
	_, ok := h.deviceCodes[code1]
	h.mu.RUnlock()
	if ok && code1 != code2 {
		t.Error("old device code was not cleaned up")
	}
}

func TestCheckDeviceCodeRateLimit(t *testing.T) {
	h := newTestHub()
	ip := "192.168.1.1"

	// First 5 attempts should be allowed
	for i := 0; i < 5; i++ {
		if h.CheckDeviceCodeRateLimit(ip) {
			t.Fatalf("attempt %d should not be rate limited", i+1)
		}
	}

	// 6th attempt should be rate limited
	if !h.CheckDeviceCodeRateLimit(ip) {
		t.Error("6th attempt should be rate limited")
	}

	// Different IP should not be affected
	if h.CheckDeviceCodeRateLimit("10.0.0.1") {
		t.Error("different IP should not be rate limited")
	}
}

func TestHandleMessage_VersionMismatch(t *testing.T) {
	h := newTestHub()
	c := newTestClient(h, "")

	h.HandleMessage(c, Message{
		Type:    MsgCreateSession,
		Version: "0.0.0", // wrong version
	})

	msg := lastMessage(c)
	if msg.Type != MsgError {
		t.Errorf("expected error, got %s", msg.Type)
	}
	if msg.Error == "" {
		t.Error("expected error message about version mismatch")
	}
}

func TestHandleCreateSession(t *testing.T) {
	h := newTestHub()
	pc := newTestClient(h, "")

	h.HandleMessage(pc, Message{Type: MsgCreateSession})

	msg := lastMessage(pc)
	if msg.Type != MsgSessionCreated {
		t.Fatalf("expected session_created, got %s", msg.Type)
	}
	if msg.Token == "" {
		t.Error("expected token in session_created")
	}
	if msg.Version != ProtocolVersion {
		t.Errorf("expected version %s, got %s", ProtocolVersion, msg.Version)
	}

	// Verify session exists in hub
	h.mu.RLock()
	session, ok := h.sessions[msg.Token]
	h.mu.RUnlock()
	if !ok {
		t.Fatal("session not stored in hub")
	}
	if session.PC != pc {
		t.Error("session PC doesn't match client")
	}
	if pc.role != "pc" {
		t.Errorf("expected role 'pc', got %q", pc.role)
	}
}

func TestHandleJoinSession_Success(t *testing.T) {
	h := newTestHub()
	pc := newTestClient(h, "")
	mobile := newTestClient(h, "")

	// PC creates session
	h.HandleMessage(pc, Message{Type: MsgCreateSession})
	created := lastMessage(pc)

	// Mobile joins
	h.HandleMessage(mobile, Message{
		Type:     MsgJoinSession,
		Token:    created.Token,
		DeviceID: "device-123",
	})

	// Mobile should get 'joined' and 'peer_joined' (PC is already connected)
	msgs := readMessages(mobile)
	foundJoined := false
	foundPeerJoined := false
	for _, m := range msgs {
		if m.Type == MsgJoined && m.Role == "mobile" {
			foundJoined = true
		}
		if m.Type == MsgPeerJoined && m.Role == "pc" {
			foundPeerJoined = true
		}
	}
	if !foundJoined {
		t.Error("mobile didn't receive joined message")
	}
	if !foundPeerJoined {
		t.Error("mobile didn't receive peer_joined for PC")
	}

	// PC should get 'peer_joined' for mobile
	pcMsgs := readMessages(pc)
	foundPCPeerJoined := false
	for _, m := range pcMsgs {
		if m.Type == MsgPeerJoined && m.Role == "mobile" {
			foundPCPeerJoined = true
		}
	}
	if !foundPCPeerJoined {
		t.Error("PC didn't receive peer_joined for mobile")
	}
}

func TestHandleJoinSession_SessionNotFound(t *testing.T) {
	h := newTestHub()
	mobile := newTestClient(h, "")

	h.HandleMessage(mobile, Message{
		Type:     MsgJoinSession,
		Token:    "nonexistent",
		DeviceID: "device-123",
	})

	msg := lastMessage(mobile)
	if msg.Type != MsgError {
		t.Errorf("expected error, got %s", msg.Type)
	}
}

func TestHandleJoinSession_Preempt(t *testing.T) {
	h := newTestHub()
	pc := newTestClient(h, "")
	mobile1 := newTestClient(h, "")
	mobile2 := newTestClient(h, "")

	// PC creates session
	h.HandleMessage(pc, Message{Type: MsgCreateSession})
	token := lastMessage(pc).Token

	// Mobile 1 joins
	h.HandleMessage(mobile1, Message{Type: MsgJoinSession, Token: token, DeviceID: "device-1"})
	readMessages(mobile1) // drain

	// Mobile 2 joins (should preempt mobile 1)
	h.HandleMessage(mobile2, Message{Type: MsgJoinSession, Token: token, DeviceID: "device-2"})

	// Mobile 1 should receive 'preempted'
	m1Msgs := readMessages(mobile1)
	foundPreempted := false
	for _, m := range m1Msgs {
		if m.Type == MsgPreempted {
			foundPreempted = true
		}
	}
	if !foundPreempted {
		t.Error("mobile1 didn't receive preempted message")
	}

	// Mobile 2 should be joined
	m2Msgs := readMessages(mobile2)
	foundJoined := false
	for _, m := range m2Msgs {
		if m.Type == MsgJoined {
			foundJoined = true
		}
	}
	if !foundJoined {
		t.Error("mobile2 didn't receive joined message")
	}
}

func TestHandleRelay_ForwardsToPeer(t *testing.T) {
	h := newTestHub()
	pc := newTestClient(h, "")
	mobile := newTestClient(h, "")

	// Setup session with both peers
	h.HandleMessage(pc, Message{Type: MsgCreateSession})
	token := lastMessage(pc).Token
	h.HandleMessage(mobile, Message{Type: MsgJoinSession, Token: token, DeviceID: "d1"})
	readMessages(pc)
	readMessages(mobile)

	// PC sends key_exchange, should be relayed to mobile
	h.HandleMessage(pc, Message{
		Type:      MsgKeyExchange,
		PublicKey: "test-key",
	})
	mobileMsgs := readMessages(mobile)
	foundRelay := false
	for _, m := range mobileMsgs {
		if m.Type == MsgKeyExchange && m.PublicKey == "test-key" {
			foundRelay = true
		}
	}
	if !foundRelay {
		t.Error("key_exchange not relayed to mobile")
	}

	// Mobile sends encrypted, should be relayed to PC
	h.HandleMessage(mobile, Message{
		Type:    MsgEncrypted,
		Payload: "encrypted-data",
	})
	pcMsgs := readMessages(pc)
	foundEncRelay := false
	for _, m := range pcMsgs {
		if m.Type == MsgEncrypted && m.Payload == "encrypted-data" {
			foundEncRelay = true
		}
	}
	if !foundEncRelay {
		t.Error("encrypted not relayed to PC")
	}
}

func TestHandleRelay_NoPeer(t *testing.T) {
	h := newTestHub()
	pc := newTestClient(h, "")

	h.HandleMessage(pc, Message{Type: MsgCreateSession})
	readMessages(pc)

	// PC sends key_exchange with no mobile connected
	h.HandleMessage(pc, Message{Type: MsgKeyExchange, PublicKey: "key"})
	msg := lastMessage(pc)
	if msg.Type != MsgError {
		t.Errorf("expected error, got %s", msg.Type)
	}
}

func TestHandleRelay_NotInSession(t *testing.T) {
	h := newTestHub()
	c := newTestClient(h, "")

	h.HandleMessage(c, Message{Type: MsgKeyExchange, PublicKey: "key"})
	msg := lastMessage(c)
	if msg.Type != MsgError {
		t.Errorf("expected error, got %s", msg.Type)
	}
}

func TestHandlePing(t *testing.T) {
	h := newTestHub()
	c := newTestClient(h, "")

	h.HandleMessage(c, Message{Type: MsgPing})
	msg := lastMessage(c)
	if msg.Type != MsgPong {
		t.Errorf("expected pong, got %s", msg.Type)
	}
}

func TestHandleUnknownMessage(t *testing.T) {
	h := newTestHub()
	c := newTestClient(h, "")

	h.HandleMessage(c, Message{Type: "unknown_type_xyz"})
	msg := lastMessage(c)
	if msg.Type != MsgError {
		t.Errorf("expected error, got %s", msg.Type)
	}
}

func TestTokenFingerprint(t *testing.T) {
	fp1 := tokenFingerprint("test-token")
	fp2 := tokenFingerprint("test-token")
	if fp1 != fp2 {
		t.Error("same token should produce same fingerprint")
	}

	fp3 := tokenFingerprint("other-token")
	if fp1 == fp3 {
		t.Error("different tokens should produce different fingerprints")
	}

	if len(fp1) != 64 { // SHA-256 hex = 64 chars
		t.Errorf("expected 64-char hex fingerprint, got %d chars", len(fp1))
	}
}

func TestGenerateAlphanumericCode(t *testing.T) {
	code := generateAlphanumericCode(8)
	if len(code) != 8 {
		t.Errorf("expected 8 chars, got %d", len(code))
	}

	// Should not contain confusing characters
	for _, c := range code {
		if c == '0' || c == 'O' || c == '1' || c == 'I' {
			t.Errorf("confusing character %q found in code", c)
		}
	}
}

func TestGenerateAlphanumericCode_Uniqueness(t *testing.T) {
	codes := make(map[string]bool)
	for i := 0; i < 100; i++ {
		code := generateAlphanumericCode(8)
		if codes[code] {
			t.Fatalf("duplicate code generated: %s", code)
		}
		codes[code] = true
	}
}
