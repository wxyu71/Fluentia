package main

import (
	"encoding/json"
	"log"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const (
	writeWait      = 10 * time.Second
	pongWait       = 18 * time.Second
	pingPeriod     = (pongWait * 9) / 10
	maxMessageSize = 64 * 1024
)

// Client represents a single WebSocket connection (either PC or mobile).
type Client struct {
	hub       *Hub
	conn      *websocket.Conn
	send      chan []byte
	session   *Session // the active session this client belongs to
	role      string   // "pc" or "mobile"
	deviceID  string
	closeOnce sync.Once
}

func NewClient(hub *Hub, conn *websocket.Conn) *Client {
	return &Client{
		hub:  hub,
		conn: conn,
		send: make(chan []byte, 256),
	}
}

// ReadPump reads messages from the WebSocket and dispatches them to the hub.
func (c *Client) ReadPump() {
	defer func() {
		c.hub.Unregister(c)
		_ = c.conn.Close()
	}()

	c.conn.SetReadLimit(maxMessageSize)
	_ = c.conn.SetReadDeadline(time.Now().Add(pongWait))
	c.conn.SetPongHandler(func(string) error {
		_ = c.conn.SetReadDeadline(time.Now().Add(pongWait))
		return nil
	})

	for {
		_, data, err := c.conn.ReadMessage()
		if err != nil {
			if websocket.IsUnexpectedCloseError(err, websocket.CloseGoingAway, websocket.CloseNormalClosure) {
				log.Printf("read error: %v", err)
			}
			break
		}

		var msg Message
		if err := json.Unmarshal(data, &msg); err != nil {
			c.SendMessage(&Message{Type: MsgError, Error: "invalid message format"})
			continue
		}

		c.hub.HandleMessage(c, &msg)
	}
}

// WritePump sends queued messages to the WebSocket connection.
func (c *Client) WritePump() {
	ticker := time.NewTicker(pingPeriod)
	defer func() {
		ticker.Stop()
		_ = c.conn.Close()
	}()

	for {
		select {
		case message, ok := <-c.send:
			_ = c.conn.SetWriteDeadline(time.Now().Add(writeWait))
			if !ok {
				_ = c.conn.WriteMessage(websocket.CloseMessage, []byte{})
				return
			}
			if err := c.conn.WriteMessage(websocket.TextMessage, message); err != nil {
				return
			}
		case <-ticker.C:
			_ = c.conn.SetWriteDeadline(time.Now().Add(writeWait))
			if err := c.conn.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}
		}
	}
}

// SendMessage marshals and enqueues a message for sending (non-blocking).
func (c *Client) SendMessage(msg *Message) {
	data, err := json.Marshal(msg)
	if err != nil {
		log.Printf("marshal error: %v", err)
		return
	}
	select {
	case c.send <- data:
	default:
		log.Printf("send buffer full for client %s", c.deviceID)
	}
}

// SendAndClose sends a final message then closes the send channel.
func (c *Client) SendAndClose(msg *Message) {
	c.closeOnce.Do(func() {
		data, _ := json.Marshal(msg)
		select {
		case c.send <- data:
		default:
		}
		close(c.send)
	})
}

// Shutdown closes the send channel without sending a message.
func (c *Client) Shutdown() {
	c.closeOnce.Do(func() {
		close(c.send)
	})
}
