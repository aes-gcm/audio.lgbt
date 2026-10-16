package sfu

import (
	"errors"
	"strings"
	"sync"
	"testing"
	"unicode/utf8"
)

func TestRestrictedCreationAllowsInvitedGuests(t *testing.T) {
	h := NewHub(HubConfig{RequireAuth: true, RoomSecret: "legacy-secret"})
	if _, err := h.joinRoom("private", "legacy-secret"); !errors.Is(err, errBadToken) {
		t.Fatal("legacy secret bypassed account authentication")
	}
	room, err := h.joinAuthorized("private", "", true)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = room.add(newParticipant(h, room, "admin", "Admin")); err != nil {
		t.Fatal(err)
	}
	guest, err := h.joinRoom("private", "")
	if err != nil || guest != room {
		t.Fatal("invited guest rejected")
	}
	room.remove("admin")
	if _, err = h.joinRoom("private", ""); !errors.Is(err, errBadToken) {
		t.Fatal("guest recreated an empty room")
	}
}

func TestRoomAdmissionRechecksLock(t *testing.T) {
	h := NewHub(HubConfig{MaxRoom: 2})
	r, _ := h.joinRoom("room", "")
	r.setLocked(true)
	if _, err := r.add(newParticipant(h, r, "late", "Гость")); !errors.Is(err, errRoomLocked) {
		t.Fatalf("late admission: %v", err)
	}
}

func TestDetachedRoomCannotAdmit(t *testing.T) {
	h := NewHub(HubConfig{MaxRoom: 2})
	r, _ := h.joinRoom("room", "")
	r.remove("departed")
	replacement, _ := h.joinRoom("room", "")
	if _, err := r.add(newParticipant(h, r, "late", "Гость")); !errors.Is(err, errNoSuchKey) {
		t.Fatalf("detached admission: %v", err)
	}
	if _, err := replacement.add(newParticipant(h, replacement, "new", "Гость")); err != nil {
		t.Fatal(err)
	}
}

func TestConcurrentAdmissionRespectsCapacity(t *testing.T) {
	h := NewHub(HubConfig{MaxRoom: 3})
	r, _ := h.joinRoom("room", "")
	var wg sync.WaitGroup
	for i := range 20 {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			id := string(rune('a' + i))
			_, _ = r.add(newParticipant(h, r, id, "Гость"))
		}(i)
	}
	wg.Wait()
	if got := len(r.allParticipants()); got != 3 {
		t.Fatalf("admitted %d, want 3", got)
	}
}

func TestNameClipPreservesUnicode(t *testing.T) {
	got := clip("  "+strings.Repeat("Я🙂", 40)+"  ", 64)
	if !utf8.ValidString(got) || len([]rune(got)) != 64 {
		t.Fatalf("invalid name: %q", got)
	}
}
