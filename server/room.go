package main

import (
	"crypto/rand"
	"encoding/hex"
	"time"
)

// Session represents a paired connection between a PC client and a mobile client.
type Session struct {
	Token      string
	PC         *Client
	Mobile     *Client
	CreatedAt  time.Time
	ExpiresAt  time.Time
	GraceTimer *time.Timer // set when PC disconnects; fires when the reusable session finally expires
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
	b := make([]byte, 8) // 8 bytes = 16 hex chars (64-bit entropy, sufficient for sessions)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}
