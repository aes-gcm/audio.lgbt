package sfu

import (
	"github.com/pion/webrtc/v4"
	"strings"
	"testing"
	"time"
)

func TestTransportRestartKeepsLockedRoomMembership(t *testing.T) {
	h := NewHub(HubConfig{})
	room, _ := h.joinRoom("transport", "")
	p := newParticipant(h, room, "host", "Host")
	if _, err := room.add(p); err != nil {
		t.Fatal(err)
	}
	room.setLocked(true)
	var err error
	p.sub, err = webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = p.sub.AddTransceiverFromKind(webrtc.RTPCodecTypeAudio); err != nil {
		t.Fatal(err)
	}
	remote, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer remote.Close()
	offers := make(chan Msg, 4)
	p.out = func(m Msg) {
		if m.Type == "subscribe-offer" {
			offers <- m
		}
	}
	go p.run()
	t.Cleanup(func() {
		p.Close()
		select {
		case <-p.done:
		case <-time.After(3 * time.Second):
			t.Error("actor did not close")
		}
	})
	nextOffer := func() Msg {
		select {
		case m := <-offers:
			return m
		case <-time.After(3 * time.Second):
			t.Fatal("no subscribe offer")
			return Msg{}
		}
	}
	answer := func(m Msg) {
		if err := remote.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: m.SDP}); err != nil {
			t.Fatal(err)
		}
		a, err := remote.CreateAnswer(nil)
		if err != nil {
			t.Fatal(err)
		}
		if err := remote.SetLocalDescription(a); err != nil {
			t.Fatal(err)
		}
		p.post(func() { p.onSubscribeAnswer(Msg{SDP: a.SDP}) })
	}
	p.post(func() { p.subDirty = true; p.doNego() })
	first := nextOffer()
	answer(first)
	p.post(func() { p.handle(Msg{Type: "restart-sub-ice"}) })
	second := nextOffer()
	answer(second)
	ufrag := func(sdp string) string {
		for _, l := range strings.Split(sdp, "\r\n") {
			if strings.HasPrefix(l, "a=ice-ufrag:") {
				return l
			}
		}
		return ""
	}
	if ufrag(first.SDP) == "" || ufrag(first.SDP) == ufrag(second.SDP) {
		t.Fatal("restart did not replace ICE credentials")
	}
	if !room.isHost(p.id) || !room.isLocked() || len(room.allParticipants()) != 1 {
		t.Fatal("restart changed room membership or host/lock state")
	}
}
