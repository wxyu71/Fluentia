package main

import (
	"crypto/rand"
	"encoding/hex"
	"time"
)

// Session represents a paired connection between a PC client and a mobile client.
type Session struct {
	Token     string
	PC        *Client
	Mobile    *Client
	CreatedAt time.Time
}

// NewSession creates a new session with a random token, owned by the given PC client.
func NewSession(pc *Client) *Session {
	return &Session{
		Token:     generateToken(),
		PC:        pc,
		CreatedAt: time.Now(),
	}
}

func generateToken() string {
	b := make([]byte, 8) // 8 bytes = 16 hex chars (64-bit entropy, sufficient for sessions)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}
