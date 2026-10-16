package turnx

import (
	"net"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/logging"
	"github.com/pion/turn/v5"
)

type Deps struct {
	Listener net.Listener
	PublicIP net.IP
	BindIP   string
	SFUIPs   []net.IP
	SFUPort  int
	Secret   string
	Realm    string
	MinPort  uint16
	MaxPort  uint16
	Log      logging.LoggerFactory
}

type Server struct {
	srv *turn.Server
	gen *relayGen
}

func RESTCredentials(secret, participant string, ttl time.Duration) (user, pass string, err error) {
	return turn.GenerateLongTermTURNRESTCredentials(secret, participant, ttl)
}

func New(d Deps) (*Server, error) {
	if d.MinPort == 0 {
		d.MinPort = 49160
	}
	if d.MaxPort == 0 {
		d.MaxPort = 49260
	}
	isSFU := func(ip net.IP) bool {
		for _, s := range d.SFUIPs {
			if s.Equal(ip) {
				return true
			}
		}
		return false
	}
	gen := &relayGen{
		inner: &turn.RelayAddressGeneratorPortRange{
			RelayAddress: d.PublicIP, Address: d.BindIP, MinPort: d.MinPort, MaxPort: d.MaxPort,
		},
		allowed: func(a *net.UDPAddr) bool { return isSFU(a.IP) && (d.SFUPort == 0 || a.Port == d.SFUPort) },
	}
	log := d.Log.NewLogger("turn")
	srv, err := turn.NewServer(turn.ServerConfig{
		Realm:              d.Realm,
		LoggerFactory:      d.Log,
		AuthHandler:        turn.LongTermTURNRESTAuthHandler(d.Secret, log),
		AllocationLifetime: 10 * time.Minute,
		ListenerConfigs: []turn.ListenerConfig{{
			Listener:              d.Listener,
			RelayAddressGenerator: gen,
			PermissionHandler:     func(_ net.Addr, peer net.IP) bool { return isSFU(peer) },
		}},
	})
	if err != nil {
		return nil, err
	}
	return &Server{srv: srv, gen: gen}, nil
}

func (s *Server) Close() error { return s.srv.Close() }

type relayGen struct {
	inner   *turn.RelayAddressGeneratorPortRange
	allowed func(*net.UDPAddr) bool
}

func (g *relayGen) Validate() error { return g.inner.Validate() }

func (g *relayGen) AllocatePacketConn(conf turn.AllocateListenerConfig) (net.PacketConn, net.Addr, error) {
	conn, addr, err := g.inner.AllocatePacketConn(conf)
	if err != nil {
		return nil, nil, err
	}
	return &guardedPacketConn{PacketConn: conn, allowed: g.allowed, bytes: new(atomic.Int64)}, addr, nil
}

func (g *relayGen) AllocateListener(turn.AllocateListenerConfig) (net.Listener, net.Addr, error) {
	return nil, nil, errNoTCPRelay
}
func (g *relayGen) AllocateConn(turn.AllocateConnConfig) (net.Conn, error) { return nil, errNoTCPRelay }

type errStr string

func (e errStr) Error() string { return string(e) }

const errNoTCPRelay = errStr("tcp relay disabled")

type guardedPacketConn struct {
	net.PacketConn
	allowed func(*net.UDPAddr) bool
	bytes   *atomic.Int64
	mu      sync.Mutex
}

func (c *guardedPacketConn) WriteTo(p []byte, addr net.Addr) (int, error) {
	ua, ok := addr.(*net.UDPAddr)
	if !ok || !c.allowed(ua) {
		return len(p), nil
	}
	c.bytes.Add(int64(len(p)))
	return c.PacketConn.WriteTo(p, addr)
}

func (c *guardedPacketConn) ReadFrom(p []byte) (int, net.Addr, error) {
	for {
		n, addr, err := c.PacketConn.ReadFrom(p)
		if err != nil {
			return n, addr, err
		}
		if ua, ok := addr.(*net.UDPAddr); ok && c.allowed(ua) {
			c.bytes.Add(int64(n))
			return n, addr, nil
		}
	}
}
