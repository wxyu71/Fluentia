package main

import (
	"crypto/rand"
	"encoding/hex"
	"time"
)

// Session represents a paired connection between a PC client and a mobile client.
type Session struct {
	CreatedAt  time.Time
	ExpiresAt  time.Time
	PC         *Client
	Mobile     *Client
	GraceTimer *time.Timer
	Token      string
}

// NewSession creates a new session with a random token, owned by the given PC client.
func NewSession(pc *Client, maxAge time.Duration) *Session {
	expiresAt := time.Now().Add(maxAge)
	return &Session{
		Token:     generateToken(),
		PC:        pc,
		CreatedAt: time.Now(),
		ExpiresAt: expiresAt,
	}
}

func generateToken() string {
	b := make([]byte, 16) // [M5] 16 bytes = 32 hex chars (128-bit entropy)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}
