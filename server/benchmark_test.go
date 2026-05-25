package main

import (
	"encoding/json"
	"fmt"
	"testing"
	"time"
)

// === P2: Performance benchmarks ===

func BenchmarkMessageMarshal(b *testing.B) {
	msg := Message{
		Type:    MsgEncrypted,
		Payload: "encrypted-data-payload-that-is-reasonably-long-for-a-typical-message",
		Nonce:   "nonce-value-1234567890abcdef",
		Seq:     intPtr(42),
	}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_, _ = json.Marshal(msg)
	}
}

func BenchmarkMessageUnmarshal(b *testing.B) {
	data, _ := json.Marshal(Message{
		Type:    MsgEncrypted,
		Payload: "encrypted-data-payload",
		Nonce:   "nonce-value",
		Seq:     intPtr(42),
	})
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var msg Message
		_ = json.Unmarshal(data, &msg)
	}
}

func BenchmarkTokenFingerprint(b *testing.B) {
	token := "abcdef1234567890"
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		tokenFingerprint(token)
	}
}

func BenchmarkGenerateToken(b *testing.B) {
	for i := 0; i < b.N; i++ {
		generateToken()
	}
}

func BenchmarkGenerateAlphanumericCode(b *testing.B) {
	for i := 0; i < b.N; i++ {
		generateAlphanumericCode(8)
	}
}

func BenchmarkShortToken(b *testing.B) {
	token := "abcdef1234567890abcdef1234567890"
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		shortToken(token)
	}
}

func BenchmarkHubSessionLookup(b *testing.B) {
	hub := NewHub(1)
	now := time.Now()
	for i := 0; i < 100; i++ {
		token := fmt.Sprintf("token-%d", i)
		hub.sessions[token] = &Session{
			Token:     token,
			CreatedAt: now,
			ExpiresAt: now.Add(7 * 24 * time.Hour),
		}
	}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		hub.mu.RLock()
		_ = hub.sessions[fmt.Sprintf("token-%d", i%100)]
		hub.mu.RUnlock()
	}
}

func BenchmarkConcurrentSessionLookup(b *testing.B) {
	hub := NewHub(1)
	now := time.Now()
	for i := 0; i < 100; i++ {
		token := fmt.Sprintf("token-%d", i)
		hub.sessions[token] = &Session{
			Token:     token,
			CreatedAt: now,
			ExpiresAt: now.Add(7 * 24 * time.Hour),
		}
	}
	b.ResetTimer()
	b.RunParallel(func(pb *testing.PB) {
		i := 0
		for pb.Next() {
			hub.mu.RLock()
			_ = hub.sessions[fmt.Sprintf("token-%d", i%100)]
			hub.mu.RUnlock()
			i++
		}
	})
}
