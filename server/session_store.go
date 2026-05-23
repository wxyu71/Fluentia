package main

import (
	"encoding/json"
	"errors"
	"log"
	"os"
	"path/filepath"
	"strings"
	"time"
)

type persistedSession struct {
	Token     string    `json:"token"`
	CreatedAt time.Time `json:"createdAt"`
	ExpiresAt time.Time `json:"expiresAt"`
}

func (h *Hub) LoadPersistedSessions() error {
	if strings.TrimSpace(h.SessionStorePath) == "" {
		return nil
	}

	data, err := os.ReadFile(h.SessionStorePath)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil
		}
		return err
	}

	var persisted []persistedSession
	if err := json.Unmarshal(data, &persisted); err != nil {
		return err
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	now := time.Now()
	for _, entry := range persisted {
		if entry.Token == "" || !entry.ExpiresAt.After(now) {
			continue
		}

		h.sessions[entry.Token] = &Session{
			Token:     entry.Token,
			CreatedAt: entry.CreatedAt,
			ExpiresAt: entry.ExpiresAt,
		}
	}

	if len(persisted) > 0 {
		log.Printf("Loaded %d persisted session(s)", len(h.sessions))
	}

	return nil
}

func (h *Hub) saveSessionsLocked() {
	if strings.TrimSpace(h.SessionStorePath) == "" {
		return
	}

	snapshot := make([]persistedSession, 0, len(h.sessions))
	for _, session := range h.sessions {
		if session == nil || h.sessionExpiredLocked(session) {
			continue
		}

		snapshot = append(snapshot, persistedSession{
			Token:     session.Token,
			CreatedAt: session.CreatedAt,
			ExpiresAt: session.ExpiresAt,
		})
	}

	if err := os.MkdirAll(filepath.Dir(h.SessionStorePath), 0o755); err != nil {
		log.Printf("failed to create session store directory: %v", err)
		return
	}

	payload, err := json.MarshalIndent(snapshot, "", "  ")
	if err != nil {
		log.Printf("failed to marshal persisted sessions: %v", err)
		return
	}

	tempPath := h.SessionStorePath + ".tmp"
	if err := os.WriteFile(tempPath, payload, 0o600); err != nil {
		log.Printf("failed to write persisted sessions: %v", err)
		return
	}

	if err := os.Rename(tempPath, h.SessionStorePath); err != nil {
		log.Printf("failed to replace persisted sessions: %v", err)
	}
}
