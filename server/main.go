package main

import (
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"

	"github.com/gorilla/websocket"
)

// ServerConfig holds all environment-driven settings.
type ServerConfig struct {
	Port          string
	StaticDir     string
	SecretPath    string   // if set, WS is served at /ws/<secret> instead of /ws
	AllowedIPs    []string // if non-empty, only these IPs may connect
	MaxFileMB     int      // -1=disabled, 0=unlimited, N=N MB
	MobileExpiry  int      // seconds after mobile disconnects before PC shows window (default 60)
	SessionMaxAge int      // how long a session token remains reusable, in days
	PrivateMode   bool     // require SecretPath
	IPWhitelist   bool     // enforce AllowedIPs
}

func loadConfig() ServerConfig {
	cfg := ServerConfig{
		Port:         envOr("PORT", "8080"),
		StaticDir:    envOr("STATIC_DIR", "./static"),
		MaxFileMB:    envInt("MAX_FILE_MB", -1),
		MobileExpiry: envInt("MOBILE_EXPIRY_SECS", 60),
		SessionMaxAge: envInt("SESSION_MAX_AGE_DAYS", 7),
	}

	cfg.SecretPath = os.Getenv("SECRET_PATH")
	cfg.PrivateMode = envBool("PRIVATE_MODE", false)
	cfg.IPWhitelist = envBool("IP_WHITELIST", false)

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

var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
	CheckOrigin:     func(r *http.Request) bool { return true },
}

func serveWs(hub *Hub, cfg ServerConfig, w http.ResponseWriter, r *http.Request) {
	// IP whitelist enforcement
	if cfg.IPWhitelist && len(cfg.AllowedIPs) > 0 {
		clientIP := extractIP(r)
		if !isAllowed(clientIP, cfg.AllowedIPs) {
			http.Error(w, "forbidden", http.StatusForbidden)
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

func extractIP(r *http.Request) string {
	// Check X-Real-IP / X-Forwarded-For for proxied setups
	if ip := r.Header.Get("X-Real-IP"); ip != "" {
		return ip
	}
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		return strings.TrimSpace(strings.Split(xff, ",")[0])
	}
	host, _, _ := net.SplitHostPort(r.RemoteAddr)
	return host
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

	mux := http.NewServeMux()

	// Health check
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"status":"ok","version":"` + ProtocolVersion + `"}`))
	})

	// WebSocket endpoint — optionally behind a secret path
	wsPath := "/ws"
	if cfg.PrivateMode && cfg.SecretPath != "" {
		wsPath = "/ws/" + cfg.SecretPath
		log.Printf("Private mode: WebSocket path = %s", wsPath)
	}
	mux.HandleFunc(wsPath, func(w http.ResponseWriter, r *http.Request) {
		serveWs(hub, cfg, w, r)
	})

	// Config endpoint (non-sensitive info for clients)
	mux.HandleFunc("/api/config", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		fileEnabled := cfg.MaxFileMB != -1
		maxMB := cfg.MaxFileMB
		w.Write([]byte(`{"version":"` + ProtocolVersion + `","fileTransfer":` +
			strconv.FormatBool(fileEnabled) + `,"maxFileMB":` + strconv.Itoa(maxMB) +
			`,"mobileExpirySecs":` + strconv.Itoa(cfg.MobileExpiry) +
			`,"sessionMaxAgeDays":` + strconv.Itoa(cfg.SessionMaxAge) + `}`))
	})

	// Serve mobile web static files
	if info, err := os.Stat(cfg.StaticDir); err == nil && info.IsDir() {
		fs := http.FileServer(http.Dir(cfg.StaticDir))
		mux.Handle("/", fs)
		log.Printf("Serving static files from %s", cfg.StaticDir)
	} else {
		mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "text/plain")
			w.WriteHeader(http.StatusOK)
			w.Write([]byte("Fluentia Relay Server"))
		})
	}

	log.Printf("Fluentia relay server v%s starting on :%s", ProtocolVersion, cfg.Port)
	if err := http.ListenAndServe(":"+cfg.Port, mux); err != nil {
		log.Fatal(err)
	}
}
