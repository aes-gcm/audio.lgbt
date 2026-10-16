package sfu

import (
	"context"
	"crypto/rand"
	"encoding/base32"
	"net/http"
	"strings"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

const (
	sendBuffer   = 128
	writeTimeout = 10 * time.Second
	readLimit    = 1 << 20
)

func (h *Hub) ServeWS(w http.ResponseWriter, r *http.Request) {
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		CompressionMode: websocket.CompressionDisabled,
	})
	if err != nil {
		return
	}
	c.SetReadLimit(readLimit)
	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()

	var join Msg
	rctx, rcancel := context.WithTimeout(ctx, 20*time.Second)
	err = wsjson.Read(rctx, c, &join)
	rcancel()
	if err != nil || join.Type != "join" {
		_ = c.Close(websocket.StatusPolicyViolation, "expected join")
		return
	}
	authorized := h.canCreate != nil && h.canCreate(r)
	room, err := h.joinAuthorized(strings.TrimSpace(join.Room), join.Token, authorized)
	if err != nil {
		_ = writeMsg(ctx, c, Msg{Type: "error", Error: err.Error()})
		_ = c.Close(websocket.StatusPolicyViolation, err.Error())
		return
	}

	name := clip(join.Name, 64)
	if name == "" {
		name = "Гость"
	}
	p := newParticipant(h, room, newID(), name)
	p.closeWS = cancel

	sendCh := make(chan Msg, sendBuffer)
	p.out = func(m Msg) {
		select {
		case sendCh <- m:
		case <-p.done:
		default:

			p.Close()
		}
	}
	go func() {
		for {
			select {
			case m := <-sendCh:
				wctx, wcancel := context.WithTimeout(ctx, writeTimeout)
				errw := wsjson.Write(wctx, c, m)
				wcancel()
				if errw != nil {
					p.Close()
					return
				}
			case <-p.done:
				return
			case <-ctx.Done():
				return
			}
		}
	}()

	if err := p.start(); err != nil {
		p.Close()
		go p.run()
		_ = writeMsg(ctx, c, Msg{Type: "error", Error: err.Error()})
		_ = c.Close(websocket.StatusInternalError, "start failed")
		return
	}
	go p.run()

	for {
		var m Msg
		if err := wsjson.Read(ctx, c, &m); err != nil {
			break
		}
		mm := m
		p.post(func() { p.handle(mm) })
	}
	p.Close()
	_ = c.Close(websocket.StatusNormalClosure, "")
}

func writeMsg(ctx context.Context, c *websocket.Conn, m Msg) error {
	wctx, cancel := context.WithTimeout(ctx, writeTimeout)
	defer cancel()
	return wsjson.Write(wctx, c, m)
}

func newID() string {
	var b [10]byte
	_, _ = rand.Read(b[:])
	return strings.ToLower(base32.StdEncoding.WithPadding(base32.NoPadding).EncodeToString(b[:]))
}
