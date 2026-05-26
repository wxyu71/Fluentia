package main

import (
	"encoding/json"
	"fmt"
	"os"
	"runtime"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func readFile(path string) ([]byte, error) {
	return os.ReadFile(path)
}

// === K: Server stress tests (short burst, < 1 minute each) ===

func TestStress_20SessionsWithMessage(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	const n = 20
	var wg sync.WaitGroup
	var successCount int32

	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(idx int) {
			defer wg.Done()

			pc := dial(t, ts)
			defer pc.Close()

			wsSend(t, pc, Message{Type: MsgCreateSession})
			pc.SetReadDeadline(time.Now().Add(5 * time.Second))
			var created Message
			if err := pc.ReadJSON(&created); err != nil || created.Type != MsgSessionCreated {
				return
			}

			mobile := dial(t, ts)
			defer mobile.Close()
			wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: fmt.Sprintf("d-%d", idx)})
			mobile.SetReadDeadline(time.Now().Add(5 * time.Second))
			var joined Message
			if err := mobile.ReadJSON(&joined); err != nil || joined.Type != MsgJoined {
				return
			}

			// Send a message through the relay
			wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: fmt.Sprintf("msg-%d", idx), Nonce: "n"})
			pc.SetReadDeadline(time.Now().Add(5 * time.Second))
			// Read until we get the encrypted message (may get peer_joined first)
			for {
				var relayed Message
				if err := pc.ReadJSON(&relayed); err != nil {
					return
				}
				if relayed.Type == MsgEncrypted {
					if relayed.Payload == fmt.Sprintf("msg-%d", idx) {
						atomic.AddInt32(&successCount, 1)
					}
					return
				}
			}
		}(i)
	}

	wg.Wait()

	if int(atomic.LoadInt32(&successCount)) < n/2 {
		t.Errorf("expected at least %d successful relays, got %d", n/2, atomic.LoadInt32(&successCount))
	}
}

func TestStress_HighFrequencyMessages(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()
	mobile := dial(t, ts)
	defer mobile.Close()

	// Setup
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	wsRecv(t, mobile)
	wsRecv(t, mobile)
	wsRecv(t, pc)

	// Send 500 messages as fast as possible (simulates ~100/sec for 5 sec)
	const count = 500
	go func() {
		for i := 0; i < count; i++ {
			wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: fmt.Sprintf("m-%d", i), Nonce: "n"})
		}
	}()

	received := int32(0)
	pc.SetReadDeadline(time.Now().Add(10 * time.Second))
	for atomic.LoadInt32(&received) < int32(count) {
		var msg Message
		if err := pc.ReadJSON(&msg); err != nil {
			t.Fatalf("read failed at %d: %v", atomic.LoadInt32(&received), err)
		}
		if msg.Type == MsgEncrypted {
			atomic.AddInt32(&received, 1)
		}
	}

	if int(atomic.LoadInt32(&received)) != count {
		t.Errorf("expected %d, got %d", count, atomic.LoadInt32(&received))
	}
}

func TestStress_RapidConnectDisconnect(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	// 20 clients connect and immediately disconnect, 3 rounds
	for round := 0; round < 3; round++ {
		var wg sync.WaitGroup
		for i := 0; i < 20; i++ {
			wg.Add(1)
			go func() {
				defer wg.Done()
				ws := dial(t, ts)
				wsSend(t, ws, Message{Type: MsgPing})
				ws.Close()
			}()
		}
		wg.Wait()
	}
}

func TestStress_ConcurrentSessionPersistence(t *testing.T) {
	dir := t.TempDir()
	storePath := dir + "/sessions.json"

	hub := NewHub(1)
	hub.SessionStorePath = storePath

	// Create 50 sessions and save concurrently
	var wg sync.WaitGroup
	for i := 0; i < 50; i++ {
		wg.Add(1)
		go func(idx int) {
			defer wg.Done()
			hub.mu.Lock()
			token := fmt.Sprintf("token-%d", idx)
			hub.sessions[token] = &Session{
				Token:     token,
				CreatedAt: time.Now(),
				ExpiresAt: time.Now().Add(7 * 24 * time.Hour),
			}
			hub.persistedSessions[tokenFingerprint(token)] = persistedSession{
				TokenHash: tokenFingerprint(token),
				CreatedAt: time.Now(),
				ExpiresAt: time.Now().Add(7 * 24 * time.Hour),
			}
			hub.saveSessionsLocked()
			hub.mu.Unlock()
		}(i)
	}
	wg.Wait()

	// Verify file is valid JSON
	data, err := readFile(storePath)
	if err != nil {
		t.Fatalf("failed to read: %v", err)
	}
	var sessions []persistedSession
	if err := json.Unmarshal(data, &sessions); err != nil {
		t.Fatalf("corrupted JSON: %v", err)
	}
}

func TestStress_MemoryReclamation(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	// Force GC and measure baseline
	runtime.GC()
	runtime.GC()
	var before runtime.MemStats
	runtime.ReadMemStats(&before)

	// Create and destroy 100 sessions
	for i := 0; i < 100; i++ {
		pc := dial(t, ts)
		wsSend(t, pc, Message{Type: MsgCreateSession})
		wsRecv(t, pc)
		pc.Close()
	}

	// Wait for cleanup
	time.Sleep(500 * time.Millisecond)
	runtime.GC()
	runtime.GC()

	var after runtime.MemStats
	runtime.ReadMemStats(&after)

	// Memory should not have grown significantly (allow 10MB overhead)
	growth := int64(after.HeapAlloc) - int64(before.HeapAlloc)
	if growth > 10*1024*1024 {
		t.Errorf("memory grew by %d bytes (> 10MB)", growth)
	}
}

func TestStress_SessionCreateDestroyCycle(t *testing.T) {
	hub, ts := newTestServer()
	defer ts.Close()

	// Rapidly create and destroy sessions
	for i := 0; i < 200; i++ {
		pc := dial(t, ts)
		wsSend(t, pc, Message{Type: MsgCreateSession})
		created := wsRecv(t, pc)

		// Immediately expire
		hub.mu.Lock()
		if s, ok := hub.sessions[created.Token]; ok {
			s.ExpiresAt = time.Now().Add(-1 * time.Second)
		}
		hub.mu.Unlock()

		pc.Close()
	}

	// Hub should still be functional
	pc := dial(t, ts)
	defer pc.Close()
	wsSend(t, pc, Message{Type: MsgCreateSession})
	msg := wsRecv(t, pc)
	if msg.Type != MsgSessionCreated {
		t.Errorf("expected session_created after stress, got %s", msg.Type)
	}
}
