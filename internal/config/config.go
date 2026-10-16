package config

import (
	"flag"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
)

type Config struct {
	RequireAuth  bool
	AccountsFile string
	DownloadsDir string

	Dev bool

	Domain string

	HTTPAddr string

	TLSAddr string

	UDPPort int

	PublicIPs []string

	TLSCert string
	TLSKey  string

	SelfSigned bool

	CertDir string

	RoomSecret string

	MaxRoom int

	MaxRooms int

	ICETCP bool
}

func Parse(args []string) (*Config, error) {
	fs := flag.NewFlagSet("audio.lgbt", flag.ContinueOnError)
	c := &Config{}
	var publicIPs string
	requireAuth, err := strconv.ParseBool(env("AUDIO_REQUIRE_AUTH", "false"))
	if err != nil {
		return nil, fmt.Errorf("AUDIO_REQUIRE_AUTH must be true or false")
	}
	fs.BoolVar(&c.RequireAuth, "require-auth", requireAuth, "require a local account to create rooms; invited guests need no account")
	fs.StringVar(&c.AccountsFile, "accounts", env("AUDIO_ACCOUNTS_FILE", "data/accounts.json"), "local account file")
	fs.StringVar(&c.DownloadsDir, "downloads-dir", env("AUDIO_DOWNLOADS_DIR", ""), "optional installer mirror for older clients")

	fs.BoolVar(&c.Dev, "dev", false, "development mode: plain HTTP on -http, no TLS/TURN (localhost only)")
	fs.StringVar(&c.Domain, "domain", env("AUDIO_DOMAIN", ""), "public hostname for Let's Encrypt certificates")
	fs.StringVar(&c.HTTPAddr, "http", "127.0.0.1:8080", "dev-mode HTTP listen address")
	fs.StringVar(&c.TLSAddr, "tls", ":443", "production TCP listen address (HTTPS + WSS + TURN-TLS)")
	fs.IntVar(&c.UDPPort, "udp", 3478, "UDP port for direct WebRTC media (never 443)")
	fs.StringVar(&publicIPs, "public-ip", env("AUDIO_PUBLIC_IP", ""), "comma-separated public IP(s) to announce as ICE candidates")
	fs.StringVar(&c.TLSCert, "tls-cert", "", "path to a TLS certificate (PEM); disables Let's Encrypt")
	fs.StringVar(&c.TLSKey, "tls-key", "", "path to the TLS private key (PEM)")
	fs.BoolVar(&c.SelfSigned, "self-signed", false, "use an in-memory self-signed certificate (testing only)")
	fs.StringVar(&c.CertDir, "cert-dir", "audio-certs", "directory for cached Let's Encrypt certificates")
	fs.StringVar(&c.RoomSecret, "room-secret", env("AUDIO_ROOM_SECRET", ""), "secret required to create (not join) rooms")
	fs.IntVar(&c.MaxRoom, "max-room", 25, "maximum participants per room")
	fs.IntVar(&c.MaxRooms, "max-rooms", 2000, "maximum concurrent rooms (DoS protection)")
	fs.BoolVar(&c.ICETCP, "ice-tcp", false, "advertise a plaintext ICE-TCP candidate on 443 (less DPI-stealthy; off by default)")

	if err := fs.Parse(args); err != nil {
		return nil, err
	}

	for _, ip := range strings.Split(publicIPs, ",") {
		ip = strings.TrimSpace(ip)
		if ip == "" {
			continue
		}
		if net.ParseIP(ip) == nil {
			return nil, fmt.Errorf("invalid -public-ip %q", ip)
		}
		c.PublicIPs = append(c.PublicIPs, ip)
	}

	if c.MaxRoom < 2 {
		return nil, fmt.Errorf("-max-room must be at least 2")
	}
	if c.MaxRooms < 1 {
		return nil, fmt.Errorf("-max-rooms must be at least 1")
	}
	if !c.Dev && c.Domain == "" && c.TLSCert == "" && !c.SelfSigned {
		return nil, fmt.Errorf("production mode needs one of -domain, -tls-cert/-tls-key or -self-signed (or use -dev)")
	}
	if (c.TLSCert == "") != (c.TLSKey == "") {
		return nil, fmt.Errorf("-tls-cert and -tls-key must be given together")
	}
	return c, nil
}

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}
