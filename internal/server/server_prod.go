package server

import (
	"context"
	"fmt"
	"net"
	"net/http"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/webrtc/v4"

	"github.com/aes-gcm/audio.lgbt/internal/sfu"
	"github.com/aes-gcm/audio.lgbt/internal/turnx"
)

func (s *Server) runProd(ctx context.Context) error {
	log := s.log.NewLogger("server")

	tlsConf, err := buildTLSConfig(s.cfg)
	if err != nil {
		return err
	}

	real, err := net.Listen("tcp", s.cfg.TLSAddr)
	if err != nil {
		return fmt.Errorf("listen %s: %w", s.cfg.TLSAddr, err)
	}
	defer real.Close()
	addr := real.Addr()

	iceLn := newChanListener(addr)
	turnLn := newChanListener(addr)
	httpLn := newChanListener(addr)

	var tcpMux ice.TCPMux
	if s.cfg.ICETCP {
		m := webrtc.NewICETCPMux(s.log.NewLogger("tcpmux"), iceLn, 64)
		defer m.Close()
		tcpMux = m
	}

	sfuIPs := parseIPs(s.cfg.PublicIPs)
	turnEnabled := len(sfuIPs) > 0
	turnSecret := randomSecret()
	turnHost := s.cfg.Domain
	if turnHost == "" && len(s.cfg.PublicIPs) > 0 {
		turnHost = s.cfg.PublicIPs[0]
	}
	if turnHost == "" {
		turnHost = hostOf(s.cfg.TLSAddr)
	}
	turnPort := portOf(s.cfg.TLSAddr)
	if turnEnabled && net.ParseIP(turnHost) != nil && !s.cfg.SelfSigned {
		log.Warnf("TURN-TLS advertised to an IP (%s); the certificate must cover it, else the fallback silently fails — prefer -domain", turnHost)
	}

	iceServers := func(participantID string) []sfu.ICEServer {
		if !turnEnabled {
			return nil
		}
		user, pass, err := turnx.RESTCredentials(turnSecret, participantID, time.Hour)
		if err != nil {
			return nil
		}
		return []sfu.ICEServer{{
			URLs:       []string{fmt.Sprintf("turns:%s:%s?transport=tcp", turnHost, turnPort)},
			Username:   user,
			Credential: pass,
		}}
	}

	if err := s.buildHub(sfu.ServerNet{UDPMux: s.udpMux, TCPMux: tcpMux}, iceServers); err != nil {
		return err
	}

	if turnEnabled {
		relay, err := turnx.New(turnx.Deps{
			Listener: turnLn,
			PublicIP: sfuIPs[0],
			BindIP:   "0.0.0.0",
			SFUIPs:   sfuIPs,
			SFUPort:  s.cfg.UDPPort,
			Secret:   turnSecret,
			Realm:    firstNonEmpty(s.cfg.Domain, "audio.lgbt"),
			Log:      s.log,
		})
		if err != nil {
			return fmt.Errorf("turn: %w", err)
		}
		defer relay.Close()
	} else {

		go func() {
			for {
				c, err := turnLn.Accept()
				if err != nil {
					return
				}
				_ = c.Close()
			}
		}()
		log.Warnf("TURN-over-TLS fallback disabled: set -public-ip to enable it")
	}

	httpSrv := &http.Server{Handler: s.Handler(), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 20 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 16 << 10}
	go func() { _ = httpSrv.Serve(httpLn) }()

	demux := &Demuxer{TLSConfig: tlsConf, ICELn: iceLn, TURNLn: turnLn, HTTPLn: httpLn, AllowICETCP: s.cfg.ICETCP, Log: s.log.NewLogger("demux"), Meter: s.meter}

	log.Infof("audio.lgbt on https://%s (UDP media :%d, TURN-TLS=%v)", addr, s.cfg.UDPPort, turnEnabled)

	go func() {
		<-ctx.Done()
		_ = real.Close()
		iceLn.Close()
		turnLn.Close()
		httpLn.Close()
		shutdown(httpSrv)
	}()

	if err := demux.Serve(&guardedListener{Listener: real, guard: s.guard}); err != nil {
		select {
		case <-ctx.Done():
			return nil
		default:
			return err
		}
	}
	return nil
}
