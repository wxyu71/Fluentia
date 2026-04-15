package main

import (
	"log"
	"sync"
)

// Hub manages all rooms and connected clients.
type Hub struct {
	rooms   map[string]*Room
	clients map[*Client]bool
	mu      sync.RWMutex
}

func NewHub() *Hub {
	return &Hub{
		rooms:   make(map[string]*Room),
		clients: make(map[*Client]bool),
	}
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
	switch msg.Type {
	case MsgCreateRoom:
		h.handleCreateRoom(c)
	case MsgJoinRoom:
		h.handleJoinRoom(c, msg)
	case MsgKeyExchange, MsgEncrypted:
		h.handleRelay(c, msg)
	case MsgPing:
		c.SendMessage(Message{Type: MsgPong})
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
	c.SendMessage(Message{Type: MsgRoomCreated, Token: room.Token})
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

	c.SendMessage(Message{Type: MsgJoined, Role: "mobile", Token: room.Token})
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
