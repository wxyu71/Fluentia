package main

import (
	"context"
	"encoding/json"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/gorilla/websocket"
)

// srvGlobals is set once in main() before any HTTP requests are served.
var srvGlobals *ServerGlobals

// ServerGlobals holds runtime configuration needed by HTTP handlers.
type ServerGlobals struct {
	AllowEmptyOrigin bool
	TrustedProxies   []*net.IPNet
	MaxConnections   int
}

// ServerConfig holds all environment-driven settings.
type ServerConfig struct {
	Port             string
	StaticDir        string
	MinVersion       string
	SessionStorePath string
	SecretPath       string
	AllowedIPs       []string
	MaxFileMB        int
	MobileExpiry     int
	SessionMaxAge    int
	PrivateMode      bool
	IPWhitelist      bool
	AllowEmptyOrigin bool     // [M1] Allow empty Origin header (dev/localhost mode)
	TrustedProxies   []string // [M2] CIDRs of trusted reverse proxies
	MaxConnections   int      // [M4] Max concurrent WebSocket connections (0 = unlimited)
	TLSCertFile      string   // [L5] TLS certificate file path
	TLSKeyFile       string   // [L5] TLS key file path
}

func loadConfig() ServerConfig {
	cfg := ServerConfig{
		Port:             envOr("PORT", "8080"),
		StaticDir:        envOr("STATIC_DIR", "./static"),
		MinVersion:       envOr("MIN_VERSION", ProtocolVersion),
		SessionStorePath: envOr("SESSION_STORE_PATH", "./data/sessions.json"),
		MaxFileMB:        envInt("MAX_FILE_MB", -1),
		MobileExpiry:     envInt("MOBILE_EXPIRY_SECS", 60),
		SessionMaxAge:    envInt("SESSION_MAX_AGE_DAYS", 7),
	}

	cfg.SecretPath = os.Getenv("SECRET_PATH")
	cfg.PrivateMode = envBool("PRIVATE_MODE", false)
	cfg.IPWhitelist = envBool("IP_WHITELIST", false)
	cfg.AllowEmptyOrigin = envBool("ALLOW_EMPTY_ORIGIN", false)
	cfg.MaxConnections = envInt("MAX_CONNECTIONS", 0)
	cfg.TLSCertFile = os.Getenv("TLS_CERT_FILE")
	cfg.TLSKeyFile = os.Getenv("TLS_KEY_FILE")

	if tp := os.Getenv("TRUSTED_PROXIES"); tp != "" {
		for _, cidr := range strings.Split(tp, ",") {
			if t := strings.TrimSpace(cidr); t != "" {
				cfg.TrustedProxies = append(cfg.TrustedProxies, t)
			}
		}
	}

	if ips := os.Getenv("ALLOWED_IPS"); ips != "" {
		for _, ip := range strings.Split(ips, ",") {
			if t := strings.TrimSpace(ip); t != "" {
				cfg.AllowedIPs = append(cfg.AllowedIPs, t)
			}
		}
	}

	return cfg
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func envInt(key string, fallback int) int {
	if v := os.Getenv(key); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			return n
		}
	}
	return fallback
}

func envBool(key string, fallback bool) bool {
	if v := os.Getenv(key); v != "" {
		return v == "true" || v == "1" || v == "yes"
	}
	return fallback
}

// isLocalhost returns true if the host (without port) is a loopback address.
func isLocalhost(host string) bool {
	return host == "localhost" || host == "127.0.0.1" || host == "::1"
}

// validateOrigin checks that the WebSocket Origin header matches the request Host
// to prevent Cross-Site WebSocket Hijacking (CSWSH). Compares the full host:port
// pair, with an exception for localhost variants (any port allowed for local dev).
func validateOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		// [M1] Only allow empty origin when explicitly configured (dev/localhost mode).
		if srvGlobals != nil && srvGlobals.AllowEmptyOrigin {
			return true
		}
		return false
	}

	parsed, err := url.Parse(origin)
	if err != nil {
		return false
	}

	originHost := parsed.Hostname() // strips brackets from IPv6, e.g. "::1"
	reqHost, reqPort, splitErr := net.SplitHostPort(r.Host)
	if splitErr != nil {
		// No port in Host header — compare hostname only.
		reqHost = r.Host
		reqPort = ""
	}

	// Allow localhost variants with any port for local development.
	if isLocalhost(originHost) && isLocalhost(reqHost) {
		return true
	}

	// For non-localhost, compare the full host:port pair.
	originFull := originHost
	if originPort := parsed.Port(); originPort != "" {
		originFull = net.JoinHostPort(originHost, originPort)
	}
	reqFull := reqHost
	if reqPort != "" {
		reqFull = net.JoinHostPort(reqHost, reqPort)
	}

	return strings.EqualFold(originFull, reqFull)
}

var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
	CheckOrigin:     validateOrigin,
}

func serveWs(hub *Hub, cfg *ServerConfig, w http.ResponseWriter, r *http.Request) {
	// IP whitelist enforcement
	if cfg.IPWhitelist && len(cfg.AllowedIPs) > 0 {
		var trustedProxies []*net.IPNet
		if srvGlobals != nil {
			trustedProxies = srvGlobals.TrustedProxies
		}
		clientIP := extractIP(r, trustedProxies)
		if !isAllowed(clientIP, cfg.AllowedIPs) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
	}

	// [M4] Enforce max connections
	if srvGlobals != nil && srvGlobals.MaxConnections > 0 {
		if hub.ClientCount() >= srvGlobals.MaxConnections {
			http.Error(w, "server is at capacity", http.StatusServiceUnavailable)
			return
		}
	}

	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Printf("WebSocket upgrade error: %v", err)
		return
	}

	client := NewClient(hub, conn)
	hub.Register(client)

	go client.WritePump()
	go client.ReadPump()
}

// extractIP returns the client IP, trusting proxy headers only from trusted proxies.
// [M2] If trustedProxies is non-nil and non-empty, proxy headers are only parsed
// when the direct connection (RemoteAddr) is from a trusted proxy.
func extractIP(r *http.Request, trustedProxies []*net.IPNet) string {
	directIP, _, _ := net.SplitHostPort(r.RemoteAddr)

	isTrusted := len(trustedProxies) == 0 // if no trusted proxies configured, trust all (backward compat)
	if !isTrusted {
		parsed := net.ParseIP(directIP)
		for _, cidr := range trustedProxies {
			if cidr.Contains(parsed) {
				isTrusted = true
				break
			}
		}
	}

	if isTrusted {
		// Check X-Real-IP / X-Forwarded-For for proxied setups
		if ip := r.Header.Get("X-Real-IP"); ip != "" {
			return ip
		}
		if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
			return strings.TrimSpace(strings.Split(xff, ",")[0])
		}
	}

	return directIP
}

func isAllowed(ip string, allowed []string) bool {
	for _, a := range allowed {
		if strings.Contains(a, "/") {
			_, cidr, err := net.ParseCIDR(a)
			if err == nil && cidr.Contains(net.ParseIP(ip)) {
				return true
			}
		} else if a == ip {
			return true
		}
	}
	return false
}

func main() {
	cfg := loadConfig()
	hub := NewHub(cfg.SessionMaxAge)
	hub.MaxFileMB = cfg.MaxFileMB
	hub.MinVersion = cfg.MinVersion
	hub.SessionStorePath = cfg.SessionStorePath
	if err := hub.LoadPersistedSessions(); err != nil {
		log.Printf("Failed to load persisted sessions: %v", err)
	}

	// [M2] Parse trusted proxy CIDRs once at startup
	var trustedCIDRs []*net.IPNet
	for _, cidr := range cfg.TrustedProxies {
		_, ipNet, err := net.ParseCIDR(cidr)
		if err != nil {
			log.Fatalf("Invalid TRUSTED_PROXIES entry %q: %v", cidr, err)
		}
		trustedCIDRs = append(trustedCIDRs, ipNet)
	}

	srvGlobals = &ServerGlobals{
		AllowEmptyOrigin: cfg.AllowEmptyOrigin,
		TrustedProxies:   trustedCIDRs,
		MaxConnections:   cfg.MaxConnections,
	}

	// [M4] Log connection limit
	if cfg.MaxConnections > 0 {
		log.Printf("Max connections: %d", cfg.MaxConnections)
	}

	// [L5] Warn if running without TLS
	if cfg.TLSCertFile == "" || cfg.TLSKeyFile == "" {
		log.Printf("WARNING: No TLS configured. Consider setting TLS_CERT_FILE and TLS_KEY_FILE for production use.")
	}

	mux := http.NewServeMux()

	// Health check
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		resp := map[string]string{"status": "ok", "version": ProtocolVersion}
		if data, err := json.Marshal(resp); err == nil {
			_, _ = w.Write(data)
		}
	})

	// WebSocket endpoint — optionally behind a secret path
	wsPath := "/ws"
	if cfg.PrivateMode && cfg.SecretPath != "" {
		wsPath = "/ws/" + cfg.SecretPath
		log.Printf("Private mode: WebSocket path enabled (path length: %d chars)", len(wsPath))
	}
	mux.HandleFunc(wsPath, func(w http.ResponseWriter, r *http.Request) {
		serveWs(hub, &cfg, w, r)
	})

	// Config endpoint (non-sensitive info for clients)
	mux.HandleFunc("/api/config", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		resp := map[string]interface{}{
			"version":          ProtocolVersion,
			"minVersion":       cfg.MinVersion,
			"fileTransfer":     cfg.MaxFileMB != -1,
			"maxFileMB":        cfg.MaxFileMB,
			"mobileExpirySecs": cfg.MobileExpiry,
			"sessionMaxAgeDays": cfg.SessionMaxAge,
		}
		if data, err := json.Marshal(resp); err == nil {
			_, _ = w.Write(data)
		}
	})

	// Serve mobile web static files
	if info, err := os.Stat(cfg.StaticDir); err == nil && info.IsDir() {
		fs := http.FileServer(http.Dir(cfg.StaticDir))
		mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Cache-Control", "no-cache, must-revalidate")
			fs.ServeHTTP(w, r)
		})
		log.Printf("Serving static files from %s", cfg.StaticDir)
	} else {
		mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "text/plain")
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write([]byte("Fluentia Relay Server"))
		})
	}

	srv := &http.Server{
		Addr:    ":" + cfg.Port,
		Handler: mux,
	}

	// Graceful shutdown on SIGINT/SIGTERM
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)

	errCh := make(chan error, 1)
	go func() {
		log.Printf("Fluentia relay server v%s starting on :%s", ProtocolVersion, cfg.Port)
		// [L5] Use TLS if configured
		if cfg.TLSCertFile != "" && cfg.TLSKeyFile != "" {
			log.Printf("TLS enabled with cert=%s key=%s", cfg.TLSCertFile, cfg.TLSKeyFile)
			if err := srv.ListenAndServeTLS(cfg.TLSCertFile, cfg.TLSKeyFile); err != nil && err != http.ErrServerClosed {
				errCh <- err
			}
		} else {
			if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
				errCh <- err
			}
		}
	}()

	select {
	case err := <-errCh:
		log.Fatalf("ListenAndServe error: %v", err)
	case sig := <-stop:
		log.Printf("Received signal %v, shutting down gracefully...", sig)

		// [L1] Send maintenance message to all connected clients before shutting down
		hub.ShutdownAll()
		log.Println("Sent maintenance notifications to connected clients")

		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()

		if err := srv.Shutdown(ctx); err != nil {
			log.Fatalf("Server shutdown error: %v", err)
		}
		log.Println("Server stopped")
	}
}
