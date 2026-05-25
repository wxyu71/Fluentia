package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestSaveAndLoadSessions(t *testing.T) {
	dir := t.TempDir()
	storePath := filepath.Join(dir, "sessions.json")

	h := newTestHub()
	h.SessionStorePath = storePath

	// Add a session
	session := &Session{
		Token:     "test-token-abc",
		CreatedAt: time.Now(),
		ExpiresAt: time.Now().Add(7 * 24 * time.Hour),
	}
	h.sessions[session.Token] = session
	h.persistedSessions[tokenFingerprint(session.Token)] = persistedSession{
		TokenHash: tokenFingerprint(session.Token),
		CreatedAt: session.CreatedAt,
		ExpiresAt: session.ExpiresAt,
	}

	// Save
	h.saveSessionsLocked()

	// Verify file exists
	if _, err := os.Stat(storePath); os.IsNotExist(err) {
		t.Fatal("session file not created")
	}

	// Load into a new hub
	h2 := newTestHub()
	h2.SessionStorePath = storePath
	if err := h2.LoadPersistedSessions(); err != nil {
		t.Fatalf("failed to load sessions: %v", err)
	}

	// Verify the session was loaded
	fp := tokenFingerprint("test-token-abc")
	h2.mu.RLock()
	entry, ok := h2.persistedSessions[fp]
	h2.mu.RUnlock()
	if !ok {
		t.Fatal("session not loaded")
	}
	if entry.TokenHash != fp {
		t.Errorf("expected token hash %s, got %s", fp, entry.TokenHash)
	}
}

func TestLoadPersistedSessions_Expired(t *testing.T) {
	dir := t.TempDir()
	storePath := filepath.Join(dir, "sessions.json")

	// Write an expired session directly to file
	expired := []persistedSession{
		{
			TokenHash: "expired-hash",
			CreatedAt: time.Now().Add(-10 * 24 * time.Hour),
			ExpiresAt: time.Now().Add(-3 * 24 * time.Hour), // expired 3 days ago
		},
	}
	data, _ := json.MarshalIndent(expired, "", "  ")
	os.WriteFile(storePath, data, 0o600)

	h := newTestHub()
	h.SessionStorePath = storePath
	if err := h.LoadPersistedSessions(); err != nil {
		t.Fatalf("failed to load sessions: %v", err)
	}

	// Expired session should not be loaded
	h.mu.RLock()
	_, ok := h.persistedSessions["expired-hash"]
	h.mu.RUnlock()
	if ok {
		t.Error("expired session should not be loaded")
	}
}

func TestLoadPersistedSessions_MissingFile(t *testing.T) {
	h := newTestHub()
	h.SessionStorePath = "/nonexistent/path/sessions.json"
	if err := h.LoadPersistedSessions(); err != nil {
		t.Fatalf("should not error on missing file, got: %v", err)
	}
}

func TestLoadPersistedSessions_EmptyPath(t *testing.T) {
	h := newTestHub()
	h.SessionStorePath = ""
	if err := h.LoadPersistedSessions(); err != nil {
		t.Fatalf("should not error on empty path, got: %v", err)
	}
}

func TestSaveSessionsLocked_EmptyPath(t *testing.T) {
	h := newTestHub()
	h.SessionStorePath = ""
	// Should not panic
	h.saveSessionsLocked()
}

func TestSaveSessionsLocked_CreatesDirectory(t *testing.T) {
	dir := t.TempDir()
	storePath := filepath.Join(dir, "subdir", "sessions.json")

	h := newTestHub()
	h.SessionStorePath = storePath

	session := &Session{
		Token:     "test",
		CreatedAt: time.Now(),
		ExpiresAt: time.Now().Add(1 * time.Hour),
	}
	h.sessions[session.Token] = session

	h.saveSessionsLocked()

	if _, err := os.Stat(storePath); os.IsNotExist(err) {
		t.Fatal("session file not created in nested directory")
	}
}
