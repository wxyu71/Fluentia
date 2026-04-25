package main

import (
	"crypto/rand"
	"encoding/hex"
	"log"
	"strings"
	"sync"
	"time"
)

// DeviceCodeEntry stores a pending device code and its associated session.
type DeviceCodeEntry struct {
	Code      string
	Session   *Session
	PC        *Client
	CreatedAt time.Time
	ExpiresAt time.Time
	Pending   *Client // mobile client waiting for confirmation
	VerifyID  string  // matching ID for visual confirmation
}

// Hub manages all sessions and connected clients.
type Hub struct {
	sessions    map[string]*Session
	clients     map[*Client]bool
	deviceCodes map[string]*DeviceCodeEntry // code → entry
	mu          sync.RWMutex
	MaxFileMB   int
	SessionMaxAge time.Duration

	// Rate limiting for device code attempts
	codeAttempts map[string][]time.Time // IP → timestamps
	rateMu       sync.Mutex
}


func NewHub(sessionMaxAgeDays int) *Hub {
	if sessionMaxAgeDays <= 0 {
		sessionMaxAgeDays = 7
	}

	return &Hub{
		sessions:     make(map[string]*Session),
		clients:      make(map[*Client]bool),
		deviceCodes:  make(map[string]*DeviceCodeEntry),
		codeAttempts: make(map[string][]time.Time),
		SessionMaxAge: time.Duration(sessionMaxAgeDays) * 24 * time.Hour,
	}
}

// GenerateDeviceCode creates an 8-char alphanumeric code for a session.
func (h *Hub) GenerateDeviceCode(session *Session, pc *Client) string {
	h.mu.Lock()
	defer h.mu.Unlock()

	// Remove any existing code for this session
	for code, entry := range h.deviceCodes {
		if entry.Session == session {
			delete(h.deviceCodes, code)
		}
	}

	code := generateAlphanumericCode(8)
	h.deviceCodes[code] = &DeviceCodeEntry{
		Code:      code,
		Session:   session,
		PC:        pc,
		CreatedAt: time.Now(),
		ExpiresAt: session.ExpiresAt,
	}
	return code
}

func (h *Hub) sessionExpiredLocked(session *Session) bool {
	if session == nil {
		return true
	}
	if session.ExpiresAt.IsZero() {
		return false
	}
	return time.Now().After(session.ExpiresAt)
}

// CheckDeviceCodeRateLimit returns true if the IP is rate-limited.
func (h *Hub) CheckDeviceCodeRateLimit(ip string) bool {
	h.rateMu.Lock()
	defer h.rateMu.Unlock()

	now := time.Now()
	cutoff := now.Add(-1 * time.Minute)

	// Clean old entries
	var valid []time.Time
	for _, t := range h.codeAttempts[ip] {
		if t.After(cutoff) {
			valid = append(valid, t)
		}
	}
	h.codeAttempts[ip] = valid

	if len(valid) >= 5 {
		return true // rate limited: max 5 attempts per minute
	}

	h.codeAttempts[ip] = append(h.codeAttempts[ip], now)
	return false
}

func generateAlphanumericCode(length int) string {
	const charset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789" // no 0/O/1/I to avoid confusion
	b := make([]byte, length)
	rand.Read(b)
	for i := range b {
		b[i] = charset[int(b[i])%len(charset)]
	}
	return string(b)
}

func generateVerifyID() string {
	b := make([]byte, 4)
	rand.Read(b)
	return strings.ToUpper(hex.EncodeToString(b))
}

func (h *Hub) Register(c *Client) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.clients[c] = true
}

func (h *Hub) Unregister(c *Client) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if _, ok := h.clients[c]; !ok {
		return
	}
	delete(h.clients, c)
	c.Shutdown()

	if c.session == nil {
		return
	}

	session := c.session
	if c.role == "pc" {
		session.PC = nil
		// Grace period: keep session alive for 30s so PC can rejoin.
		// Notify mobile that PC is temporarily gone.
		if session.Mobile != nil {
			session.Mobile.SendMessage(Message{Type: MsgPeerLeft, Role: "pc", Error: "temporary"})
		}
		token := session.Token
		session.GraceTimer = time.AfterFunc(30*time.Second, func() {
			h.expireSession(token)
		})
		log.Printf("Session %s: PC disconnected, grace period started (30s)", token)
	} else if c.role == "mobile" {
		session.Mobile = nil
		if session.PC != nil {
			session.PC.SendMessage(Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: c.deviceID})
		}
		log.Printf("Mobile %s left session %s", c.deviceID, session.Token)
	}
	c.session = nil
}

// expireSession destroys a session after the grace period if PC hasn't rejoined.
func (h *Hub) expireSession(token string) {
	h.mu.Lock()
	defer h.mu.Unlock()

	session, ok := h.sessions[token]
	if !ok {
		return // already destroyed
	}
	// If PC has rejoined during the grace period, don't destroy.
	if session.PC != nil {
		return
	}
	// Grace period expired — destroy session and kick mobile.
	if session.Mobile != nil {
		mobile := session.Mobile
		session.Mobile = nil
		mobile.session = nil
		delete(h.clients, mobile)
		mobile.SendAndClose(Message{Type: MsgPeerLeft, Role: "pc"})
	}
	delete(h.sessions, token)
	log.Printf("Session %s destroyed (PC grace period expired)", token)
}

// HandleMessage is called from a client's ReadPump goroutine.
func (h *Hub) HandleMessage(c *Client, msg Message) {
	// Version check for handshake messages
	if msg.Type == MsgCreateSession || msg.Type == MsgJoinSession || msg.Type == MsgRejoinSession {
		if msg.Version != "" && msg.Version != ProtocolVersion {
			c.SendMessage(Message{
				Type:    MsgError,
				Error:   "version mismatch: client=" + msg.Version + " server=" + ProtocolVersion,
				Version: ProtocolVersion,
			})
			return
		}
	}

	switch msg.Type {
	case MsgCreateSession:
		h.handleCreateSession(c)
	case MsgRejoinSession:
		h.handleRejoinSession(c, msg)
	case MsgJoinSession:
		h.handleJoinSession(c, msg)
	case MsgKeyExchange, MsgEncrypted:
		h.handleRelay(c, msg)
	case MsgPing:
		c.SendMessage(Message{Type: MsgPong})
	case MsgDeviceCodeRequest:
		h.handleDeviceCodeRequest(c)
	case MsgDeviceCodeJoin:
		h.handleDeviceCodeJoin(c, msg)
	case MsgDeviceCodeConfirm:
		h.handleDeviceCodeConfirm(c, msg)
	case MsgDeviceCodeReject:
		h.handleDeviceCodeReject(c, msg)
	default:
		c.SendMessage(Message{Type: MsgError, Error: "unknown message type: " + msg.Type})
	}
}

func (h *Hub) handleCreateSession(c *Client) {
	h.mu.Lock()
	defer h.mu.Unlock()

	// Destroy existing session owned by this client
	if c.session != nil {
		oldSession := c.session
		if oldSession.GraceTimer != nil {
			oldSession.GraceTimer.Stop()
			oldSession.GraceTimer = nil
		}
		if oldSession.Mobile != nil {
			mobile := oldSession.Mobile
			oldSession.Mobile = nil
			mobile.session = nil
			delete(h.clients, mobile)
			mobile.SendAndClose(Message{Type: MsgPeerLeft, Role: "pc"})
		}
		delete(h.sessions, oldSession.Token)
	}

	session := NewSession(c, h.SessionMaxAge)
	c.session = session
	c.role = "pc"
	h.sessions[session.Token] = session

	log.Printf("Session created: %s (expires %s)", session.Token, session.ExpiresAt.Format(time.RFC3339))
	c.SendMessage(Message{Type: MsgSessionCreated, Token: session.Token, Version: ProtocolVersion})
}

// handleRejoinSession lets PC reclaim an existing session within the grace period.
func (h *Hub) handleRejoinSession(c *Client, msg Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if msg.Token == "" {
		c.SendMessage(Message{Type: MsgError, Error: "token required for rejoin"})
		return
	}

	session, ok := h.sessions[msg.Token]
	if !ok {
		// Session expired or was destroyed — tell PC to create a new one.
		c.SendMessage(Message{Type: MsgError, Error: "session not found"})
		return
	}
	if h.sessionExpiredLocked(session) {
		delete(h.sessions, session.Token)
		c.SendMessage(Message{Type: MsgError, Error: "session expired, create a new session"})
		return
	}

	if session.PC != nil {
		// Session already has a PC — can't rejoin.
		c.SendMessage(Message{Type: MsgError, Error: "session already has a PC"})
		return
	}

	// Cancel the grace timer.
	if session.GraceTimer != nil {
		session.GraceTimer.Stop()
		session.GraceTimer = nil
	}

	// Reclaim the session.
	c.role = "pc"
	c.session = session
	session.PC = c

	log.Printf("Session %s: PC rejoined", session.Token)
	c.SendMessage(Message{Type: MsgRejoined, Token: session.Token, Version: ProtocolVersion})

	// Notify both sides about each other.
	if session.Mobile != nil {
		session.Mobile.SendMessage(Message{Type: MsgPeerJoined, Role: "pc"})
		c.SendMessage(Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: session.Mobile.deviceID})
	}
}

func (h *Hub) handleJoinSession(c *Client, msg Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if msg.Token == "" || msg.DeviceID == "" {
		c.SendMessage(Message{Type: MsgError, Error: "token and deviceId required"})
		return
	}

	session, ok := h.sessions[msg.Token]
	if !ok {
		c.SendMessage(Message{Type: MsgError, Error: "session not found"})
		return
	}
	if h.sessionExpiredLocked(session) {
		delete(h.sessions, session.Token)
		c.SendMessage(Message{Type: MsgError, Error: "session expired, scan a new code"})
		return
	}

	c.role = "mobile"
	c.deviceID = msg.DeviceID
	c.session = session

	// Preempt existing mobile client (force-disconnect)
	if old := session.Mobile; old != nil && old != c {
		log.Printf("Preempting device %s in session %s (new: %s)", old.deviceID, session.Token, c.deviceID)
		session.Mobile = nil
		old.session = nil
		delete(h.clients, old)
		old.SendAndClose(Message{
			Type:  MsgPreempted,
			Error: "another device connected",
		})
		if session.PC != nil {
			session.PC.SendMessage(Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: old.deviceID})
		}
	}

	session.Mobile = c
	log.Printf("Device %s joined session %s", c.deviceID, session.Token)

	c.SendMessage(Message{Type: MsgJoined, Role: "mobile", Token: session.Token, Version: ProtocolVersion})
	if session.PC != nil {
		c.SendMessage(Message{Type: MsgPeerJoined, Role: "pc"})
		session.PC.SendMessage(Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: c.deviceID})
	}
}

// handleRelay forwards key_exchange and encrypted messages to the peer.
func (h *Hub) handleRelay(c *Client, msg Message) {
	h.mu.RLock()
	defer h.mu.RUnlock()

	if c.session == nil {
		c.SendMessage(Message{Type: MsgError, Error: "not in a session"})
		return
	}

	var peer *Client
	if c.role == "pc" {
		peer = c.session.Mobile
	} else {
		peer = c.session.PC
	}

	if peer == nil {
		c.SendMessage(Message{Type: MsgError, Error: "no peer connected"})
		return
	}

	peer.SendMessage(msg)
}

// handleDeviceCodeRequest: PC requests a device code for its session.
func (h *Hub) handleDeviceCodeRequest(c *Client) {
	if c.session == nil {
		c.SendMessage(Message{Type: MsgError, Error: "create a session first"})
		return
	}

	h.mu.RLock()
	expired := h.sessionExpiredLocked(c.session)
	h.mu.RUnlock()
	if expired {
		c.SendMessage(Message{Type: MsgError, Error: "session expired, create a new session"})
		return
	}

	code := h.GenerateDeviceCode(c.session, c)
	c.SendMessage(Message{Type: MsgDeviceCodeCreated, DeviceCode: code})
	log.Printf("Device code %s created for session %s", code, c.session.Token)
}

// handleDeviceCodeJoin: mobile submits a device code to join.
func (h *Hub) handleDeviceCodeJoin(c *Client, msg Message) {
	// Rate limiting
	ip := ""
	if c.conn != nil {
		ip = c.conn.RemoteAddr().String()
	}
	if h.CheckDeviceCodeRateLimit(ip) {
		c.SendMessage(Message{Type: MsgError, Error: "too many attempts, try again later"})
		return
	}

	code := strings.ToUpper(strings.TrimSpace(msg.DeviceCode))
	if code == "" {
		c.SendMessage(Message{Type: MsgError, Error: "device code required"})
		return
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	entry, ok := h.deviceCodes[code]
	if !ok || time.Now().After(entry.ExpiresAt) || h.sessionExpiredLocked(entry.Session) {
		if ok {
			delete(h.deviceCodes, code)
		}
		c.SendMessage(Message{Type: MsgError, Error: "invalid or expired device code"})
		return
	}

	// Generate verification ID shown on both sides
	verifyID := generateVerifyID()
	entry.Pending = c
	entry.VerifyID = verifyID

	// Notify PC to confirm — include mobile's user agent and verify ID
	if entry.PC != nil {
		entry.PC.SendMessage(Message{
			Type:       MsgDeviceCodePending,
			DeviceCode: code,
			VerifyID:   verifyID,
			UserAgent:  msg.UserAgent,
			DeviceID:   msg.DeviceID,
		})
	}

	// Tell mobile to wait for confirmation
	c.SendMessage(Message{
		Type:     MsgDeviceCodePending,
		VerifyID: verifyID,
	})
}

// handleDeviceCodeConfirm: PC approves the device code join.
func (h *Hub) handleDeviceCodeConfirm(c *Client, msg Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	code := msg.DeviceCode
	entry, ok := h.deviceCodes[code]
	if !ok || entry.Pending == nil || entry.PC != c {
		c.SendMessage(Message{Type: MsgError, Error: "no pending request"})
		return
	}

	mobile := entry.Pending
	session := entry.Session

	// Now join the mobile to the session (same as handleJoinSession logic)
	mobile.role = "mobile"
	mobile.deviceID = msg.DeviceID
	mobile.session = session

	// Preempt existing mobile
	if old := session.Mobile; old != nil && old != mobile {
		session.Mobile = nil
		old.session = nil
		delete(h.clients, old)
		old.SendAndClose(Message{Type: MsgPreempted, Error: "another device connected"})
		if session.PC != nil {
			session.PC.SendMessage(Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: old.deviceID})
		}
	}

	session.Mobile = mobile
	delete(h.deviceCodes, code)

	// Send session token and PC's public key to mobile
	mobile.SendMessage(Message{
		Type:      MsgJoined,
		Role:      "mobile",
		Token:     session.Token,
		Version:   ProtocolVersion,
		PublicKey: msg.PublicKey, // PC sends its public key in the confirm message
		Approved:  true,
	})
	if session.PC != nil {
		mobile.SendMessage(Message{Type: MsgPeerJoined, Role: "pc"})
		session.PC.SendMessage(Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: mobile.deviceID})
	}

	log.Printf("Device code %s confirmed, device joined session %s", code, session.Token)
}

// handleDeviceCodeReject: PC rejects the device code join.
func (h *Hub) handleDeviceCodeReject(c *Client, msg Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	code := msg.DeviceCode
	entry, ok := h.deviceCodes[code]
	if !ok || entry.Pending == nil {
		return
	}

	if entry.Pending != nil {
		entry.Pending.SendMessage(Message{Type: MsgError, Error: "connection rejected by PC"})
	}
	entry.Pending = nil
	entry.VerifyID = ""
}
