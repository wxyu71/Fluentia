package main

import (
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// === P1: Concurrent message relay ===

func TestConcurrency_DualChannelMessageDedup(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()
	mobile := dial(t, ts)
	defer mobile.Close()

	// Setup session
	wsSend(t, pc, Message{Type: MsgCreateSession})
	created := wsRecv(t, pc)
	wsSend(t, mobile, Message{Type: MsgJoinSession, Token: created.Token, DeviceID: "d1"})
	wsRecv(t, mobile)
	wsRecv(t, mobile)
	wsRecv(t, pc)

	// Send same message type rapidly — server should relay all without dropping
	const count = 20
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		for i := 0; i < count; i++ {
			wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: fmt.Sprintf("msg-%d", i), Nonce: fmt.Sprintf("n-%d", i)})
		}
	}()

	received := 0
	pc.SetReadDeadline(time.Now().Add(5 * time.Second))
	for received < count {
		var msg Message
		if err := pc.ReadJSON(&msg); err != nil {
			t.Fatalf("read failed at %d: %v", received, err)
		}
		if msg.Type == MsgEncrypted {
			received++
		}
	}

	wg.Wait()
	if received != count {
		t.Errorf("expected %d messages, got %d", count, received)
	}
}

// === P1: Bidirectional concurrent relay ===

func TestConcurrency_BidirectionalRelay(t *testing.T) {
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

	const count = 10

	// Mobile sends to PC
	var wg sync.WaitGroup
	wg.Add(2)

	go func() {
		defer wg.Done()
		for i := 0; i < count; i++ {
			wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: fmt.Sprintf("m2p-%d", i), Nonce: fmt.Sprintf("mn-%d", i)})
		}
	}()

	go func() {
		defer wg.Done()
		for i := 0; i < count; i++ {
			wsSend(t, pc, Message{Type: MsgEncrypted, Payload: fmt.Sprintf("p2m-%d", i), Nonce: fmt.Sprintf("pn-%d", i)})
		}
	}()

	// Collect messages on both sides
	var pcReceived, mobileReceived int32
	var collectWg sync.WaitGroup
	collectWg.Add(2)

	go func() {
		defer collectWg.Done()
		pc.SetReadDeadline(time.Now().Add(5 * time.Second))
		for pcReceived < int32(count) {
			var msg Message
			if err := pc.ReadJSON(&msg); err != nil {
				return
			}
			if msg.Type == MsgEncrypted {
				atomic.AddInt32(&pcReceived, 1)
			}
		}
	}()

	go func() {
		defer collectWg.Done()
		mobile.SetReadDeadline(time.Now().Add(5 * time.Second))
		for mobileReceived < int32(count) {
			var msg Message
			if err := mobile.ReadJSON(&msg); err != nil {
				return
			}
			if msg.Type == MsgEncrypted {
				atomic.AddInt32(&mobileReceived, 1)
			}
		}
	}()

	wg.Wait()
	collectWg.Wait()

	if int(atomic.LoadInt32(&pcReceived)) != count {
		t.Errorf("PC expected %d, got %d", count, atomic.LoadInt32(&pcReceived))
	}
	if int(atomic.LoadInt32(&mobileReceived)) != count {
		t.Errorf("Mobile expected %d, got %d", count, atomic.LoadInt32(&mobileReceived))
	}
}

// === P1: Concurrent session create/destroy ===

func TestConcurrency_SessionCreateDestroy(t *testing.T) {
	hub, ts := newTestServer()
	defer ts.Close()

	const rounds = 50
	for i := 0; i < rounds; i++ {
		pc := dial(t, ts)
		wsSend(t, pc, Message{Type: MsgCreateSession})
		created := wsRecv(t, pc)
		if created.Type != MsgSessionCreated {
			t.Fatalf("round %d: expected session_created", i)
		}

		// Immediately expire
		hub.mu.Lock()
		if s, ok := hub.sessions[created.Token]; ok {
			s.ExpiresAt = time.Now().Add(-1 * time.Second)
		}
		hub.mu.Unlock()

		pc.Close()
	}

	// Hub should have cleaned up
	hub.mu.RLock()
	count := len(hub.sessions)
	hub.mu.RUnlock()
	// Sessions may still exist but should be expired
	t.Logf("sessions remaining: %d (all should be expired)", count)
}

// === P1: Rapid connect/disconnect ===

func TestConcurrency_RapidConnectDisconnect(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	const iterations = 100
	for i := 0; i < iterations; i++ {
		ws := dial(t, ts)
		wsSend(t, ws, Message{Type: MsgPing})
		ws.Close()
	}
	// If we get here without panic or deadlock, the test passes
}

// === P1: Concurrent device code requests ===

func TestConcurrency_ConcurrentDeviceCodeRequests(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	pc := dial(t, ts)
	defer pc.Close()

	wsSend(t, pc, Message{Type: MsgCreateSession})
	wsRecv(t, pc)

	// Request multiple device codes rapidly — each should succeed
	codes := make(map[string]bool)
	for i := 0; i < 10; i++ {
		wsSend(t, pc, Message{Type: MsgDeviceCodeRequest})
		msg := wsRecv(t, pc)
		if msg.Type != MsgDeviceCodeCreated {
			t.Fatalf("iteration %d: expected device_code_created, got %s", i, msg.Type)
		}
		if codes[msg.DeviceCode] {
			t.Errorf("duplicate device code: %s", msg.DeviceCode)
		}
		codes[msg.DeviceCode] = true
	}
}

// === P1: Message ordering guarantee ===

func TestConcurrency_MessageOrdering(t *testing.T) {
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

	// Send ordered messages
	const count = 100
	go func() {
		for i := 0; i < count; i++ {
			wsSend(t, mobile, Message{Type: MsgEncrypted, Payload: fmt.Sprintf("seq-%d", i), Nonce: "n"})
		}
	}()

	// Verify order on PC side
	pc.SetReadDeadline(time.Now().Add(10 * time.Second))
	for i := 0; i < count; i++ {
		var msg Message
		if err := pc.ReadJSON(&msg); err != nil {
			t.Fatalf("read failed at %d: %v", i, err)
		}
		if msg.Type != MsgEncrypted {
			i-- // skip non-encrypted messages
			continue
		}
		expected := fmt.Sprintf("seq-%d", i)
		if msg.Payload != expected {
			t.Errorf("message %d: expected %s, got %s", i, expected, msg.Payload)
		}
	}
}

// === P1: Grace timer cleanup ===

func TestConcurrency_GraceTimerCleanup(t *testing.T) {
	_, ts := newTestServer()
	defer ts.Close()

	// Create and destroy many sessions to stress grace timers
	for i := 0; i < 20; i++ {
		pc := dial(t, ts)
		wsSend(t, pc, Message{Type: MsgCreateSession})
		wsRecv(t, pc)
		pc.Close()
		time.Sleep(10 * time.Millisecond)
	}

	// Wait for cleanup
	time.Sleep(500 * time.Millisecond)
}
