package server

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"net"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/aes-gcm/audio.lgbt/internal/accounts"
)

func TestBrowserSecurityHeaders(t *testing.T) {
	w := httptest.NewRecorder()
	securityHeaders(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(200) }), true).ServeHTTP(w, httptest.NewRequest("GET", "https://calls.example/", nil))
	if !strings.Contains(w.Header().Get("Content-Security-Policy"), "script-src 'self';") || !strings.Contains(w.Header().Get("Content-Security-Policy"), "frame-ancestors 'none'") || w.Header().Get("Strict-Transport-Security") == "" || w.Header().Get("Referrer-Policy") != "no-referrer" {
		t.Fatal(w.Header())
	}
}

func TestLoginThroughRealTLSDemux(t *testing.T) {
	path := filepath.Join(t.TempDir(), "accounts.json")
	if err := accounts.SetUser(path, "owner", "test-only-password-123", false); err != nil {
		t.Fatal(err)
	}
	store, err := accounts.New(path, true, true)
	if err != nil {
		t.Fatal(err)
	}
	cert, err := selfSigned("localhost")
	if err != nil {
		t.Fatal(err)
	}
	root, err := x509.ParseCertificate(cert.Certificate[0])
	if err != nil {
		t.Fatal(err)
	}
	roots := x509.NewCertPool()
	roots.AddCert(root)
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	httpLn := newChanListener(listener.Addr())
	defer httpLn.Close()
	d := &Demuxer{TLSConfig: &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS12}, HTTPLn: httpLn}
	srv := &http.Server{Handler: restoreTLSState(http.HandlerFunc(store.Login)), ConnContext: tlsConnContext, ReadHeaderTimeout: time.Second}
	defer srv.Close()
	go srv.Serve(httpLn)
	go d.Serve(listener)
	transport := &http.Transport{TLSClientConfig: &tls.Config{RootCAs: roots, ServerName: "localhost", MinVersion: tls.VersionTLS12}}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 5 * time.Second}
	endpoint := "https://" + listener.Addr().String()
	for _, test := range []struct {
		origin string
		status int
	}{{endpoint, 200}, {"https://evil.example", 403}} {
		r, _ := http.NewRequest("POST", endpoint+"/api/login", strings.NewReader(`{"username":"owner","password":"test-only-password-123"}`))
		r.Header.Set("Origin", test.origin)
		r.Header.Set("Content-Type", "application/json")
		response, err := client.Do(r)
		if err != nil {
			t.Fatal(err)
		}
		response.Body.Close()
		if response.StatusCode != test.status {
			t.Fatalf("origin %s: %d", test.origin, response.StatusCode)
		}
	}
}

func TestDemuxPreservesTLSRequestState(t *testing.T) {
	state := &tls.ConnectionState{Version: tls.VersionTLS13, HandshakeComplete: true}
	ctx := tlsConnContext(context.Background(), &peekedConn{tlsState: state})
	r := httptest.NewRequest("POST", "http://calls.example/api/login", nil).WithContext(ctx)
	restoreTLSState(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.TLS != state {
			t.Fatal("TLS state lost")
		}
	})).ServeHTTP(httptest.NewRecorder(), r)
	r = httptest.NewRequest("POST", "http://calls.example/api/login", nil)
	r.Header.Set("X-Forwarded-Proto", "https")
	restoreTLSState(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.TLS != nil {
			t.Fatal("untrusted header established TLS")
		}
	})).ServeHTTP(httptest.NewRecorder(), r)
}
