package nativeclient

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
	"zvonok/internal/sfu"
)

func TestCryptoAuthentication(t *testing.T) {
	a, e := NewCrypto("AAAAAAAAAAAAAAAAAAAAAA")
	if e != nil {
		t.Fatal(e)
	}
	b, _ := NewCrypto("AQEBAQEBAQEBAQEBAQEBAQ")
	for _, video := range []bool{false, true} {
		data := []byte{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 11, 22, 33, 44}
		sealed := a.SealMedia(data, video)
		clear, e := a.OpenMedia(sealed, video)
		if e != nil || !bytes.Equal(clear, data) {
			t.Fatal("round trip", e)
		}
		if _, e = b.OpenMedia(sealed, video); e == nil {
			t.Fatal("wrong key accepted")
		}
		sealed[len(sealed)-15] ^= 1
		if _, e = a.OpenMedia(sealed, video); e == nil {
			t.Fatal("tampered frame accepted")
		}
		if _, e = a.OpenMedia(data[:3], video); e == nil {
			t.Fatal("plaintext accepted")
		}
	}
	if _, e = NewCrypto("bad"); e == nil {
		t.Fatal("bad key accepted")
	}
}
func TestTwoNativeClientsThroughSFU(t *testing.T) {
	engine, e := sfu.NewEngine(sfu.EngineConfig{Loopback: true})
	if e != nil {
		t.Fatal(e)
	}
	hub := sfu.NewHub(sfu.HubConfig{Engine: engine})
	server := httptest.NewServer(http.HandlerFunc(hub.ServeWS))
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	type event map[string]json.RawMessage
	events := [2]chan event{make(chan event, 100), make(chan event, 100)}
	clients := make([]*Client, 2)
	for i := range clients {
		ch := events[i]
		clients[i], e = New(ctx, Command{Server: server.URL + "/", Key: "AAAAAAAAAAAAAAAAAAAAAA", Name: "native"}, func(v any) {
			data, _ := json.Marshal(v)
			var ev event
			json.Unmarshal(data, &ev)
			select {
			case ch <- ev:
			case <-ctx.Done():
			}
		})
		if e != nil {
			t.Fatal(e)
		}
		defer clients[i].Close()
		go clients[i].Run()
	}
	wait := func(i int, typ string) event {
		for {
			select {
			case ev := <-events[i]:
				var kind string
				json.Unmarshal(ev["type"], &kind)
				if kind == "error" {
					t.Fatalf("client error %s", ev["error"])
				}
				if kind == typ {
					return ev
				}
			case <-ctx.Done():
				t.Fatalf("timed out waiting for %s", typ)
				return nil
			}
		}
	}
	wait(0, "welcome")
	wait(1, "welcome")
	if e = clients[0].Command(Command{Type: "chat", Text: "Привет из приложения"}); e != nil {
		t.Fatal(e)
	}
	message := wait(1, "chat")
	var text string
	json.Unmarshal(message["text"], &text)
	if text != "Привет из приложения" {
		t.Fatal(text)
	}
	// A valid Opus silence packet still exercises encryption, RTP, forwarding and authentication.
	packet := []byte{0xf8, 0xff, 0xfe}
	sendDone := make(chan struct{})
	defer close(sendDone)
	go func() {
		timer := time.NewTicker(20 * time.Millisecond)
		defer timer.Stop()
		for {
			select {
			case <-timer.C:
				for _, c := range clients {
					for _, source := range []string{"mic", "screen-audio"} {
						c.Command(Command{Type: "media", Source: source, Data: base64.StdEncoding.EncodeToString(packet), Duration: 20000})
					}
				}
			case <-sendDone:
				return
			case <-ctx.Done():
				return
			}
		}
	}()
	for i := range clients {
		received := map[string]bool{}
		for len(received) < 2 {
			frame := wait(i, "frame")
			var data []byte
			var source string
			json.Unmarshal(frame["data"], &data)
			json.Unmarshal(frame["source"], &source)
			if !bytes.Equal(data, packet) {
				t.Fatal("received media differs")
			}
			if source != "mic" && source != "screen-audio" {
				t.Fatal("unexpected source", source)
			}
			received[source] = true
		}
	}
}
