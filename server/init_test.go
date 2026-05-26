package main

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

// === H: Startup & Initialization ===

func TestInit_ColdStart_SessionCreatedWithin1Second(t *testing.T) {
	hub := NewHub(1)
	start := time.Now()

	// Simulate cold start: create session immediately
	session := NewSession(&Client{hub: hub, send: make(chan []byte, 1)}, hub.SessionMaxAge)
	elapsed := time.Since(start)

	if elapsed > time.Second {
		t.Errorf("session creation took %v, expected < 1s", elapsed)
	}
	if session.Token == "" {
		t.Fatal("session token should not be empty")
	}
}

func TestInit_DefaultConfig_AllFieldsPopulated(t *testing.T) {
	cfg := loadConfig()

	// All required fields should have defaults
	if cfg.Port == "" {
		t.Error("Port should have default")
	}
	if cfg.StaticDir == "" {
		t.Error("StaticDir should have default")
	}
	if cfg.MinVersion == "" {
		t.Error("MinVersion should have default")
	}
	if cfg.SessionStorePath == "" {
		t.Error("SessionStorePath should have default")
	}
	if cfg.MaxFileMB == 0 {
		t.Error("MaxFileMB should have non-zero default")
	}
	if cfg.MobileExpiry == 0 {
		t.Error("MobileExpiry should have non-zero default")
	}
	if cfg.SessionMaxAge == 0 {
		t.Error("SessionMaxAge should have non-zero default")
	}
}

func TestInit_MissingSessionFile_LoadsCleanly(t *testing.T) {
	hub := NewHub(1)
	hub.SessionStorePath = "/nonexistent/path/sessions.json"

	err := hub.LoadPersistedSessions()
	if err != nil {
		t.Fatalf("should not error on missing file, got: %v", err)
	}
}

func TestInit_CorruptedSessionFile_ReturnsError(t *testing.T) {
	dir := t.TempDir()
	storePath := filepath.Join(dir, "sessions.json")

	// Write invalid JSON
	os.WriteFile(storePath, []byte("{corrupted json!!!"), 0o600)

	hub := NewHub(1)
	hub.SessionStorePath = storePath

	err := hub.LoadPersistedSessions()
	if err == nil {
		t.Fatal("should return error for corrupted file")
	}
}

func TestInit_EmptySessionFile_LoadsCleanly(t *testing.T) {
	dir := t.TempDir()
	storePath := filepath.Join(dir, "sessions.json")

	os.WriteFile(storePath, []byte("[]"), 0o600)

	hub := NewHub(1)
	hub.SessionStorePath = storePath

	if err := hub.LoadPersistedSessions(); err != nil {
		t.Fatalf("should handle empty array, got: %v", err)
	}
}

func TestInit_CorruptedPersistedSession_ReturnsError(t *testing.T) {
	dir := t.TempDir()
	storePath := filepath.Join(dir, "sessions.json")

	// Write JSON with invalid time format
	data := `[
		{"tokenHash": "abc", "createdAt": "2024-01-01T00:00:00Z", "expiresAt": "2099-01-01T00:00:00Z"},
		{"tokenHash": "", "createdAt": "invalid", "expiresAt": "invalid"}
	]`
	os.WriteFile(storePath, []byte(data), 0o600)

	hub := NewHub(1)
	hub.SessionStorePath = storePath

	// Corrupted time format causes parse error
	err := hub.LoadPersistedSessions()
	if err == nil {
		t.Fatal("should return error for corrupted time format")
	}
}

func TestInit_HubDefaults_AreReasonable(t *testing.T) {
	hub := NewHub(0) // should default to 7 days

	if hub.SessionMaxAge != 7*24*time.Hour {
		t.Errorf("expected 7 days, got %v", hub.SessionMaxAge)
	}
	if hub.MinVersion != ProtocolVersion {
		t.Errorf("expected MinVersion %s, got %s", ProtocolVersion, hub.MinVersion)
	}
}

func TestInit_NegativeMaxAge_DefaultsTo7Days(t *testing.T) {
	hub := NewHub(-1)
	if hub.SessionMaxAge != 7*24*time.Hour {
		t.Errorf("expected 7 days, got %v", hub.SessionMaxAge)
	}
}

func TestInit_SessionStorePathEmpty_NoFileOperations(t *testing.T) {
	hub := NewHub(1)
	hub.SessionStorePath = ""

	// Should not panic on save
	hub.saveSessionsLocked()

	// Should not panic on load
	if err := hub.LoadPersistedSessions(); err != nil {
		t.Fatalf("should not error, got: %v", err)
	}
}

func TestInit_ConcurrentHubCreation_NoRace(t *testing.T) {
	const n = 20
	done := make(chan *Hub, n)
	for i := 0; i < n; i++ {
		go func() {
			hub := NewHub(1)
			done <- hub
		}()
	}
	for i := 0; i < n; i++ {
		hub := <-done
		if hub == nil {
			t.Fatal("hub should not be nil")
		}
	}
}
