package server

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"mime"
	"net"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/logging"

	"github.com/aes-gcm/audio.lgbt/internal/accounts"
	"github.com/aes-gcm/audio.lgbt/internal/config"
	"github.com/aes-gcm/audio.lgbt/internal/observe"
	"github.com/aes-gcm/audio.lgbt/internal/sfu"
	"github.com/aes-gcm/audio.lgbt/web"
)

func init() {

	_ = mime.AddExtensionType(".woff2", "font/woff2")
}

type Server struct {
	accounts *accounts.Store
	cfg      *config.Config
	log      logging.LoggerFactory
	udpMux   ice.UDPMux
	hub      *sfu.Hub
	meter    *observe.Meter
	guard    *guard
}

func New(cfg *config.Config) (*Server, error) {
	users, err := accounts.New(cfg.AccountsFile, cfg.RequireAuth, !cfg.Dev)
	if err != nil {
		return nil, err
	}
	lf := logging.NewDefaultLoggerFactory()

	loopback := cfg.Dev || containsLoopback(cfg.PublicIPs)
	opts := []ice.UDPMuxFromPortOption{ice.UDPMuxFromPortWithLogger(lf.NewLogger("udpmux"))}
	if loopback {
		opts = append(opts, ice.UDPMuxFromPortWithLoopback())
	}
	udpMux, err := ice.NewMultiUDPMuxFromPort(cfg.UDPPort, opts...)
	if err != nil {
		return nil, fmt.Errorf("listen UDP :%d: %w", cfg.UDPPort, err)
	}
	meter := observe.New()
	return &Server{cfg: cfg, log: lf, udpMux: udpMux, accounts: users, meter: meter, guard: newGuard(meter)}, nil
}

func (s *Server) buildHub(net sfu.ServerNet, iceServers func(string) []sfu.ICEServer) error {
	loopback := s.cfg.Dev || containsLoopback(s.cfg.PublicIPs)
	engine, err := sfu.NewEngine(sfu.EngineConfig{
		Net:       net,
		PublicIPs: s.cfg.PublicIPs,
		Lite:      !s.cfg.Dev,
		Loopback:  loopback,
		Log:       s.log,
	})
	if err != nil {
		return err
	}
	s.hub = sfu.NewHub(sfu.HubConfig{
		RequireAuth: s.cfg.RequireAuth,
		Meter:       s.meter,
		CanCreate:   s.accounts.CanCreate,
		Engine:      engine,
		Log:         s.log,
		MaxRoom:     s.cfg.MaxRoom,
		MaxRooms:    s.cfg.MaxRooms,
		RoomSecret:  s.cfg.RoomSecret,
		ICEServers:  iceServers,
	})
	return nil
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/server", s.accounts.Status)
	mux.HandleFunc("/api/login", s.accounts.Login)
	mux.HandleFunc("/api/logout", s.accounts.Logout)
	mux.HandleFunc("/ws", s.hub.ServeWS)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("ok")) })
	mux.Handle("/downloads/", downloadHandler(s.cfg.DownloadsDir))
	mux.HandleFunc("/download", serveDownload)
	mux.HandleFunc("/download/", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/download/" {
			http.NotFound(w, r)
			return
		}
		http.Redirect(w, r, "/download", http.StatusPermanentRedirect)
	})
	mux.Handle("/", noCacheHTML(http.FileServerFS(web.FS)))
	return securityHeaders(s.meter.HTTP(s.guard.HTTP(mux)), !s.cfg.Dev)
}

var installerVersion = regexp.MustCompile(`^audio\.lgbt-(\d+\.\d+\.\d+(?:-preview\.\d+)?)-x64\.exe$`)

func downloadHandler(directory string) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := path.Base(r.URL.Path)
		match := installerVersion.FindStringSubmatch(name)
		if r.URL.Path != "/downloads/"+name || (name != "audio.lgbt-Setup.exe" && match == nil) {
			if strings.HasSuffix(r.URL.Path, ".exe") {
				http.NotFound(w, r)
				return
			}
			noCacheHTML(http.FileServerFS(web.FS)).ServeHTTP(w, r)
			return
		}
		w.Header().Set("Cache-Control", "no-cache")
		if directory != "" {
			file := filepath.Join(directory, name)
			if info, err := os.Stat(file); err == nil && info.Mode().IsRegular() {
				http.ServeFile(w, r, file)
				return
			}
		}
		version := "0.3.0-preview.8"
		if match != nil {
			version = match[1]
		}
		http.Redirect(w, r, "https://github.com/aes-gcm/audio.lgbt-windows/releases/download/v"+version+"/audio.lgbt-"+version+"-x64.exe", http.StatusTemporaryRedirect)
	})
}

func serveDownload(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-cache")
	http.ServeFileFS(w, r, web.FS, "download.html")
}

func (s *Server) Run(ctx context.Context) error {
	go s.meter.Run(ctx, os.Stdout, 15*time.Second)
	if s.cfg.Dev {
		return s.runDev(ctx)
	}
	return s.runProd(ctx)
}

func (s *Server) runDev(ctx context.Context) error {
	if err := s.buildHub(sfu.ServerNet{UDPMux: s.udpMux}, nil); err != nil {
		return err
	}
	ln, err := net.Listen("tcp", s.cfg.HTTPAddr)
	if err != nil {
		return err
	}
	srv := &http.Server{Handler: s.Handler(), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 20 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 16 << 10}
	s.log.NewLogger("server").Infof("audio.lgbt dev on http://%s (UDP media :%d)", ln.Addr(), s.cfg.UDPPort)
	go func() { <-ctx.Done(); shutdown(srv) }()
	if err := srv.Serve(&guardedListener{Listener: ln, guard: s.guard}); err != nil && err != http.ErrServerClosed {
		return err
	}
	return nil
}

func noCacheHTML(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/server-auth.js" || r.URL.Path == "/bootstrap.js" {
			w.Header().Set("Cache-Control", "no-cache")
		}
		if r.URL.Path == "/" || r.URL.Path == "/index.html" || r.URL.Path == "/download.html" || r.URL.Path == "/download.js" || r.URL.Path == "/app.js" || r.URL.Path == "/recent-rooms.js" || r.URL.Path == "/style.css" || r.URL.Path == "/e2ee-worker.js" || r.URL.Path == "/downloads/windows-x64.json" || r.URL.Path == "/downloads/audio.lgbt-Setup.exe" || r.URL.Path == "/downloads/audio.lgbt-Setup.exe.sha256" {
			w.Header().Set("Cache-Control", "no-cache")
		}
		next.ServeHTTP(w, r)
	})
}

func shutdown(srv *http.Server) {
	sctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	_ = srv.Shutdown(sctx)
}

func containsLoopback(ips []string) bool {
	for _, s := range ips {
		if ip := net.ParseIP(s); ip != nil && ip.IsLoopback() {
			return true
		}
	}
	return false
}

func hostOf(addr string) string {
	h, _, err := net.SplitHostPort(addr)
	if err != nil || h == "" || h == "0.0.0.0" || h == "::" {
		return ""
	}
	return h
}

func portOf(addr string) string {
	_, p, err := net.SplitHostPort(addr)
	if err != nil {
		return "443"
	}
	return p
}

func firstNonEmpty(vs ...string) string {
	for _, v := range vs {
		if v != "" {
			return v
		}
	}
	return ""
}

func randomSecret() string {
	var b [32]byte
	_, _ = rand.Read(b[:])
	return hex.EncodeToString(b[:])
}

func parseIPs(ss []string) []net.IP {
	out := make([]net.IP, 0, len(ss))
	for _, s := range ss {
		if ip := net.ParseIP(s); ip != nil {
			out = append(out, ip)
		}
	}
	return out
}
