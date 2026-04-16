package main

import (
	"crypto/rand"
	"encoding/hex"
	"time"
)

// Room represents a paired session between a PC client and a mobile client.
type Room struct {
	Token     string
	PC        *Client
	Mobile    *Client
	CreatedAt time.Time
}

// NewRoom creates a new room with a random token, owned by the given PC client.
func NewRoom(pc *Client) *Room {
	return &Room{
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
