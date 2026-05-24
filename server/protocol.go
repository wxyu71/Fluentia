package main

// Protocol version — all three components (server, mobile, Windows) must match.
const ProtocolVersion = "1.5.7"

// Message type constants
const (
	MsgCreateSession  = "create_session"
	MsgSessionCreated = "session_created"
	MsgJoinSession    = "join_session"
	MsgJoined         = "joined"
	MsgRejoinSession  = "rejoin_session"  // PC reclaims session after brief disconnect
	MsgRejoined       = "rejoined"        // Server confirms rejoin
	MsgPeerJoined     = "peer_joined"
	MsgPeerLeft       = "peer_left"
	MsgPreempted      = "preempted"
	MsgKeyExchange    = "key_exchange"
	MsgEncrypted      = "encrypted"
	MsgPing           = "ping"
	MsgPong           = "pong"
	MsgError          = "error"

	// PC → mobile focus/state notifications (relayed as encrypted messages)

	// Device code auth
	MsgDeviceCodeRequest = "device_code_request"
	MsgDeviceCodeCreated = "device_code_created"
	MsgDeviceCodeJoin    = "device_code_join"
	MsgDeviceCodePending = "device_code_pending" // sent to PC: mobile wants to join
	MsgDeviceCodeConfirm = "device_code_confirm" // PC confirms
	MsgDeviceCodeReject  = "device_code_reject"  // PC rejects
)

// Message is the universal message envelope for all WebSocket communication.
type Message struct {
	Type      string `json:"type"`
	Token     string `json:"token,omitempty"`
	DeviceID  string `json:"deviceId,omitempty"`
	Role      string `json:"role,omitempty"`
	PublicKey string `json:"publicKey,omitempty"`
	Payload   string `json:"payload,omitempty"`
	Nonce     string `json:"nonce,omitempty"`
	Error     string `json:"error,omitempty"`
	Version   string `json:"version,omitempty"`
	MinVersion string `json:"min_version,omitempty"`
	ExpiresAt string `json:"expiresAt,omitempty"`
	Seq       *int   `json:"seq,omitempty"`

	// Device code auth fields
	DeviceCode string `json:"deviceCode,omitempty"`
	VerifyID   string `json:"verifyId,omitempty"`  // UUID shown on both sides for visual confirmation
	UserAgent  string `json:"userAgent,omitempty"`
	Approved   bool   `json:"approved,omitempty"`
}
