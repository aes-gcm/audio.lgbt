package observe

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"sync"
	"time"
)

type Meter struct {
	mu     sync.Mutex
	counts map[string]int64
	gauges map[string]int64
}

func New() *Meter { return &Meter{counts: make(map[string]int64), gauges: make(map[string]int64)} }
func (m *Meter) Add(key string, n int64) {
	if m == nil {
		return
	}
	m.mu.Lock()
	m.counts[key] += n
	m.mu.Unlock()
}
func (m *Meter) Gauge(key string, n int64) {
	if m == nil {
		return
	}
	m.mu.Lock()
	m.gauges[key] += n
	m.mu.Unlock()
}
func (m *Meter) Snapshot() (map[string]int64, map[string]int64) {
	m.mu.Lock()
	defer m.mu.Unlock()
	counts := m.counts
	m.counts = make(map[string]int64)
	gauges := make(map[string]int64, len(m.gauges))
	for k, v := range m.gauges {
		gauges[k] = v
	}
	return counts, gauges
}
func (m *Meter) Run(ctx context.Context, output io.Writer, interval time.Duration) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	previous := time.Now()
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-ticker.C:
			counts, gauges := m.Snapshot()
			_ = json.NewEncoder(output).Encode(map[string]any{"event": "traffic_summary", "time": now.UTC().Format(time.RFC3339), "interval_seconds": now.Sub(previous).Seconds(), "counts": counts, "gauges": gauges})
			previous = now
		}
	}
}
func route(path string) string {
	switch path {
	case "/ws":
		return "ws"
	case "/api/login":
		return "login"
	case "/api/logout", "/api/server":
		return "api"
	case "/healthz":
		return "health"
	case "/", "/index.html", "/download":
		return "page"
	}
	if strings.HasPrefix(path, "/downloads/") {
		return "downloads"
	}
	return "asset_or_unknown"
}

type response struct {
	http.ResponseWriter
	meter  *Meter
	status int
	start  time.Time
}

func (w *response) Unwrap() http.ResponseWriter { return w.ResponseWriter }
func (w *response) WriteHeader(status int) {
	if w.status == 0 && (status >= 200 || status == 101) {
		w.status = status
		group := "other"
		if status >= 100 && status < 600 {
			group = string(rune('0'+status/100)) + "xx"
		}
		w.meter.Add("http_"+group, 1)
		if status == 101 {
			w.meter.Add("ws_upgraded", 1)
		}
		if status == 429 {
			w.meter.Add("http_429", 1)
		}
		if status == 401 || status == 403 {
			w.meter.Add("http_auth_rejected", 1)
		}
		if time.Since(w.start) > time.Second {
			w.meter.Add("http_slow_headers", 1)
		}
	}
	w.ResponseWriter.WriteHeader(status)
}
func (w *response) Write(p []byte) (int, error) {
	if w.status == 0 {
		w.WriteHeader(200)
	}
	n, err := w.ResponseWriter.Write(p)
	w.meter.Add("http_response_bytes", int64(n))
	return n, err
}
func (m *Meter) HTTP(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		m.Add("http_requests", 1)
		m.Add("http_route_"+route(r.URL.Path), 1)
		m.Gauge("http_handlers", 1)
		defer m.Gauge("http_handlers", -1)
		out := &response{ResponseWriter: w, meter: m, start: time.Now()}
		next.ServeHTTP(out, r)
		if out.status == 0 {
			out.WriteHeader(200)
		}
	})
}
