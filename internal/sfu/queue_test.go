package sfu

import (
	"testing"
	"time"
)

func TestFullMailboxClosesWithoutDeadlock(t *testing.T) {
	h := NewHub(HubConfig{})
	r, _ := h.joinRoom("overload", "")
	p := newParticipant(h, r, "peer", "Guest")
	if _, err := r.add(p); err != nil {
		t.Fatal(err)
	}
	for range mailboxLimit {
		p.post(func() { t.Error("discarded backlog executed") })
	}
	returned := make(chan struct{})
	go func() { p.post(func() {}); close(returned) }()
	select {
	case <-returned:
	case <-time.After(time.Second):
		t.Fatal("overflow deadlocked")
	}
	go p.run()
	select {
	case <-p.done:
	case <-time.After(3 * time.Second):
		t.Fatal("cleanup stalled")
	}
	p.post(func() { t.Error("post-close work executed") })
	p.Close()
	if len(r.others("")) != 0 {
		t.Fatal("participant retained")
	}
}
