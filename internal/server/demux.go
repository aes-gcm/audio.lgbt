package server

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/binary"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/aes-gcm/audio.lgbt/internal/observe"
	"github.com/pion/logging"
)

const (
	stunMagicCookie = 0x2112A442
	peekTimeout     = 12 * time.Second
	peekBytes       = 10
)

type chanListener struct {
	addr   net.Addr
	ch     chan net.Conn
	done   chan struct{}
	closer sync.Once
}

func newChanListener(addr net.Addr) *chanListener {
	return &chanListener{addr: addr, ch: make(chan net.Conn, 128), done: make(chan struct{})}
}

func (l *chanListener) Accept() (net.Conn, error) {
	select {
	case c := <-l.ch:
		return c, nil
	case <-l.done:
		return nil, net.ErrClosed
	}
}
func (l *chanListener) Close() error   { l.closer.Do(func() { close(l.done) }); return nil }
func (l *chanListener) Addr() net.Addr { return l.addr }
func (l *chanListener) push(c net.Conn) bool {
	select {
	case l.ch <- c:
		return true
	case <-l.done:
		_ = c.Close()
	default:
		_ = c.Close()
	}
	return false
}

type peekedConn struct {
	net.Conn
	r        *bufio.Reader
	tlsState *tls.ConnectionState
}

type tlsContextKey struct{}

func tlsConnContext(ctx context.Context, conn net.Conn) context.Context {
	if c, ok := conn.(*peekedConn); ok && c.tlsState != nil {
		return context.WithValue(ctx, tlsContextKey{}, c.tlsState)
	}
	return ctx
}

func restoreTLSState(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if state, ok := r.Context().Value(tlsContextKey{}).(*tls.ConnectionState); ok {
			r.TLS = state
		}
		next.ServeHTTP(w, r)
	})
}

func (c *peekedConn) Read(b []byte) (int, error) { return c.r.Read(b) }

type Demuxer struct {
	TLSConfig   *tls.Config
	ICELn       *chanListener
	TURNLn      *chanListener
	HTTPLn      *chanListener
	AllowICETCP bool
	Log         logging.LeveledLogger
	Meter       *observe.Meter
}

func (d *Demuxer) Serve(real net.Listener) error {
	pending := make(chan struct{}, 128)
	for {
		raw, err := real.Accept()
		if err != nil {
			return err
		}
		select {
		case pending <- struct{}{}:
			d.Meter.Gauge("handshakes_pending", 1)
			go func() { defer func() { <-pending; d.Meter.Gauge("handshakes_pending", -1) }(); d.route(raw) }()
		default:
			d.Meter.Add("handshake_capacity_rejected", 1)
			_ = raw.Close()
		}
	}
}

func (d *Demuxer) route(raw net.Conn) {
	_ = raw.SetReadDeadline(time.Now().Add(peekTimeout))
	br := bufio.NewReaderSize(raw, 2048)
	hdr, err := br.Peek(peekBytes)
	if err != nil {
		d.Meter.Add("tcp_peek_failed", 1)
		_ = raw.Close()
		return
	}
	_ = raw.SetReadDeadline(time.Time{})
	conn := &peekedConn{Conn: raw, r: br}

	switch {
	case hdr[0] == 0x16 && hdr[1] == 0x03:
		d.handleTLS(conn)
	case d.AllowICETCP && hdr[2]&0xC0 == 0 && binary.BigEndian.Uint32(hdr[6:10]) == stunMagicCookie:

		d.deliver(d.ICELn, conn, "ice_tcp_routed")
	default:

		d.Meter.Add("tcp_protocol_rejected", 1)
		_ = raw.Close()
	}
}

func (d *Demuxer) handleTLS(raw net.Conn) {
	_ = raw.SetDeadline(time.Now().Add(peekTimeout))
	tc := tls.Server(raw, d.TLSConfig)
	if err := tc.Handshake(); err != nil {
		d.Meter.Add("tls_failed", 1)
		_ = raw.Close()
		return
	}
	proto := tc.ConnectionState().NegotiatedProtocol
	d.Meter.Add("tls_ok", 1)
	if proto == "acme-tls/1" {
		_ = tc.Close()
		return
	}
	inner := bufio.NewReaderSize(tc, 2048)
	first, err := inner.Peek(8)
	_ = raw.SetDeadline(time.Time{})
	state := tc.ConnectionState()
	wrapped := &peekedConn{Conn: tc, r: inner, tlsState: &state}
	if err == nil && first[0]&0xC0 == 0 && binary.BigEndian.Uint32(first[4:8]) == stunMagicCookie {
		d.deliver(d.TURNLn, wrapped, "turn_tls_routed")
		return
	}
	if err != nil {
		d.Meter.Add("tls_peek_failed", 1)
		_ = raw.Close()
		return
	}
	d.deliver(d.HTTPLn, wrapped, "https_routed")
}

func (d *Demuxer) deliver(listener *chanListener, conn net.Conn, kind string) {
	if listener.push(conn) {
		d.Meter.Add(kind, 1)
	} else {
		d.Meter.Add("protocol_queue_rejected", 1)
	}
}
