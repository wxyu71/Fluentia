package main

import (
	"net"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"

	"github.com/gorilla/websocket"
)

// === [M1] Origin Validation Tests ===

// TestValidateOrigin_EmptyOrigin_Rejected verifies that empty Origin is rejected
// when AllowEmptyOrigin is false (the default secure behavior).
func TestValidateOrigin_EmptyOrigin_Rejected(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()

	srvGlobals = &ServerGlobals{AllowEmptyOrigin: false}

	r := httptest.NewRequest("GET", "/ws", nil)
	r.Header.Del("Origin") // ensure empty
	if validateOrigin(r) {
		t.Error("empty Origin should be rejected when AllowEmptyOrigin is false")
	}
}

// TestValidateOrigin_EmptyOrigin_AllowedInDevMode verifies that empty Origin
// is allowed only when AllowEmptyOrigin is explicitly set to true (dev/localhost mode).
func TestValidateOrigin_EmptyOrigin_AllowedInDevMode(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()

	srvGlobals = &ServerGlobals{AllowEmptyOrigin: true}

	r := httptest.NewRequest("GET", "/ws", nil)
	r.Header.Del("Origin")
	if !validateOrigin(r) {
		t.Error("empty Origin should be allowed when AllowEmptyOrigin is true")
	}
}

// TestValidateOrigin_NilGlobals_RejectsEmpty verifies that when srvGlobals is nil,
// empty Origin is rejected (secure default).
func TestValidateOrigin_NilGlobals_RejectsEmpty(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()
	srvGlobals = nil

	r := httptest.NewRequest("GET", "/ws", nil)
	r.Header.Del("Origin")
	if validateOrigin(r) {
		t.Error("empty Origin should be rejected when srvGlobals is nil")
	}
}

// TestValidateOrigin_ValidOrigin_Accepted verifies that a valid matching Origin
// is accepted regardless of AllowEmptyOrigin setting.
func TestValidateOrigin_ValidOrigin_Accepted(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()
	srvGlobals = &ServerGlobals{AllowEmptyOrigin: false}

	r := httptest.NewRequest("GET", "/ws", nil)
	r.Host = "example.com:8080"
	r.Header.Set("Origin", "http://example.com:8080")
	if !validateOrigin(r) {
		t.Error("valid matching Origin should be accepted")
	}
}

// TestValidateOrigin_MismatchedOrigin_Rejected verifies that a mismatched
// Origin header is rejected to prevent Cross-Site WebSocket Hijacking.
func TestValidateOrigin_MismatchedOrigin_Rejected(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()
	srvGlobals = &ServerGlobals{AllowEmptyOrigin: false}

	r := httptest.NewRequest("GET", "/ws", nil)
	r.Host = "example.com:8080"
	r.Header.Set("Origin", "http://evil.com:8080")
	if validateOrigin(r) {
		t.Error("mismatched Origin should be rejected")
	}
}

// TestValidateOrigin_Localhost_Accepted verifies that localhost variants
// are accepted with any port for local development.
func TestValidateOrigin_Localhost_Accepted(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()
	srvGlobals = &ServerGlobals{AllowEmptyOrigin: false}

	tests := []struct {
		name   string
		origin string
		host   string
	}{
		{"localhost same port", "http://localhost:3000", "localhost:3000"},
		{"localhost diff port", "http://localhost:9999", "localhost:3000"},
		{"127.0.0.1 diff port", "http://127.0.0.1:5555", "127.0.0.1:3000"},
		{"ipv6 loopback", "http://[::1]:3000", "[::1]:3000"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			r := httptest.NewRequest("GET", "/ws", nil)
			r.Host = tt.host
			r.Header.Set("Origin", tt.origin)
			if !validateOrigin(r) {
				t.Errorf("localhost origin %q should be accepted for host %q", tt.origin, tt.host)
			}
		})
	}
}

// TestValidateOrigin_InvalidOriginURL_Rejected verifies that a malformed
// Origin header is rejected.
func TestValidateOrigin_InvalidOriginURL_Rejected(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()
	srvGlobals = &ServerGlobals{AllowEmptyOrigin: false}

	r := httptest.NewRequest("GET", "/ws", nil)
	r.Host = "example.com"
	r.Header.Set("Origin", "://invalid")
	if validateOrigin(r) {
		t.Error("invalid Origin URL should be rejected")
	}
}

// === [M2] IP Extraction Tests ===

// TestExtractIP_TrustedProxy_UsesHeader verifies that when the direct connection
// is from a trusted proxy, X-Real-IP and X-Forwarded-For headers are used.
func TestExtractIP_TrustedProxy_UsesHeader(t *testing.T) {
	tests := []struct {
		name     string
		header   string
		value    string
		expected string
	}{
		{"X-Real-IP", "X-Real-IP", "203.0.113.50", "203.0.113.50"},
		{"X-Forwarded-For first", "X-Forwarded-For", "203.0.113.50, 70.41.3.18", "203.0.113.50"},
		{"X-Forwarded-For with spaces", "X-Forwarded-For", " 203.0.113.50 , 70.41.3.18 ", "203.0.113.50"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			r := httptest.NewRequest("GET", "/ws", nil)
			r.RemoteAddr = "10.0.0.1:12345" // from trusted proxy
			r.Header.Set(tt.header, tt.value)

			_, trustedNet, _ := net.ParseCIDR("10.0.0.0/8")
			trustedProxies := []*net.IPNet{trustedNet}

			ip := extractIP(r, trustedProxies)
			if ip != tt.expected {
				t.Errorf("expected %q, got %q", tt.expected, ip)
			}
		})
	}
}

// TestExtractIP_UntrustedProxy_UsesRemoteAddr verifies that when the direct
// connection is NOT from a trusted proxy, proxy headers are ignored.
func TestExtractIP_UntrustedProxy_UsesRemoteAddr(t *testing.T) {
	r := httptest.NewRequest("GET", "/ws", nil)
	r.RemoteAddr = "192.168.1.100:12345"
	r.Header.Set("X-Real-IP", "203.0.113.50") // should be ignored
	r.Header.Set("X-Forwarded-For", "10.0.0.1")

	_, trustedNet, _ := net.ParseCIDR("10.0.0.0/8") // only trust 10.x.x.x
	trustedProxies := []*net.IPNet{trustedNet}

	ip := extractIP(r, trustedProxies)
	if ip != "192.168.1.100" {
		t.Errorf("expected direct IP 192.168.1.100, got %q", ip)
	}
}

// TestExtractIP_NoTrustedProxiesConfigured verifies backward compatibility:
// when no trusted proxies are configured, proxy headers are always trusted.
func TestExtractIP_NoTrustedProxiesConfigured(t *testing.T) {
	r := httptest.NewRequest("GET", "/ws", nil)
	r.RemoteAddr = "192.168.1.100:12345"
	r.Header.Set("X-Real-IP", "203.0.113.50")

	// No trusted proxies configured (nil) → trust all
	ip := extractIP(r, nil)
	if ip != "203.0.113.50" {
		t.Errorf("expected proxy IP 203.0.113.50 when no trusted proxies configured, got %q", ip)
	}
}

// TestExtractIP_NoProxyHeaders verifies direct IP extraction when no
// proxy headers are present.
func TestExtractIP_NoProxyHeaders(t *testing.T) {
	r := httptest.NewRequest("GET", "/ws", nil)
	r.RemoteAddr = "192.168.1.100:12345"
	// No proxy headers set

	ip := extractIP(r, nil)
	if ip != "192.168.1.100" {
		t.Errorf("expected 192.168.1.100, got %q", ip)
	}
}

// === [M4] Max Connections Test ===

// TestMaxConnections_Rejected verifies that when the connection limit is reached,
// new connections receive a 503 Service Unavailable response.
func TestMaxConnections_Rejected(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()

	hub := NewHub(1)
	srvGlobals = &ServerGlobals{
		AllowEmptyOrigin: true,
		MaxConnections:   2,
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/ws", func(w http.ResponseWriter, r *http.Request) {
		serveWs(hub, &ServerConfig{}, w, r)
	})
	ts := httptest.NewServer(mux)
	defer ts.Close()

	// First two connections should succeed
	ws1 := dial(t, ts)
	defer ws1.Close()
	ws2 := dial(t, ts)
	defer ws2.Close()

	// Give registrations time to complete
	// The hub should now have 2 clients
	if hub.ClientCount() < 2 {
		t.Fatalf("expected at least 2 clients, got %d", hub.ClientCount())
	}

	// Third connection should be rejected with 503
	resp, err := http.Get(ts.URL + "/ws")
	if err != nil {
		t.Fatalf("request failed: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Errorf("expected 503 when at capacity, got %d", resp.StatusCode)
	}
}

// TestMaxConnections_ZeroMeansUnlimited verifies that MaxConnections=0 means no limit.
func TestMaxConnections_ZeroMeansUnlimited(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()

	hub := NewHub(1)
	srvGlobals = &ServerGlobals{
		AllowEmptyOrigin: true,
		MaxConnections:   0, // unlimited
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/ws", func(w http.ResponseWriter, r *http.Request) {
		serveWs(hub, &ServerConfig{}, w, r)
	})
	ts := httptest.NewServer(mux)
	defer ts.Close()

	// Connect several clients - all should succeed
	var conns []*websocket.Conn
	for i := 0; i < 5; i++ {
		ws := dial(t, ts)
		conns = append(conns, ws)
	}
	for _, ws := range conns {
		ws.Close()
	}
}

// === [L1] Graceful Shutdown Test ===

// TestGracefulShutdown_SendsMaintenanceMessage verifies that ShutdownAll sends
// a server_maintenance message to all connected clients.
func TestGracefulShutdown_SendsMaintenanceMessage(t *testing.T) {
	hub := newTestHub()

	// Register several clients
	c1 := newTestClient(hub, "pc")
	c2 := newTestClient(hub, "mobile")
	c3 := newTestClient(hub, "pc")

	// Drain any existing messages
	readMessages(c1)
	readMessages(c2)
	readMessages(c3)

	// Trigger shutdown
	hub.ShutdownAll()

	// All clients should receive maintenance message
	for _, c := range []*Client{c1, c2, c3} {
		msg := lastMessage(c)
		if msg.Type != MsgError {
			t.Errorf("expected error message type, got %q", msg.Type)
		}
		if msg.Error != "server_maintenance" {
			t.Errorf("expected server_maintenance error, got %q", msg.Error)
		}
	}
}

// TestGracefulShutdown_EmptyHub verifies ShutdownAll works with no clients.
func TestGracefulShutdown_EmptyHub(t *testing.T) {
	hub := newTestHub()
	// Should not panic
	hub.ShutdownAll()
}

// === ServerConfig Tests ===

// TestServerConfig_NewFieldsHaveDefaults verifies that new config fields
// have sensible zero-value defaults.
func TestServerConfig_NewFieldsHaveDefaults(t *testing.T) {
	cfg := loadConfig()

	if cfg.AllowEmptyOrigin {
		t.Error("AllowEmptyOrigin should default to false")
	}
	if len(cfg.TrustedProxies) != 0 {
		t.Error("TrustedProxies should default to empty")
	}
	if cfg.MaxConnections != 0 {
		t.Error("MaxConnections should default to 0 (unlimited)")
	}
	if cfg.TLSCertFile != "" {
		t.Error("TLSCertFile should default to empty")
	}
	if cfg.TLSKeyFile != "" {
		t.Error("TLSKeyFile should default to empty")
	}
}

// TestIsLocalhost_NoWildcard verifies that 0.0.0.0 is not treated as localhost.
func TestIsLocalhost_NoWildcard(t *testing.T) {
	if isLocalhost("0.0.0.0") {
		t.Error("0.0.0.0 should not be treated as localhost")
	}
}

// TestClientCount_ReturnsCorrectNumber verifies ClientCount returns the
// number of registered clients.
func TestClientCount_ReturnsCorrectNumber(t *testing.T) {
	hub := newTestHub()
	if hub.ClientCount() != 0 {
		t.Errorf("expected 0 clients, got %d", hub.ClientCount())
	}

	c1 := newTestClient(hub, "")
	if hub.ClientCount() != 1 {
		t.Errorf("expected 1 client, got %d", hub.ClientCount())
	}

	c2 := newTestClient(hub, "")
	if hub.ClientCount() != 2 {
		t.Errorf("expected 2 clients, got %d", hub.ClientCount())
	}

	hub.Unregister(c1)
	if hub.ClientCount() != 1 {
		t.Errorf("expected 1 client after unregister, got %d", hub.ClientCount())
	}

	hub.Unregister(c2)
	if hub.ClientCount() != 0 {
		t.Errorf("expected 0 clients after unregister, got %d", hub.ClientCount())
	}
}

// === MaxConnections concurrency test ===

// TestMaxConnections_ConcurrentRequests verifies that the connection limit
// works correctly under concurrent access.
func TestMaxConnections_ConcurrentRequests(t *testing.T) {
	old := srvGlobals
	defer func() { srvGlobals = old }()

	hub := NewHub(1)
	srvGlobals = &ServerGlobals{
		AllowEmptyOrigin: true,
		MaxConnections:   5,
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/ws", func(w http.ResponseWriter, r *http.Request) {
		serveWs(hub, &ServerConfig{}, w, r)
	})
	ts := httptest.NewServer(mux)
	defer ts.Close()

	// First, create 5 actual WebSocket connections to fill the capacity
	var conns []*websocket.Conn
	for i := 0; i < 5; i++ {
		ws := dial(t, ts)
		conns = append(conns, ws)
	}
	defer func() {
		for _, ws := range conns {
			ws.Close()
		}
	}()

	// Wait for all registrations to complete
	// The hub should now have 5 clients
	if hub.ClientCount() < 5 {
		t.Fatalf("expected at least 5 clients, got %d", hub.ClientCount())
	}

	// Now try to connect more clients via HTTP - these should be rejected with 503
	var wg sync.WaitGroup
	var mu sync.Mutex
	rejected := 0

	for i := 0; i < 5; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			resp, err := http.Get(ts.URL + "/ws")
			if err != nil {
				return
			}
			resp.Body.Close()
			if resp.StatusCode == http.StatusServiceUnavailable {
				mu.Lock()
				rejected++
				mu.Unlock()
			}
		}()
	}
	wg.Wait()

	// All overflow connections should be rejected
	if rejected != 5 {
		t.Errorf("expected 5 rejected connections, got %d", rejected)
	}
}

// Ensure imports are used
