package main

import (
	"crypto/rand"
	"encoding/hex"
	"log"
	"strings"
	"sync"
	"time"
)

// DeviceCodeEntry stores a pending device code and its associated room.
type DeviceCodeEntry struct {
	Code       string
	Room       *Room
	PC         *Client
	CreatedAt  time.Time
	Pending    *Client  // mobile client waiting for confirmation
	VerifyID   string   // matching UUID for visual confirmation
}

// Hub manages all rooms and connected clients.
type Hub struct {
	rooms       map[string]*Room
	clients     map[*Client]bool
	deviceCodes map[string]*DeviceCodeEntry // code → entry
	mu          sync.RWMutex
	MaxFileMB   int

	// Rate limiting for device code attempts
	codeAttempts map[string][]time.Time // IP → timestamps
	rateMu       sync.Mutex
}

func NewHub() *Hub {
	return &Hub{
		rooms:        make(map[string]*Room),
		clients:      make(map[*Client]bool),
		deviceCodes:  make(map[string]*DeviceCodeEntry),
		codeAttempts: make(map[string][]time.Time),
	}
}

// GenerateDeviceCode creates an 8-char alphanumeric code for a room.
func (h *Hub) GenerateDeviceCode(room *Room, pc *Client) string {
	h.mu.Lock()
	defer h.mu.Unlock()

	// Remove any existing code for this room
	for code, entry := range h.deviceCodes {
		if entry.Room == room {
			delete(h.deviceCodes, code)
		}
	}

	code := generateAlphanumericCode(8)
	h.deviceCodes[code] = &DeviceCodeEntry{
		Code:      code,
		Room:      room,
		PC:        pc,
		CreatedAt: time.Now(),
	}
	return code
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

	if c.room == nil {
		return
	}

	room := c.room
	if c.role == "pc" {
		room.PC = nil
		// PC disconnected → destroy room, kick mobile
		if room.Mobile != nil {
			mobile := room.Mobile
			room.Mobile = nil
			mobile.room = nil
			delete(h.clients, mobile)
			mobile.SendAndClose(Message{Type: MsgPeerLeft, Role: "pc"})
		}
		delete(h.rooms, room.Token)
		log.Printf("Room %s destroyed (PC disconnected)", room.Token)
	} else if c.role == "mobile" {
		room.Mobile = nil
		if room.PC != nil {
			room.PC.SendMessage(Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: c.deviceID})
		}
		log.Printf("Mobile %s left room %s", c.deviceID, room.Token)
	}
	c.room = nil
}

// HandleMessage is called from a client's ReadPump goroutine.
func (h *Hub) HandleMessage(c *Client, msg Message) {
	// Version check for handshake messages
	if msg.Type == MsgCreateRoom || msg.Type == MsgJoinRoom {
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
	case MsgCreateRoom:
		h.handleCreateRoom(c)
	case MsgJoinRoom:
		h.handleJoinRoom(c, msg)
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

func (h *Hub) handleCreateRoom(c *Client) {
	h.mu.Lock()
	defer h.mu.Unlock()

	// Destroy existing room owned by this client
	if c.room != nil {
		oldRoom := c.room
		if oldRoom.Mobile != nil {
			mobile := oldRoom.Mobile
			oldRoom.Mobile = nil
			mobile.room = nil
			delete(h.clients, mobile)
			mobile.SendAndClose(Message{Type: MsgPeerLeft, Role: "pc"})
		}
		delete(h.rooms, oldRoom.Token)
	}

	room := NewRoom(c)
	c.room = room
	c.role = "pc"
	h.rooms[room.Token] = room

	log.Printf("Room created: %s", room.Token)
	c.SendMessage(Message{Type: MsgRoomCreated, Token: room.Token, Version: ProtocolVersion})
}

func (h *Hub) handleJoinRoom(c *Client, msg Message) {
	h.mu.Lock()
	defer h.mu.Unlock()

	if msg.Token == "" || msg.DeviceID == "" {
		c.SendMessage(Message{Type: MsgError, Error: "token and deviceId required"})
		return
	}

	room, ok := h.rooms[msg.Token]
	if !ok {
		c.SendMessage(Message{Type: MsgError, Error: "room not found"})
		return
	}

	c.role = "mobile"
	c.deviceID = msg.DeviceID
	c.room = room

	// Preempt existing mobile client (force-disconnect)
	if old := room.Mobile; old != nil && old != c {
		log.Printf("Preempting device %s in room %s (new: %s)", old.deviceID, room.Token, c.deviceID)
		room.Mobile = nil
		old.room = nil
		delete(h.clients, old)
		old.SendAndClose(Message{
			Type:  MsgPreempted,
			Error: "another device connected",
		})
		if room.PC != nil {
			room.PC.SendMessage(Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: old.deviceID})
		}
	}

	room.Mobile = c
	log.Printf("Device %s joined room %s", c.deviceID, room.Token)

	c.SendMessage(Message{Type: MsgJoined, Role: "mobile", Token: room.Token, Version: ProtocolVersion})
	if room.PC != nil {
		room.PC.SendMessage(Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: c.deviceID})
	}
}

// handleRelay forwards key_exchange and encrypted messages to the peer.
func (h *Hub) handleRelay(c *Client, msg Message) {
	h.mu.RLock()
	defer h.mu.RUnlock()

	if c.room == nil {
		c.SendMessage(Message{Type: MsgError, Error: "not in a room"})
		return
	}

	var peer *Client
	if c.role == "pc" {
		peer = c.room.Mobile
	} else {
		peer = c.room.PC
	}

	if peer == nil {
		c.SendMessage(Message{Type: MsgError, Error: "no peer connected"})
		return
	}

	peer.SendMessage(msg)
}

// handleDeviceCodeRequest: PC requests a device code for its room.
func (h *Hub) handleDeviceCodeRequest(c *Client) {
	if c.room == nil {
		c.SendMessage(Message{Type: MsgError, Error: "create a room first"})
		return
	}
	code := h.GenerateDeviceCode(c.room, c)
	c.SendMessage(Message{Type: MsgDeviceCodeCreated, DeviceCode: code})
	log.Printf("Device code %s created for room %s", code, c.room.Token)
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
	if !ok || time.Since(entry.CreatedAt) > 5*time.Minute {
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
	room := entry.Room

	// Now join the mobile to the room (same as handleJoinRoom logic)
	mobile.role = "mobile"
	mobile.deviceID = msg.DeviceID
	mobile.room = room

	// Preempt existing mobile
	if old := room.Mobile; old != nil && old != mobile {
		room.Mobile = nil
		old.room = nil
		delete(h.clients, old)
		old.SendAndClose(Message{Type: MsgPreempted, Error: "another device connected"})
		if room.PC != nil {
			room.PC.SendMessage(Message{Type: MsgPeerLeft, Role: "mobile", DeviceID: old.deviceID})
		}
	}

	room.Mobile = mobile
	delete(h.deviceCodes, code)

	// Send room token and PC's public key to mobile
	mobile.SendMessage(Message{
		Type:      MsgJoined,
		Role:      "mobile",
		Token:     room.Token,
		Version:   ProtocolVersion,
		PublicKey: msg.PublicKey, // PC sends its public key in the confirm message
		Approved:  true,
	})
	if room.PC != nil {
		room.PC.SendMessage(Message{Type: MsgPeerJoined, Role: "mobile", DeviceID: mobile.deviceID})
	}

	log.Printf("Device code %s confirmed, device joined room %s", code, room.Token)
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
