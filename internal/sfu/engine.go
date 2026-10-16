package sfu

import (
	"context"
	"net"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/interceptor"
	"github.com/pion/interceptor/pkg/nack"
	"github.com/pion/interceptor/pkg/report"
	"github.com/pion/interceptor/pkg/twcc"
	"github.com/pion/logging"
	"github.com/pion/sdp/v3"
	"github.com/pion/webrtc/v4"
)

type ServerNet struct {
	UDPMux ice.UDPMux
	TCPMux ice.TCPMux
}

type EngineConfig struct {
	Net       ServerNet
	PublicIPs []string
	Lite      bool
	Loopback  bool
	Log       logging.LoggerFactory
}

type Engine struct {
	api *webrtc.API
	log logging.LoggerFactory
}

func NewEngine(cfg EngineConfig) (*Engine, error) {
	if cfg.Log == nil {
		cfg.Log = logging.NewDefaultLoggerFactory()
	}
	m, err := newMediaEngine()
	if err != nil {
		return nil, err
	}
	ir := &interceptor.Registry{}

	if err := webrtc.RegisterDefaultInterceptorsWithOptions(m, ir,
		webrtc.WithNackResponderOptions(nack.ResponderSize(2048)),
		webrtc.WithNackGeneratorOptions(nack.GeneratorMaxNacksPerPacket(5), nack.GeneratorSize(2048)),
		webrtc.WithReportReceiverOptions(report.ReceiverInterval(time.Second)),
		webrtc.WithReportSenderOptions(report.SenderInterval(time.Second)),
		webrtc.WithTWCCOptions(twcc.SendInterval(100*time.Millisecond)),
	); err != nil {
		return nil, err
	}

	if err := registerHeaderExtensions(m); err != nil {
		return nil, err
	}

	se, err := newSettingEngine(cfg)
	if err != nil {
		return nil, err
	}
	api := webrtc.NewAPI(
		webrtc.WithMediaEngine(m),
		webrtc.WithInterceptorRegistry(ir),
		webrtc.WithSettingEngine(se),
	)
	return &Engine{api: api, log: cfg.Log}, nil
}

func (e *Engine) NewPeerConnection() (*webrtc.PeerConnection, error) {
	return e.api.NewPeerConnection(webrtc.Configuration{
		BundlePolicy:  webrtc.BundlePolicyMaxBundle,
		RTCPMuxPolicy: webrtc.RTCPMuxPolicyRequire,
	})
}

func newMediaEngine() (*webrtc.MediaEngine, error) {
	m := &webrtc.MediaEngine{}

	if err := m.RegisterCodec(webrtc.RTPCodecParameters{
		RTPCodecCapability: webrtc.RTPCodecCapability{
			MimeType:     webrtc.MimeTypeOpus,
			ClockRate:    48000,
			Channels:     2,
			SDPFmtpLine:  "minptime=10;useinbandfec=1",
			RTCPFeedback: []webrtc.RTCPFeedback{{Type: webrtc.TypeRTCPFBTransportCC}},
		},
		PayloadType: 111,
	}, webrtc.RTPCodecTypeAudio); err != nil {
		return nil, err
	}

	videoFB := []webrtc.RTCPFeedback{
		{Type: webrtc.TypeRTCPFBGoogREMB},
		{Type: webrtc.TypeRTCPFBTransportCC},
		{Type: webrtc.TypeRTCPFBCCM, Parameter: "fir"},
		{Type: webrtc.TypeRTCPFBNACK},
		{Type: webrtc.TypeRTCPFBNACK, Parameter: "pli"},
	}
	for _, c := range []webrtc.RTPCodecParameters{
		{RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeVP8, ClockRate: 90000, RTCPFeedback: videoFB}, PayloadType: 96},
		{RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeRTX, ClockRate: 90000, SDPFmtpLine: "apt=96"}, PayloadType: 97},
	} {
		if err := m.RegisterCodec(c, webrtc.RTPCodecTypeVideo); err != nil {
			return nil, err
		}
	}
	return m, nil
}

func registerHeaderExtensions(m *webrtc.MediaEngine) error {
	recvOnly := []webrtc.RTPTransceiverDirection{webrtc.RTPTransceiverDirectionRecvonly}
	exts := []struct {
		uri  string
		kind webrtc.RTPCodecType
		dirs []webrtc.RTPTransceiverDirection
	}{
		{sdp.SDESMidURI, webrtc.RTPCodecTypeAudio, nil},
		{sdp.SDESMidURI, webrtc.RTPCodecTypeVideo, nil},
		{sdp.AudioLevelURI, webrtc.RTPCodecTypeAudio, nil},
		{sdp.TransportCCURI, webrtc.RTPCodecTypeAudio, nil},
		{sdp.TransportCCURI, webrtc.RTPCodecTypeVideo, nil},

		{sdp.SDESRTPStreamIDURI, webrtc.RTPCodecTypeVideo, recvOnly},
		{sdp.SDESRepairRTPStreamIDURI, webrtc.RTPCodecTypeVideo, recvOnly},
	}
	for _, e := range exts {
		if err := m.RegisterHeaderExtension(webrtc.RTPHeaderExtensionCapability{URI: e.uri}, e.kind, e.dirs...); err != nil {
			return err
		}
	}
	return nil
}

func newSettingEngine(cfg EngineConfig) (webrtc.SettingEngine, error) {
	var se webrtc.SettingEngine
	se.LoggerFactory = cfg.Log

	se.SetICEUDPMux(cfg.Net.UDPMux)
	netTypes := []webrtc.NetworkType{webrtc.NetworkTypeUDP4, webrtc.NetworkTypeUDP6}
	if cfg.Net.TCPMux != nil {
		se.SetICETCPMux(cfg.Net.TCPMux)
		netTypes = append(netTypes, webrtc.NetworkTypeTCP4, webrtc.NetworkTypeTCP6)
	}
	se.SetNetworkTypes(netTypes)

	se.DisableActiveTCP(true)

	se.SetICEMulticastDNSMode(ice.MulticastDNSModeDisabled)
	if cfg.Loopback {
		se.SetIncludeLoopbackCandidate(true)
	}
	se.SetLite(cfg.Lite)

	se.SetICETimeouts(10*time.Second, 20*time.Second, 2*time.Second)

	if len(cfg.PublicIPs) > 0 {
		if err := se.SetICEAddressRewriteRules(webrtc.ICEAddressRewriteRule{
			External:        cfg.PublicIPs,
			AsCandidateType: webrtc.ICECandidateTypeHost,
			Mode:            webrtc.ICEAddressRewriteReplace,
		}); err != nil {
			return se, err
		}
	}

	if err := se.SetAnsweringDTLSRole(webrtc.DTLSRoleServer); err != nil {
		return se, err
	}
	se.SetDTLSInsecureSkipHelloVerify(true)
	se.SetDTLSConnectContextMaker(func() (context.Context, func()) {
		return context.WithTimeout(context.Background(), 15*time.Second)
	})

	se.SetRemoteIPFilter(func(ip net.IP) bool {
		return !(ip.IsUnspecified() || ip.IsMulticast() || ip.IsLinkLocalUnicast())
	})
	se.SetSRTPReplayProtectionWindow(1024)
	se.SetReceiveMTU(1600)
	return se, nil
}
