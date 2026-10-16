package server

import (
	"net"
	"net/http"
	"net/netip"
	"sync"
	"time"

	"github.com/aes-gcm/audio.lgbt/internal/observe"
	"golang.org/x/time/rate"
)

const maxSources = 8192

type sourceBudget struct {
	tcp, http, joins      *rate.Limiter
	seen                  time.Time
	connections, sessions int
}
type guard struct {
	mu                    sync.Mutex
	sources               map[string]*sourceBudget
	cleaned               time.Time
	tcp, http, joins      *rate.Limiter
	connections, sessions int
	meter                 *observe.Meter
}

func newGuard(m *observe.Meter) *guard {
	return &guard{sources: make(map[string]*sourceBudget), tcp: rate.NewLimiter(100, 200), http: rate.NewLimiter(1000, 2000), joins: rate.NewLimiter(20, 40), meter: m}
}
func sourceKey(address string) string {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return "invalid"
	}
	ip, err := netip.ParseAddr(host)
	if err != nil {
		return "invalid"
	}
	ip = ip.Unmap()
	if ip.Is6() {
		return netip.PrefixFrom(ip, 64).Masked().String()
	}
	return ip.String()
}
func (g *guard) source(key string, now time.Time) *sourceBudget {
	if now.Sub(g.cleaned) >= time.Minute {
		for k, s := range g.sources {
			if s.connections == 0 && s.sessions == 0 && now.Sub(s.seen) > 2*time.Minute {
				delete(g.sources, k)
			}
		}
		g.cleaned = now
	}
	s := g.sources[key]
	if s == nil {
		if len(g.sources) >= maxSources {
			g.meter.Add("source_table_full", 1)
			return nil
		}
		s = &sourceBudget{tcp: rate.NewLimiter(30, 60), http: rate.NewLimiter(60, 180), joins: rate.NewLimiter(2, 12)}
		g.sources[key] = s
	}
	s.seen = now
	return s
}
func (g *guard) connection(address string) (func(), bool) {
	now := time.Now()
	g.mu.Lock()
	defer g.mu.Unlock()
	s := g.source(sourceKey(address), now)
	if s == nil || s.connections >= 128 || g.connections >= 2048 || !s.tcp.AllowN(now, 1) || !g.tcp.AllowN(now, 1) {
		g.meter.Add("tcp_rejected", 1)
		return nil, false
	}
	s.connections++
	g.connections++
	g.meter.Add("tcp_accepted", 1)
	g.meter.Gauge("tcp_active", 1)
	return func() { g.mu.Lock(); s.connections--; g.connections--; g.mu.Unlock(); g.meter.Gauge("tcp_active", -1) }, true
}

type guardedConn struct {
	net.Conn
	once    sync.Once
	release func()
}

func (c *guardedConn) Close() error { err := c.Conn.Close(); c.once.Do(c.release); return err }

type guardedListener struct {
	net.Listener
	guard *guard
}

func (l *guardedListener) Accept() (net.Conn, error) {
	for {
		c, err := l.Listener.Accept()
		if err != nil {
			return nil, err
		}
		release, ok := l.guard.connection(c.RemoteAddr().String())
		if !ok {
			_ = c.Close()
			continue
		}
		return &guardedConn{Conn: c, release: release}, nil
	}
}
func (g *guard) HTTP(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		now := time.Now()
		g.mu.Lock()
		s := g.source(sourceKey(r.RemoteAddr), now)
		allowed := s != nil && s.http.AllowN(now, 1) && g.http.AllowN(now, 1)
		session := allowed && r.URL.Path == "/ws"
		if session {
			allowed = s.sessions < 64 && g.sessions < 256 && s.joins.AllowN(now, 1) && g.joins.AllowN(now, 1)
			if allowed {
				s.sessions++
				g.sessions++
				g.meter.Gauge("ws_handlers", 1)
			}
		}
		g.mu.Unlock()
		if !allowed {
			reason := "http_rate_rejected"
			if r.URL.Path == "/ws" {
				reason = "ws_admission_rejected"
			}
			g.meter.Add(reason, 1)
			w.Header().Set("Retry-After", "5")
			w.Header().Set("Cache-Control", "no-store")
			http.Error(w, "Слишком много подключений. Попробуйте через несколько секунд.", http.StatusTooManyRequests)
			return
		}
		if session {
			defer func() { g.mu.Lock(); s.sessions--; g.sessions--; g.mu.Unlock(); g.meter.Gauge("ws_handlers", -1) }()
		}
		next.ServeHTTP(w, r)
	})
}
