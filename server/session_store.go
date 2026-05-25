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
	ExpiresAt time.Time `json:"expiresAt"`
	CreatedAt time.Time `json:"createdAt"`
	TokenHash string    `json:"tokenHash"`
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
	loadedCount := 0
	for _, entry := range persisted {
		if entry.TokenHash == "" || !entry.ExpiresAt.After(now) {
			continue
		}

		h.persistedSessions[entry.TokenHash] = entry
		loadedCount += 1
	}

	if loadedCount > 0 {
		log.Printf("Loaded %d persisted session fingerprint(s)", loadedCount)
	}

	return nil
}

func (h *Hub) saveSessionsLocked() {
	if strings.TrimSpace(h.SessionStorePath) == "" {
		return
	}

	now := time.Now()
	snapshotMap := make(map[string]persistedSession, len(h.persistedSessions)+len(h.sessions))

	for fingerprint, entry := range h.persistedSessions {
		if entry.ExpiresAt.After(now) {
			snapshotMap[fingerprint] = entry
		}
	}

	for _, session := range h.sessions {
		if session == nil || h.sessionExpiredLocked(session) {
			continue
		}

		fingerprint := tokenFingerprint(session.Token)
		snapshotMap[fingerprint] = persistedSession{
			TokenHash: fingerprint,
			CreatedAt: session.CreatedAt,
			ExpiresAt: session.ExpiresAt,
		}
	}

	snapshot := make([]persistedSession, 0, len(snapshotMap))
	for _, entry := range snapshotMap {
		snapshot = append(snapshot, entry)
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
