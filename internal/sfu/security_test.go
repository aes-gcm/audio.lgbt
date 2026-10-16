package sfu

import (
	"errors"
	"strings"
	"testing"
)

func TestGuestCannotClaimReservedRestrictedRoom(t *testing.T) {
	h := NewHub(HubConfig{RequireAuth: true})
	r, err := h.joinAuthorized("reserved", "", true)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = h.joinRoom("reserved", ""); !errors.Is(err, errBadToken) {
		t.Fatal("guest entered before organizer", err)
	}
	if _, err = r.add(newParticipant(h, r, "guest", "Guest")); !errors.Is(err, errBadToken) {
		t.Fatal("guest claimed host role", err)
	}
	p := newParticipant(h, r, "owner", "Owner")
	p.canOpenRoom = true
	if _, err = r.add(p); err != nil {
		t.Fatal(err)
	}
	guest := newParticipant(h, r, "guest", "Guest")
	if _, err = r.add(guest); err != nil {
		t.Fatal("guest rejected after organizer joined", err)
	}
	if !r.isHost(p.id) {
		t.Fatal("organizer not host")
	}
}

func TestGuestCannotModerateOtherParticipants(t *testing.T) {
	h := NewHub(HubConfig{})
	r, _ := h.joinRoom("moderation", "")
	host := newParticipant(h, r, "host", "Host")
	guest := newParticipant(h, r, "guest", "Guest")
	r.add(host)
	r.add(guest)
	locked := true
	for _, m := range []Msg{{Type: "lock", Locked: &locked}, {Type: "remove", PeerID: host.id}, {Type: "end"}, {Type: "mute-all"}} {
		guest.onModeration(m)
	}
	if r.isLocked() || len(r.allParticipants()) != 2 {
		t.Fatal("guest obtained moderation rights")
	}
}

func TestPublishRejectsUnboundedOrUnlabelledMedia(t *testing.T) {
	base := "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
	media := func(mid string) string {
		return "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:" + mid + "\r\na=sendonly\r\n"
	}
	valid := Msg{SDP: base + media("0"), Tracks: []TrackInfo{{Mid: "0", Source: "mic"}}}
	if _, err := validatePublish(valid); err != nil {
		t.Fatal(err)
	}
	for _, bad := range []Msg{
		{SDP: valid.SDP},
		{SDP: valid.SDP, Tracks: []TrackInfo{{Mid: "0", Source: "screen"}}},
		{SDP: base + strings.Repeat(media("0"), 65), Tracks: valid.Tracks},
		{SDP: base + "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=mid:0\r\n"},
		{SDP: valid.SDP + strings.Repeat("a=candidate:x\r\n", 129), Tracks: valid.Tracks},
		{SDP: valid.SDP, Tracks: append(valid.Tracks, valid.Tracks...)},
	} {
		if _, err := validatePublish(bad); err == nil {
			t.Fatal("unsafe SDP accepted")
		}
	}
}

func TestRoomIdentifiersAreBounded(t *testing.T) {
	h := NewHub(HubConfig{})
	for _, id := range []string{"", strings.Repeat("x", 129), "room\nforged-log", "<script>"} {
		if _, err := h.joinRoom(id, ""); err == nil {
			t.Fatal("invalid room accepted")
		}
	}
}
