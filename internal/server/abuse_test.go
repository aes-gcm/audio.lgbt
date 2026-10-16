package server

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/aes-gcm/audio.lgbt/internal/observe"
	"github.com/aes-gcm/audio.lgbt/internal/sfu"
	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

func TestWebsocketFloodDoesNotCloseOtherParticipant(t *testing.T) {
	engine, err := sfu.NewEngine(sfu.EngineConfig{Loopback: true})
	if err != nil {
		t.Fatal(err)
	}
	meter := observe.New()
	hub := sfu.NewHub(sfu.HubConfig{Engine: engine, Meter: meter})
	server := httptest.NewServer(meter.HTTP(newGuard(meter).HTTP(http.HandlerFunc(hub.ServeWS))))
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	join := func(name string) *websocket.Conn {
		c, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(server.URL, "http")+"/ws", nil)
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { c.CloseNow() })
		if err = wsjson.Write(ctx, c, sfu.Msg{Type: "join", Room: "abuse-fixture", Name: name}); err != nil {
			t.Fatal(err)
		}
		var welcome sfu.Msg
		if err = wsjson.Read(ctx, c, &welcome); err != nil || welcome.Type != "welcome" {
			t.Fatalf("welcome %v %s", err, welcome.Type)
		}
		return c
	}
	healthy := join("healthy")
	abusive := join("abusive")
	for range 180 {
		if err = wsjson.Write(ctx, abusive, sfu.Msg{Type: "unknown"}); err != nil {
			break
		}
	}
	for {
		var m sfu.Msg
		if wsjson.Read(ctx, abusive, &m) != nil {
			break
		}
	}
	foundLeave := false
	for !foundLeave {
		var m sfu.Msg
		if err = wsjson.Read(ctx, healthy, &m); err != nil {
			t.Fatal("healthy disconnected", err)
		}
		foundLeave = m.Type == "peer-left"
	}
	if err = wsjson.Write(ctx, healthy, sfu.Msg{Type: "name", Name: "still-here"}); err != nil {
		t.Fatal(err)
	}
	c, g := meter.Snapshot()
	if c["ws_message_rejected"] != 1 || c["ws_upgraded"] != 2 || g["ws_handlers"] != 1 {
		t.Fatalf("metrics: %v %v", c, g)
	}
}
