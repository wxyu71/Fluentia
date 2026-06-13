package main

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestMessageSerialization(t *testing.T) {
	tests := []struct {
		name string
		msg  Message
	}{
		{
			name: "create_session",
			msg:  Message{Type: MsgCreateSession, Version: ProtocolVersion},
		},
		{
			name: "session_created",
			msg:  Message{Type: MsgSessionCreated, Token: "abc123", Version: ProtocolVersion},
		},
		{
			name: "join_session",
			msg:  Message{Type: MsgJoinSession, Token: "abc123", DeviceID: "device-1"},
		},
		{
			name: "key_exchange",
			msg:  Message{Type: MsgKeyExchange, PublicKey: "base64key"},
		},
		{
			name: "encrypted",
			msg:  Message{Type: MsgEncrypted, Payload: "encrypted-data", Nonce: "nonce-data"},
		},
		{
			name: "error",
			msg:  Message{Type: MsgError, Error: "something went wrong"},
		},
		{
			name: "device_code_join",
			msg:  Message{Type: MsgDeviceCodeJoin, DeviceCode: "ABCD1234", DeviceID: "d1", UserAgent: "Mozilla/5.0"},
		},
		{
			name: "seq_pointer",
			msg:  Message{Type: MsgEncrypted, Payload: "data", Seq: intPtr(42)},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			data, err := json.Marshal(tt.msg)
			if err != nil {
				t.Fatalf("marshal error: %v", err)
			}

			var roundtrip Message
			if err := json.Unmarshal(data, &roundtrip); err != nil {
				t.Fatalf("unmarshal error: %v", err)
			}

			if roundtrip.Type != tt.msg.Type {
				t.Errorf("Type: got %q, want %q", roundtrip.Type, tt.msg.Type)
			}
			if roundtrip.Token != tt.msg.Token {
				t.Errorf("Token: got %q, want %q", roundtrip.Token, tt.msg.Token)
			}
			if roundtrip.Payload != tt.msg.Payload {
				t.Errorf("Payload: got %q, want %q", roundtrip.Payload, tt.msg.Payload)
			}
			if roundtrip.PublicKey != tt.msg.PublicKey {
				t.Errorf("PublicKey: got %q, want %q", roundtrip.PublicKey, tt.msg.PublicKey)
			}
			if tt.msg.Seq != nil && (roundtrip.Seq == nil || *roundtrip.Seq != *tt.msg.Seq) {
				t.Errorf("Seq: got %v, want %v", roundtrip.Seq, tt.msg.Seq)
			}
		})
	}
}

func TestMessageOmitEmpty(t *testing.T) {
	msg := Message{Type: MsgPing}
	data, err := json.Marshal(msg)
	if err != nil {
		t.Fatalf("marshal error: %v", err)
	}

	jsonStr := string(data)
	// Should not contain empty optional fields
	if strings.Contains(jsonStr, `"token"`) {
		t.Error("empty token should be omitted")
	}
	if strings.Contains(jsonStr, `"payload"`) {
		t.Error("empty payload should be omitted")
	}
	if strings.Contains(jsonStr, `"error"`) {
		t.Error("empty error should be omitted")
	}
}

func TestProtocolVersion(t *testing.T) {
	if ProtocolVersion == "" {
		t.Fatal("ProtocolVersion should not be empty")
	}
	if ProtocolVersion != "1.7.4" {
		t.Errorf("expected version 1.7.3, got %s", ProtocolVersion)
	}
}

func TestMessageConstants(t *testing.T) {
	// Verify all message type constants are non-empty
	constants := map[string]string{
		"create_session":      MsgCreateSession,
		"session_created":     MsgSessionCreated,
		"join_session":        MsgJoinSession,
		"joined":              MsgJoined,
		"rejoin_session":      MsgRejoinSession,
		"rejoined":            MsgRejoined,
		"peer_joined":         MsgPeerJoined,
		"peer_left":           MsgPeerLeft,
		"preempted":           MsgPreempted,
		"key_exchange":        MsgKeyExchange,
		"encrypted":           MsgEncrypted,
		"ping":                MsgPing,
		"pong":                MsgPong,
		"error":               MsgError,
		"device_code_request": MsgDeviceCodeRequest,
		"device_code_created": MsgDeviceCodeCreated,
		"device_code_join":    MsgDeviceCodeJoin,
		"device_code_pending": MsgDeviceCodePending,
		"device_code_confirm": MsgDeviceCodeConfirm,
		"device_code_reject":  MsgDeviceCodeReject,
	}

	for expected, actual := range constants {
		if actual != expected {
			t.Errorf("Msg constant: expected %q, got %q", expected, actual)
		}
	}
}

func intPtr(i int) *int {
	return &i
}
