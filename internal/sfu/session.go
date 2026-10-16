package sfu

import (
	"context"
	"crypto/rand"
	"encoding/base32"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"golang.org/x/time/rate"
)

const (
	sendBuffer   = 128
	writeTimeout = 10 * time.Second
	readLimit    = 256 << 10
)

func (h *Hub) ServeWS(w http.ResponseWriter, r *http.Request) {
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		CompressionMode: websocket.CompressionDisabled,
	})
	if err != nil {
		return
	}
	c.SetReadLimit(2048)
	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()

	var join Msg
	rctx, rcancel := context.WithTimeout(ctx, 10*time.Second)
	err = wsjson.Read(rctx, c, &join)
	rcancel()
	if err != nil || join.Type != "join" {
		h.meter.Add("ws_invalid_join", 1)
		_ = c.Close(websocket.StatusPolicyViolation, "expected join")
		return
	}
	c.SetReadLimit(readLimit)
	authorized := h.canCreate != nil && h.canCreate(r)
	room, err := h.joinAuthorized(strings.TrimSpace(join.Room), join.Token, authorized)
	if err != nil {
		h.meter.Add("room_join_rejected", 1)
		_ = writeMsg(ctx, c, Msg{Type: "error", Error: err.Error()})
		_ = c.Close(websocket.StatusPolicyViolation, err.Error())
		return
	}

	name := clip(join.Name, 64)
	if name == "" {
		name = "Гость"
	}
	p := newParticipant(h, room, newID(), name)
	p.canOpenRoom = h.mayCreate(join.Token, authorized)
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
	messages := rate.NewLimiter(30, 120)
	bytes := rate.NewLimiter(256<<10, 512<<10)
	negotiations := rate.NewLimiter(2, 8)
	h.meter.Add("room_joined", 1)

	for {
		kind, reader, err := c.Reader(ctx)
		if err != nil {
			break
		}
		if !messages.Allow() || kind != websocket.MessageText {
			h.meter.Add("ws_message_rejected", 1)
			break
		}
		raw, err := io.ReadAll(reader)
		if err != nil {
			break
		}
		if !bytes.AllowN(time.Now(), len(raw)) {
			h.meter.Add("ws_bytes_rejected", 1)
			break
		}
		var m Msg
		if json.Unmarshal(raw, &m) != nil {
			h.meter.Add("ws_invalid_message", 1)
			break
		}
		if (m.Type == "publish-offer" || m.Type == "subscribe-answer" || m.Type == "restart-sub-ice") && !negotiations.Allow() {
			h.meter.Add("ws_negotiation_rejected", 1)
			break
		}
		mm := m
		p.post(func() { p.handle(mm) })
	}
	p.Close()
	h.meter.Add("room_left", 1)
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
