package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"log"
	"net"
	"strings"
	"sync"
	"time"
	"unicode"
)

// shortToken returns the first 8 characters of a token for safe logging.
func shortToken(token string) string {
	if len(token) > 8 {
		return token[:8]
	}
	return token
}

// shortCode returns the first 4 characters of a device code for safe logging.
// [M6] Used to redact device codes in log output.
func shortCode(code string) string {
	if len(code) > 4 {
		return code[:4]
	}
	return code
}

// sanitizeForLog strips control characters and truncates user-supplied strings
// to prevent log injection and excessive log output.
func sanitizeForLog(s string) string {
	const maxLen = 64
	var b strings.Builder
	for i, r := range s {
		if i >= maxLen {
			break
		}
		if r == '\t' || !unicode.IsControl(r) {
			b.WriteRune(r)
		}
	}
	return b.String()
}

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
	sessions          map[string]*Session
	clients           map[*Client]bool
	deviceCodes       map[string]*DeviceCodeEntry
	persistedSessions map[string]persistedSession
	codeAttempts      map[string][]time.Time
	MinVersion        string
	SessionStorePath  string
	SessionMaxAge     time.Duration
	MaxFileMB         int
	mu                sync.RWMutex
	rateMu            sync.Mutex
	writeMu           sync.Mutex // [M3] Protects concurrent file writes
	rateCleanupCount  int // counter for periodic rate limiter cleanup
}

func NewHub(sessionMaxAgeDays int) *Hub {
	if sessionMaxAgeDays <= 0 {
		sessionMaxAgeDays = 7
	}

	return &Hub{
		sessions:          make(map[string]*Session),
		clients:           make(map[*Client]bool),
		deviceCodes:       make(map[string]*DeviceCodeEntry),
		persistedSessions: make(map[string]persistedSession),
		codeAttempts:      make(map[string][]time.Time),
		MinVersion:        ProtocolVersion,
		SessionMaxAge:     time.Duration(sessionMaxAgeDays) * 24 * time.Hour,
	}
}

func tokenFingerprint(token string) string {
	hash := sha256.Sum256([]byte(token))
	return hex.EncodeToString(hash[:])
}

// generateDeviceCodeLocked creates an 8-char alphanumeric code for a session.
// Caller must hold h.mu.
func (h *Hub) generateDeviceCodeLocked(session *Session, pc *Client) string {
	// Remove any existing code for this session
	for code, entry := range h.deviceCodes {
		if entry.Session == session {
			delete(h.deviceCodes, code)
		}
	}

	code := generateAlphanumericCode(8)
	entry := &DeviceCodeEntry{
		Code:      code,
		Session:   session,
		PC:        pc,
		CreatedAt: time.Now(),
		ExpiresAt: session.ExpiresAt,
	}
	h.deviceCodes[code] = entry

	// [L2] Schedule automatic cleanup when device code expires to prevent memory leak.
	// The cleanup runs even if the mobile never joins.
	time.AfterFunc(time.Until(session.ExpiresAt), func() {
		h.mu.Lock()
		defer h.mu.Unlock()
		if cur, ok := h.deviceCodes[code]; ok && cur == entry {
			delete(h.deviceCodes, code)
		}
	})

	return code
}

// GenerateDeviceCode creates an 8-char alphanumeric code for a session.
// Exported for tests; callers that already hold h.mu should use generateDeviceCodeLocked.
func (h *Hub) GenerateDeviceCode(session *Session, pc *Client) string {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.generateDeviceCodeLocked(session, pc)
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

	// Periodically clean up stale IP entries to prevent unbounded memory growth.
	// Run cleanup every 100 calls to amortize cost.
	h.rateCleanupCount++
	if h.rateCleanupCount >= 100 {
		h.rateCleanupCount = 0
		for k, attempts := range h.codeAttempts {
			var valid []time.Time
			for _, t := range attempts {
				if t.After(cutoff) {
					valid = append(valid, t)
				}
			}
			if len(valid) == 0 {
				delete(h.codeAttempts, k)
			} else {
				h.codeAttempts[k] = valid
			}
		}
	}

	// Clean old entries for this IP
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
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	for i := range b {
		b[i] = charset[int(b[i])%len(charset)]
	}
	return string(b)
}

func generateVerifyID() string {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return strings.ToUpper(hex.EncodeToString(b))
}

func (h *Hub) Register(c *Client) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.clients[c] = true
}

// ClientCount returns the number of currently connected clients.
func (h *Hub) ClientCount() int {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return len(h.clients)
}

// ShutdownAll sends a server_maintenance message to all connected clients.
// [L1] Used during graceful shutdown to notify clients before disconnection.
func (h *Hub) ShutdownAll() {
	h.mu.RLock()
	defer h.mu.RUnlock()
	for c := range h.clients {
		c.SendMessage(&Message{Type: MsgError, Error: "server_maintenance"})
	}
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
	switch c.role {
	case "pc":
		session.PC = nil
		// Keep the session reusable until its configured expiry so the desktop can
		// recover after a restart, power loss, or a longer network outage.
		if session.Mobile != nil {
			session.Mobile.SendMessage(&Message{Type: MsgPeerLeft, Role: "pc", Error: "temporary"})
		}
		if session.GraceTimer != nil {
			session.GraceTimer.Stop()
			session.GraceTimer = nil
		}
		token := session.Token
		remaining := time.Until(session.ExpiresAt)
		if remaining <= 0 {
			go h.expireSession(token)
			log.Printf("Session %s: PC disconnected after session expiry", shortToken(token))
		} else {
			session.GraceTimer = time.AfterFunc(remaining, func() {
				h.expireSession(token)
			})
			log.Printf("Session %s: PC disconnected, reusable until %s", shortToken(token), session.ExpiresAt.Format(time.RFC3339))
		}
	case "mobile":
		session.Mobile = nil
		if session.PC != nil {
			session.PC.SendMessage(&Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: c.deviceID})
		}
		log.Printf("Mobile %s left session %s", sanitizeForLog(c.deviceID), shortToken(session.Token))
	}
	c.session = nil
}

// expireSession destroys a reusable session once it is no longer valid.
func (h *Hub) expireSession(token string) {
	h.mu.Lock()
	defer h.mu.Unlock()

	session, ok := h.sessions[token]
	if !ok {
		return // already destroyed
	}
	// If PC has rejoined and the session is still valid, keep it.
	if session.PC != nil {
		return
	}
	// Session validity window expired — destroy session and kick mobile.
	if session.Mobile != nil {
		mobile := session.Mobile
		session.Mobile = nil
		mobile.session = nil
		delete(h.clients, mobile)
		mobile.SendAndClose(&Message{Type: MsgPeerLeft, Role: "pc"})
	}
	delete(h.sessions, token)
	delete(h.persistedSessions, tokenFingerprint(token))
	go h.writeSessionsSnapshot(h.snapshotSessions())
	log.Printf("Session %s destroyed (session expired while PC offline)", shortToken(token))
}

func (h *Hub) hydratePersistedSessionLocked(token string) (*Session, bool) {
	fingerprint := tokenFingerprint(token)
	entry, ok := h.persistedSessions[fingerprint]
	if !ok || !entry.ExpiresAt.After(time.Now()) {
		if ok {
			delete(h.persistedSessions, fingerprint)
			go h.writeSessionsSnapshot(h.snapshotSessions())
		}
		return nil, false
	}

	session := &Session{
		Token:     token,
		CreatedAt: entry.CreatedAt,
		ExpiresAt: entry.ExpiresAt,
	}
	h.sessions[token] = session
	return session, true
}

// HandleMessage is called from a client's ReadPump goroutine.
func (h *Hub) HandleMessage(c *Client, msg *Message) {
	// Version check for handshake messages
	if msg.Type == MsgCreateSession || msg.Type == MsgJoinSession || msg.Type == MsgRejoinSession {
		if msg.Version != "" && msg.Version != ProtocolVersion {
			c.SendMessage(&Message{
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
		c.SendMessage(&Message{Type: MsgPong})
	case MsgDeviceCodeRequest:
		h.handleDeviceCodeRequest(c)
	case MsgDeviceCodeJoin:
		h.handleDeviceCodeJoin(c, msg)
	case MsgDeviceCodeConfirm:
		h.handleDeviceCodeConfirm(c, msg)
	case MsgDeviceCodeReject:
		h.handleDeviceCodeReject(c, msg)
	default:
		c.SendMessage(&Message{Type: MsgError, Error: "unknown message type: " + msg.Type})
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
			mobile.SendAndClose(&Message{Type: MsgPeerLeft, Role: "pc"})
		}
		delete(h.sessions, oldSession.Token)
		delete(h.persistedSessions, tokenFingerprint(oldSession.Token))
		go h.writeSessionsSnapshot(h.snapshotSessions())
	}

	session := NewSession(c, h.SessionMaxAge)
	c.session = session
	c.role = "pc"
	h.sessions[session.Token] = session
	h.persistedSessions[tokenFingerprint(session.Token)] = persistedSession{
		TokenHash: tokenFingerprint(session.Token),
		CreatedAt: session.CreatedAt,
		ExpiresAt: session.ExpiresAt,
	}
	go h.writeSessionsSnapshot(h.snapshotSessions())

	log.Printf("Session created: %s (expires %s)", shortToken(session.Token), session.ExpiresAt.Format(time.RFC3339))
	c.SendMessage(&Message{
		Type:       MsgSessionCreated,
		Token:      session.Token,
		MinVersion: h.MinVersion,
		Version:    ProtocolVersion,
		ExpiresAt:  session.ExpiresAt.Format(time.RFC3339),
	})
}

// handleRejoinSession lets PC reclaim an existing reusable session before expiry.
func (h *Hub) handleRejoinSession(c *Client, msg *Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if msg.Token == "" {
		c.SendMessage(&Message{Type: MsgError, Error: "token required for rejoin"})
		return
	}

	session, ok := h.sessions[msg.Token]
	if !ok {
		var hydrated bool
		session, hydrated = h.hydratePersistedSessionLocked(msg.Token)
		if !hydrated {
			c.SendMessage(&Message{Type: MsgError, Error: "session not found"})
			return
		}
	}
	if h.sessionExpiredLocked(session) {
		delete(h.sessions, session.Token)
		go h.writeSessionsSnapshot(h.snapshotSessions())
		c.SendMessage(&Message{Type: MsgError, Error: "session expired, create a new session"})
		return
	}

	if session.PC != nil {
		// Session already has a PC — can't rejoin.
		c.SendMessage(&Message{Type: MsgError, Error: "session already has a PC"})
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

	log.Printf("Session %s: PC rejoined", shortToken(session.Token))
	c.SendMessage(&Message{
		Type:       MsgRejoined,
		Token:      session.Token,
		MinVersion: h.MinVersion,
		Version:    ProtocolVersion,
		ExpiresAt:  session.ExpiresAt.Format(time.RFC3339),
	})

	// Notify both sides about each other.
	if session.Mobile != nil {
		session.Mobile.SendMessage(&Message{Type: MsgPeerJoined, Role: "pc"})
		c.SendMessage(&Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: session.Mobile.deviceID})
	}
}

func (h *Hub) handleJoinSession(c *Client, msg *Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if msg.Token == "" || msg.DeviceID == "" {
		c.SendMessage(&Message{Type: MsgError, Error: "token and deviceId required"})
		return
	}

	session, ok := h.sessions[msg.Token]
	if !ok {
		var hydrated bool
		session, hydrated = h.hydratePersistedSessionLocked(msg.Token)
		if !hydrated {
			c.SendMessage(&Message{Type: MsgError, Error: "session not found"})
			return
		}
	}
	if h.sessionExpiredLocked(session) {
		delete(h.sessions, session.Token)
		go h.writeSessionsSnapshot(h.snapshotSessions())
		c.SendMessage(&Message{Type: MsgError, Error: "session expired, scan a new code"})
		return
	}

	c.role = "mobile"
	c.deviceID = msg.DeviceID
	c.session = session

	// Preempt existing mobile client (force-disconnect)
	if old := session.Mobile; old != nil && old != c {
		sameDevice := old.deviceID != "" && old.deviceID == c.deviceID
		log.Printf("Preempting device %s in session %s (new: %s)", sanitizeForLog(old.deviceID), shortToken(session.Token), sanitizeForLog(c.deviceID))
		session.Mobile = nil
		old.session = nil
		delete(h.clients, old)
		old.SendAndClose(&Message{
			Type:  MsgPreempted,
			Error: "another device connected",
		})
		if session.PC != nil && !sameDevice {
			session.PC.SendMessage(&Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: old.deviceID})
		}
	}

	session.Mobile = c
	log.Printf("Device %s joined session %s", sanitizeForLog(c.deviceID), shortToken(session.Token))

	c.SendMessage(&Message{Type: MsgJoined, Role: "mobile", Token: session.Token, Version: ProtocolVersion, MinVersion: h.MinVersion})
	if session.PC != nil {
		c.SendMessage(&Message{Type: MsgPeerJoined, Role: "pc"})
		session.PC.SendMessage(&Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: c.deviceID})
	}
}

// handleRelay forwards key_exchange and encrypted messages to the peer.
func (h *Hub) handleRelay(c *Client, msg *Message) {
	h.mu.RLock()
	defer h.mu.RUnlock()

	if c.session == nil {
		c.SendMessage(&Message{Type: MsgError, Error: "not in a session"})
		return
	}

	var peer *Client
	if c.role == "pc" {
		peer = c.session.Mobile
	} else {
		peer = c.session.PC
	}

	if peer == nil {
		c.SendMessage(&Message{Type: MsgError, Error: "no peer connected"})
		return
	}

	peer.SendMessage(msg)
}

// handleDeviceCodeRequest: PC requests a device code for its session.
func (h *Hub) handleDeviceCodeRequest(c *Client) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if c.session == nil {
		c.SendMessage(&Message{Type: MsgError, Error: "create a session first"})
		return
	}

	if h.sessionExpiredLocked(c.session) {
		c.SendMessage(&Message{Type: MsgError, Error: "session expired, create a new session"})
		return
	}

	code := h.generateDeviceCodeLocked(c.session, c)
	c.SendMessage(&Message{Type: MsgDeviceCodeCreated, DeviceCode: code})
	// [M6] Only log first 4 chars of device code for safety
	log.Printf("Device code %s created for session %s", shortCode(code), shortToken(c.session.Token))
}

// handleDeviceCodeJoin: mobile submits a device code to join.
func (h *Hub) handleDeviceCodeJoin(c *Client, msg *Message) {
	// [L6] Rate limiting — extract just IP (not IP:port) so all connections from
	// the same IP share one rate limit bucket.
	ip := ""
	if c.conn != nil {
		host, _, err := net.SplitHostPort(c.conn.RemoteAddr().String())
		if err == nil {
			ip = host
		} else {
			ip = c.conn.RemoteAddr().String()
		}
	}
	if h.CheckDeviceCodeRateLimit(ip) {
		c.SendMessage(&Message{Type: MsgError, Error: "too many attempts, try again later"})
		return
	}

	code := strings.ToUpper(strings.TrimSpace(msg.DeviceCode))
	if code == "" {
		c.SendMessage(&Message{Type: MsgError, Error: "device code required"})
		return
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	entry, ok := h.deviceCodes[code]
	if !ok || time.Now().After(entry.ExpiresAt) || h.sessionExpiredLocked(entry.Session) {
		if ok {
			delete(h.deviceCodes, code)
		}
		c.SendMessage(&Message{Type: MsgError, Error: "invalid or expired device code"})
		return
	}

	// Generate verification ID shown on both sides
	verifyID := generateVerifyID()
	c.deviceID = msg.DeviceID
	entry.Pending = c
	entry.VerifyID = verifyID

	// Notify PC to confirm — include mobile's user agent and verify ID
	if entry.PC != nil {
		entry.PC.SendMessage(&Message{
			Type:       MsgDeviceCodePending,
			DeviceCode: code,
			VerifyID:   verifyID,
			UserAgent:  msg.UserAgent,
			DeviceID:   msg.DeviceID,
		})
	}

	// Tell mobile to wait for confirmation
	c.SendMessage(&Message{
		Type:     MsgDeviceCodePending,
		VerifyID: verifyID,
	})
}

// handleDeviceCodeConfirm: PC approves the device code join.
func (h *Hub) handleDeviceCodeConfirm(c *Client, msg *Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	code := msg.DeviceCode
	entry, ok := h.deviceCodes[code]
	if !ok || entry.Pending == nil || entry.PC != c {
		c.SendMessage(&Message{Type: MsgError, Error: "no pending request"})
		return
	}

	mobile := entry.Pending
	session := entry.Session

	// Now join the mobile to the session (same as handleJoinSession logic)
	mobile.role = "mobile"
	mobile.session = session

	// Preempt existing mobile
	if old := session.Mobile; old != nil && old != mobile {
		sameDevice := old.deviceID != "" && old.deviceID == mobile.deviceID
		session.Mobile = nil
		old.session = nil
		delete(h.clients, old)
		old.SendAndClose(&Message{Type: MsgPreempted, Error: "another device connected"})
		if session.PC != nil && !sameDevice {
			session.PC.SendMessage(&Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: old.deviceID})
		}
	}

	session.Mobile = mobile
	delete(h.deviceCodes, code)

	// Send session token and PC's public key to mobile
	mobile.SendMessage(&Message{
		Type:      MsgJoined,
		Role:      "mobile",
		Token:     session.Token,
		Version:   ProtocolVersion,
		PublicKey: msg.PublicKey, // PC sends its public key in the confirm message
		Approved:  true,
	})
	if session.PC != nil {
		mobile.SendMessage(&Message{Type: MsgPeerJoined, Role: "pc"})
		session.PC.SendMessage(&Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: mobile.deviceID})
	}

	log.Printf("Device code %s confirmed, device joined session %s", shortCode(code), shortToken(session.Token))
}

// handleDeviceCodeReject: PC rejects the device code join.
func (h *Hub) handleDeviceCodeReject(c *Client, msg *Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	code := msg.DeviceCode
	entry, ok := h.deviceCodes[code]
	if !ok || entry.Pending == nil || entry.PC != c {
		return
	}

	entry.Pending.SendMessage(&Message{Type: MsgError, Error: "connection rejected by PC"})
	entry.Pending = nil
	entry.VerifyID = ""
}
