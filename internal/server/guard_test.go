package server

import (
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/aes-gcm/audio.lgbt/internal/observe"
	"golang.org/x/time/rate"
)

func TestHTTPAbuseIsIsolatedAndRecovers(t *testing.T) {
	g := newGuard(observe.New())
	h := g.HTTP(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(204) }))
	request := func(ip string) *httptest.ResponseRecorder {
		r := httptest.NewRequest("GET", "/", nil)
		r.RemoteAddr = ip
		r.Header.Set("X-Forwarded-For", fmt.Sprint(time.Now().UnixNano()))
		w := httptest.NewRecorder()
		h.ServeHTTP(w, r)
		return w
	}
	request("192.0.2.1:1000")
	g.sources["192.0.2.1"].http = rate.NewLimiter(0, 2)
	for range 2 {
		if request("192.0.2.1:1000").Code != 204 {
			t.Fatal("burst rejected")
		}
	}
	w := request("192.0.2.1:1000")
	if w.Code != 429 || w.Header().Get("Retry-After") == "" {
		t.Fatal("abuse was not throttled")
	}
	if request("192.0.2.2:1000").Code != 204 {
		t.Fatal("unrelated source rejected")
	}
	g.sources["192.0.2.1"].http = rate.NewLimiter(100, 1)
	if request("192.0.2.1:1000").Code != 204 {
		t.Fatal("recovery failed")
	}
}

func TestConnectionCapacityAndClose(t *testing.T) {
	g := newGuard(observe.New())
	g.tcp = rate.NewLimiter(rate.Inf, 1)
	s := g.source("192.0.2.1", time.Now())
	s.tcp = rate.NewLimiter(rate.Inf, 1)
	var releases []func()
	for range 128 {
		release, ok := g.connection("192.0.2.1:80")
		if !ok {
			t.Fatal("capacity too small")
		}
		releases = append(releases, release)
	}
	if _, ok := g.connection("192.0.2.1:80"); ok {
		t.Fatal("source capacity exceeded")
	}
	a, b := net.Pipe()
	defer b.Close()
	c := &guardedConn{Conn: a, release: releases[0]}
	c.Close()
	c.Close()
	if g.connections != 127 {
		t.Fatal("close must release exactly once")
	}
	release, ok := g.connection("192.0.2.1:80")
	if !ok {
		t.Fatal("slot not released")
	}
	release()
	for _, release := range releases[1:] {
		release()
	}
	if g.connections != 0 {
		t.Fatal("connection leak")
	}
}

func TestSourceTableBoundedAndIPv6Grouped(t *testing.T) {
	if sourceKey("[2001:db8::1]:1") != sourceKey("[2001:db8::ffff]:2") {
		t.Fatal("IPv6 rotation bypass")
	}
	if sourceKey("[::ffff:192.0.2.1]:1") != sourceKey("192.0.2.1:2") {
		t.Fatal("mapped IPv4 bypass")
	}
	g := newGuard(nil)
	now := time.Now()
	for i := 0; i < maxSources; i++ {
		if g.source(fmt.Sprint(i), now) == nil {
			t.Fatal("early full")
		}
	}
	if g.source("extra", now) != nil {
		t.Fatal("unbounded table")
	}
	if g.source("recovered", now.Add(3*time.Minute)) == nil || len(g.sources) != 1 {
		t.Fatal("expired sources retained")
	}
}

func TestConcurrentWebsocketAdmissionReleased(t *testing.T) {
	g := newGuard(nil)
	now := time.Now()
	s := g.source("192.0.2.1", now)
	s.joins = rate.NewLimiter(rate.Inf, 1)
	g.joins = rate.NewLimiter(rate.Inf, 1)
	entered, finish := make(chan struct{}, 64), make(chan struct{})
	h := g.HTTP(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { entered <- struct{}{}; <-finish; w.WriteHeader(204) }))
	request := func() int {
		r := httptest.NewRequest("GET", "/ws", nil)
		r.RemoteAddr = "192.0.2.1:1"
		w := httptest.NewRecorder()
		h.ServeHTTP(w, r)
		return w.Code
	}
	var wg sync.WaitGroup
	for range 64 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if request() != 204 {
				t.Error("admission failed")
			}
		}()
	}
	for range 64 {
		<-entered
	}
	if request() != 429 {
		t.Fatal("WS capacity exceeded")
	}
	close(finish)
	wg.Wait()
	if g.sessions != 0 || s.sessions != 0 {
		t.Fatal("WS slots leaked")
	}
}

func TestDemuxQueueRejectsWithoutBlocking(t *testing.T) {
	l := newChanListener(&net.TCPAddr{})
	var peers []net.Conn
	for range 128 {
		a, b := net.Pipe()
		peers = append(peers, b)
		if !l.push(a) {
			t.Fatal("early full")
		}
	}
	a, b := net.Pipe()
	defer b.Close()
	if l.push(a) {
		t.Fatal("unbounded demux queue")
	}
	b.SetReadDeadline(time.Now().Add(time.Second))
	if _, err := b.Read(make([]byte, 1)); err == nil {
		t.Fatal("rejected connection open")
	}
	for _, p := range peers {
		p.Close()
		c, _ := l.Accept()
		c.Close()
	}
	l.Close()
}
