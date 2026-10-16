package server

import (
	"bufio"
	"crypto/tls"
	"encoding/binary"
	"net"
	"sync"
	"time"

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
func (l *chanListener) push(c net.Conn) {
	select {
	case l.ch <- c:
	case <-l.done:
		_ = c.Close()
	}
}

type peekedConn struct {
	net.Conn
	r *bufio.Reader
}

func (c *peekedConn) Read(b []byte) (int, error) { return c.r.Read(b) }

type Demuxer struct {
	TLSConfig   *tls.Config
	ICELn       *chanListener
	TURNLn      *chanListener
	HTTPLn      *chanListener
	AllowICETCP bool
	Log         logging.LeveledLogger
}

func (d *Demuxer) Serve(real net.Listener) error {
	for {
		raw, err := real.Accept()
		if err != nil {
			return err
		}
		go d.route(raw)
	}
}

func (d *Demuxer) route(raw net.Conn) {
	_ = raw.SetReadDeadline(time.Now().Add(peekTimeout))
	br := bufio.NewReaderSize(raw, 2048)
	hdr, err := br.Peek(peekBytes)
	if err != nil {
		_ = raw.Close()
		return
	}
	_ = raw.SetReadDeadline(time.Time{})
	conn := &peekedConn{Conn: raw, r: br}

	switch {
	case hdr[0] == 0x16 && hdr[1] == 0x03:
		d.handleTLS(conn)
	case d.AllowICETCP && hdr[2]&0xC0 == 0 && binary.BigEndian.Uint32(hdr[6:10]) == stunMagicCookie:

		d.ICELn.push(conn)
	default:

		_ = raw.Close()
	}
}

func (d *Demuxer) handleTLS(raw net.Conn) {
	_ = raw.SetReadDeadline(time.Now().Add(peekTimeout))
	tc := tls.Server(raw, d.TLSConfig)
	if err := tc.Handshake(); err != nil {
		_ = raw.Close()
		return
	}
	proto := tc.ConnectionState().NegotiatedProtocol
	if proto == "acme-tls/1" {
		_ = tc.Close()
		return
	}
	inner := bufio.NewReaderSize(tc, 2048)
	first, err := inner.Peek(8)
	_ = raw.SetReadDeadline(time.Time{})
	wrapped := &peekedConn{Conn: tc, r: inner}
	if err == nil && first[0]&0xC0 == 0 && binary.BigEndian.Uint32(first[4:8]) == stunMagicCookie {
		d.TURNLn.push(wrapped)
		return
	}
	d.HTTPLn.push(wrapped)
}
