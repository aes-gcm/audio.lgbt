package nativeclient

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/pion/rtcp"
	"github.com/pion/rtp/codecs"
	"github.com/pion/webrtc/v4"
	"github.com/pion/webrtc/v4/pkg/media"
	"github.com/pion/webrtc/v4/pkg/media/samplebuilder"
	"zvonok/internal/sfu"
)

type Command struct {
	AuthToken string `json:"authToken,omitempty"`
	Speaking  bool   `json:"speaking"`
	Type      string `json:"type"`
	Server    string `json:"server"`
	Key       string `json:"key"`
	Name      string `json:"name"`
	Source    string `json:"source"`
	Data      string `json:"data"`
	Duration  int    `json:"duration"`
	Text      string `json:"text"`
	PeerID    string `json:"peerId"`
	Muted     bool   `json:"muted"`
	Relay     bool   `json:"relay"`
	Up        bool   `json:"up"`
	Locked    bool   `json:"locked"`
}
type Frame struct {
	Type      string `json:"type"`
	PeerID    string `json:"peerId"`
	Source    string `json:"source"`
	Data      []byte `json:"data"`
	Timestamp uint32 `json:"timestamp"`
}
type Client struct {
	ctx             context.Context
	cancel          context.CancelFunc
	stop            sync.Once
	options         Command
	crypto          *Crypto
	emit            func(any)
	mu              sync.RWMutex
	ws              *websocket.Conn
	pub, sub        *webrtc.PeerConnection
	tracks          map[string]*webrtc.TrackLocalStaticSample
	infos           map[string]sfu.TrackInfo
	pending         map[string][]webrtc.ICECandidateInit
	iceServers      []webrtc.ICEServer
	sendMu          sync.Mutex
	controls        chan Command
	frames          chan Frame
	transportChange *transportChange
}
type transportChange struct {
	relay, previous, rollback bool
	started                   time.Time
	pairs                     map[string]string
}

func New(parent context.Context, opts Command, emit func(any)) (*Client, error) {
	u, e := url.Parse(opts.Server)
	if e != nil || u.User != nil || u.Path != "/" || u.RawQuery != "" || u.Fragment != "" || (u.Scheme != "https" && !(u.Scheme == "http" && (u.Hostname() == "localhost" || u.Hostname() == "127.0.0.1" || u.Hostname() == "::1"))) {
		return nil, errors.New("Неверный адрес сервера")
	}
	crypt, e := NewCrypto(opts.Key)
	if e != nil {
		return nil, e
	}
	ctx, cancel := context.WithCancel(parent)
	c := &Client{ctx: ctx, cancel: cancel, options: opts, crypto: crypt, emit: emit, tracks: map[string]*webrtc.TrackLocalStaticSample{}, infos: map[string]sfu.TrackInfo{}, pending: map[string][]webrtc.ICECandidateInit{}, controls: make(chan Command, 32), frames: make(chan Frame, 32)}
	for _, src := range []string{"mic", "cam", "screen", "screen-audio"} {
		codec := webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeVP8, ClockRate: 90000}
		if isAudio(src) {
			codec = webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2, SDPFmtpLine: "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1"}
		}
		c.tracks[src], e = webrtc.NewTrackLocalStaticSample(codec, src, "native")
		if e != nil {
			cancel()
			return nil, e
		}
	}
	return c, nil
}
func (c *Client) Close() {
	c.stop.Do(func() {
		c.cancel()
		c.mu.Lock()
		ws, pub, sub := c.ws, c.pub, c.sub
		c.ws = nil
		c.mu.Unlock()
		if ws != nil {
			ws.CloseNow()
		}
		if pub != nil {
			pub.Close()
		}
		if sub != nil {
			sub.Close()
		}
	})
}
func (c *Client) send(v any) error {
	data, e := json.Marshal(v)
	if e != nil {
		return e
	}
	c.sendMu.Lock()
	defer c.sendMu.Unlock()
	c.mu.RLock()
	ws := c.ws
	c.mu.RUnlock()
	if ws == nil {
		return errors.New("Соединение ещё не готово")
	}
	ctx, cancel := context.WithTimeout(c.ctx, 5*time.Second)
	defer cancel()
	return ws.Write(ctx, websocket.MessageText, data)
}
func (c *Client) Command(cmd Command) error {
	if cmd.Type == "media" {
		t := c.tracks[cmd.Source]
		if t == nil {
			return nil
		}
		data, e := base64.StdEncoding.DecodeString(cmd.Data)
		if e != nil || len(data) > 2<<20 {
			return errors.New("Invalid media")
		}
		encrypted := c.crypto.SealMedia(data, !isAudio(cmd.Source))
		if len(encrypted) == 0 {
			return nil
		}
		duration := time.Duration(cmd.Duration) * time.Microsecond
		if duration < time.Millisecond || duration > time.Second {
			return errors.New("Invalid duration")
		}
		return t.WriteSample(media.Sample{Data: encrypted, Duration: duration})
	}
	select {
	case c.controls <- cmd:
		return nil
	case <-c.ctx.Done():
		return c.ctx.Err()
	default:
		return errors.New("Подождите завершения предыдущего действия")
	}
}
func (c *Client) Run() {
	defer c.Close()
	go func() {
		for {
			select {
			case f := <-c.frames:
				c.emit(f)
			case <-c.ctx.Done():
				return
			}
		}
	}()
	u, _ := url.Parse(c.options.Server)
	origin := u.String()
	u.Path = "/ws"
	if u.Scheme == "https" {
		u.Scheme = "wss"
	} else {
		u.Scheme = "ws"
	}
	ctx, cancel := context.WithTimeout(c.ctx, 12*time.Second)
	headers := http.Header{"Origin": []string{strings.TrimSuffix(origin, "/")}}
	if c.options.AuthToken != "" {
		headers.Set("Authorization", "Bearer "+c.options.AuthToken)
	}
	ws, _, e := websocket.Dial(ctx, u.String(), &websocket.DialOptions{HTTPHeader: headers})
	cancel()
	if e != nil {
		c.fail("Не удалось подключиться. Проверьте интернет и попробуйте ещё раз.")
		return
	}
	ws.SetReadLimit(1 << 20)
	c.mu.Lock()
	if c.ctx.Err() != nil {
		c.mu.Unlock()
		ws.CloseNow()
		return
	}
	c.ws = ws
	c.mu.Unlock()
	if e = c.send(sfu.Msg{Type: "join", Room: c.crypto.Room, Name: c.options.Name}); e != nil {
		c.fail(e.Error())
		return
	}
	messages := make(chan sfu.Msg, 32)
	readError := make(chan error, 1)
	go func() {
		for {
			_, raw, e := ws.Read(c.ctx)
			if e != nil {
				readError <- e
				return
			}
			var m sfu.Msg
			if e = json.Unmarshal(raw, &m); e != nil {
				continue
			}
			select {
			case messages <- m:
			case <-c.ctx.Done():
				return
			}
		}
	}()
	timer := time.NewTimer(12 * time.Second)
	defer timer.Stop()
	transportTimer := time.NewTicker(time.Second)
	defer transportTimer.Stop()
	for {
		select {
		case <-c.ctx.Done():
			return
		case <-transportTimer.C:
			c.checkTransport()
		case <-timer.C:
			c.fail("Сервер не ответил. Попробуйте подключиться ещё раз.")
			return
		case e := <-readError:
			if c.ctx.Err() == nil {
				_ = e
				c.fail("Соединение потеряно. Подключитесь ещё раз.")
			}
			return
		case cmd := <-c.controls:
			if e = c.control(cmd); e != nil {
				c.emit(map[string]any{"type": "action-error", "action": cmd.Type, "error": e.Error()})
			}
		case m := <-messages:
			if m.Type == "welcome" {
				timer.Stop()
			}
			if e = c.signal(m); e != nil {
				c.fail("Не удалось установить защищённое соединение: " + e.Error())
				return
			}
		}
	}
}
func (c *Client) fail(message string) {
	if c.ctx.Err() == nil {
		c.emit(map[string]any{"type": "error", "error": message})
	}
}
func (c *Client) config(relay bool) webrtc.Configuration {
	servers := c.iceServers
	policy := webrtc.ICETransportPolicyAll
	if relay {
		policy = webrtc.ICETransportPolicyRelay
		servers = nil
		for _, s := range c.iceServers {
			var urls []string
			for _, u := range s.URLs {
				if strings.HasPrefix(u, "turns:") {
					urls = append(urls, u)
				}
			}
			if len(urls) > 0 {
				s.URLs = urls
				servers = append(servers, s)
			}
		}
	}
	return webrtc.Configuration{ICEServers: servers, ICETransportPolicy: policy}
}
func (c *Client) setup(m sfu.Msg) error {
	for _, s := range m.ICEServers {
		c.iceServers = append(c.iceServers, webrtc.ICEServer{URLs: s.URLs, Username: s.Username, Credential: s.Credential})
	}
	pub, e := webrtc.NewPeerConnection(c.config(c.options.Relay))
	if e != nil {
		return e
	}
	sub, e := webrtc.NewPeerConnection(c.config(c.options.Relay))
	if e != nil {
		pub.Close()
		return e
	}
	c.mu.Lock()
	if c.ctx.Err() != nil {
		c.mu.Unlock()
		pub.Close()
		sub.Close()
		return context.Canceled
	}
	c.pub, c.sub = pub, sub
	c.mu.Unlock()
	for target, pc := range map[string]*webrtc.PeerConnection{"pub": pub, "sub": sub} {
		target := target
		pc.OnICECandidate(func(i *webrtc.ICECandidate) {
			if i != nil {
				_ = c.send(map[string]any{"type": "ice", "target": target, "candidate": i.ToJSON()})
			}
		})
		pc.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
			c.emit(map[string]any{"type": "connection", "target": target, "state": s.String()})
		})
	}
	sub.OnTrack(func(t *webrtc.TrackRemote, r *webrtc.RTPReceiver) { go c.receive(t, r) })
	for _, src := range []string{"mic", "cam", "screen", "screen-audio"} {
		sender, e := pub.AddTrack(c.tracks[src])
		if e != nil {
			return e
		}
		go func(src string) {
			buf := make([]byte, 1500)
			for {
				n, _, e := sender.Read(buf)
				if e != nil {
					return
				}
				packets, _ := rtcp.Unmarshal(buf[:n])
				for _, p := range packets {
					switch p.(type) {
					case *rtcp.PictureLossIndication, *rtcp.FullIntraRequest:
						c.emit(map[string]any{"type": "keyframe", "source": src})
					}
				}
			}
		}(src)
	}
	if e = c.offer(false); e != nil {
		return e
	}
	c.emit(m)
	return nil
}
func isAudio(source string) bool { return source == "mic" || source == "screen-audio" }
func (c *Client) offer(restart bool) error {
	offer, e := c.pub.CreateOffer(&webrtc.OfferOptions{ICERestart: restart})
	if e != nil {
		return e
	}
	if e = c.pub.SetLocalDescription(offer); e != nil {
		return e
	}
	var infos []sfu.TrackInfo
	for _, tr := range c.pub.GetTransceivers() {
		if tr.Sender() != nil && tr.Sender().Track() != nil {
			t := tr.Sender().Track()
			infos = append(infos, sfu.TrackInfo{Mid: tr.Mid(), Source: t.ID(), Kind: t.Kind().String()})
		}
	}
	return c.send(sfu.Msg{Type: "publish-offer", SDP: offer.SDP, Tracks: infos})
}
func (c *Client) signal(m sfu.Msg) error {
	switch m.Type {
	case "welcome":
		return c.setup(m)
	case "publish-answer":
		if c.pub == nil {
			return errors.New("unexpected answer")
		}
		if e := c.pub.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: m.SDP}); e != nil {
			return e
		}
		return c.flush("pub")
	case "subscribe-offer":
		if c.sub == nil {
			return errors.New("unexpected offer")
		}
		c.mu.Lock()
		for _, t := range m.Tracks {
			c.infos[t.Mid] = t
		}
		c.mu.Unlock()
		if e := c.sub.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: m.SDP}); e != nil {
			return e
		}
		if e := c.flush("sub"); e != nil {
			return e
		}
		answer, e := c.sub.CreateAnswer(nil)
		if e != nil {
			return e
		}
		if e = c.sub.SetLocalDescription(answer); e != nil {
			return e
		}
		return c.send(sfu.Msg{Type: "subscribe-answer", SDP: answer.SDP})
	case "ice":
		if m.Candidate == nil {
			return nil
		}
		pc := c.pub
		if m.Target == "sub" {
			pc = c.sub
		}
		i := webrtc.ICECandidateInit{Candidate: m.Candidate.Candidate, SDPMid: m.Candidate.SDPMid, SDPMLineIndex: m.Candidate.SDPMLineIndex, UsernameFragment: m.Candidate.UsernameFragment}
		if pc == nil || pc.RemoteDescription() == nil {
			c.pending[m.Target] = append(c.pending[m.Target], i)
			return nil
		}
		return pc.AddICECandidate(i)
	case "chat":
		data, e := c.crypto.OpenChat(m.Chat)
		if e != nil {
			return nil
		}
		var payload struct {
			Text string `json:"t"`
		}
		if json.Unmarshal(data, &payload) != nil {
			return nil
		}
		c.emit(map[string]any{"type": "chat", "peerId": m.PeerID, "text": payload.Text})
		return nil
	case "error":
		message := map[string]string{"room is full": "В комнате больше нет свободных мест.", "room is locked": "Организатор закрыл вход в комнату.", "server is at capacity": "Сервер занят. Попробуйте позже.", "a valid token is required to create a room": "Организатор ещё не открыл комнату. Для создания комнаты войдите в аккаунт через настройки сервера."}[m.Error]
		if message == "" {
			message = "Сервер отклонил подключение: " + m.Error
		}
		c.fail(message)
		c.cancel()
		return nil
	default:
		c.emit(m)
		if m.Type == "removed" || m.Type == "ended" {
			c.cancel()
		}
	}
	return nil
}
func (c *Client) flush(target string) error {
	pc := c.pub
	if target == "sub" {
		pc = c.sub
	}
	for _, i := range c.pending[target] {
		if e := pc.AddICECandidate(i); e != nil {
			return e
		}
	}
	delete(c.pending, target)
	return nil
}
func (c *Client) control(cmd Command) error {
	switch cmd.Type {
	case "stats":
		c.emitTransportStats()
		return nil
	case "chat":
		if len(cmd.Text) > 8000 {
			return errors.New("Сообщение слишком длинное")
		}
		payload, _ := json.Marshal(map[string]string{"n": c.options.Name, "t": cmd.Text})
		if e := c.send(sfu.Msg{Type: "chat", Chat: c.crypto.SealChat(payload)}); e != nil {
			return e
		}
		c.emit(map[string]any{"type": "chat-sent", "text": cmd.Text})
		return nil
	case "mute":
		return c.send(sfu.Msg{Type: "mute", Source: cmd.Source, Muted: &cmd.Muted})
	case "speaking":
		return c.send(sfu.Msg{Type: "speaking", Speaking: &cmd.Speaking})
	case "hand":
		return c.send(sfu.Msg{Type: "hand", Up: &cmd.Up})
	case "lock":
		return c.send(sfu.Msg{Type: "lock", Locked: &cmd.Locked})
	case "remove", "mute-all", "end":
		return c.send(sfu.Msg{Type: cmd.Type, PeerID: cmd.PeerID})
	case "transport":
		if c.transportChange != nil {
			return errors.New("Дождитесь переключения подключения")
		}
		if c.pub == nil {
			return errors.New("Дождитесь подключения")
		}
		cfg := c.config(cmd.Relay)
		if cmd.Relay && len(cfg.ICEServers) == 0 {
			return errors.New("Сервер не предоставил TLS-подключение")
		}
		change := &transportChange{relay: cmd.Relay, previous: c.options.Relay, started: time.Now(), pairs: c.pairIDs()}
		if e := c.restartTransport(cmd.Relay); e != nil {
			_ = c.restartTransport(c.options.Relay)
			return e
		}
		c.transportChange = change
		return nil
	}
	return nil
}

func (c *Client) restartTransport(relay bool) error {
	cfg := c.config(relay)
	if e := c.pub.SetConfiguration(cfg); e != nil {
		return e
	}
	if e := c.sub.SetConfiguration(cfg); e != nil {
		return e
	}
	if e := c.offer(true); e != nil {
		return e
	}
	return c.send(sfu.Msg{Type: "restart-sub-ice"})
}
func (c *Client) pairIDs() map[string]string {
	result := map[string]string{}
	for target, entry := range c.transportStats() {
		result[target], _ = entry["pairId"].(string)
	}
	return result
}
func (c *Client) checkTransport() {
	change := c.transportChange
	if change == nil {
		return
	}
	stats := c.transportStats()
	ready := len(stats) == 2
	for target, entry := range stats {
		if entry["state"] != "connected" || entry["pairId"] == change.pairs[target] {
			ready = false
		}
		if change.relay && (entry["candidateType"] != "relay" || entry["relayProtocol"] != "tls") {
			ready = false
		}
	}
	if ready {
		c.options.Relay = change.relay
		c.transportChange = nil
		c.emit(map[string]any{"type": "transport-complete", "relay": change.relay, "rollback": change.rollback})
		return
	}
	if time.Since(change.started) < 20*time.Second {
		return
	}
	if !change.rollback {
		change.rollback = true
		change.relay = change.previous
		change.pairs = c.pairIDs()
		change.started = time.Now()
		if e := c.restartTransport(change.previous); e == nil {
			c.emit(map[string]any{"type": "transport-rollback"})
			return
		}
	}
	c.transportChange = nil
	c.emit(map[string]any{"type": "action-error", "action": "transport", "error": "Не удалось восстановить подключение. Подключитесь к комнате снова."})
}

func (c *Client) emitTransportStats() {
	c.emit(map[string]any{"type": "stats", "connections": c.transportStats()})
}
func (c *Client) transportStats() map[string]map[string]any {
	connections := map[string]map[string]any{}
	for target, pc := range map[string]*webrtc.PeerConnection{"pub": c.pub, "sub": c.sub} {
		if pc == nil {
			continue
		}
		var dtls *webrtc.DTLSTransport
		for _, sender := range pc.GetSenders() {
			if sender.Transport() != nil {
				dtls = sender.Transport()
				break
			}
		}
		if dtls == nil {
			for _, receiver := range pc.GetReceivers() {
				if receiver.Transport() != nil {
					dtls = receiver.Transport()
					break
				}
			}
		}
		if dtls == nil {
			continue
		}
		selected, err := dtls.ICETransport().GetSelectedCandidatePair()
		if err != nil || selected == nil {
			continue
		}
		entry := map[string]any{"state": pc.ConnectionState().String(), "candidateType": selected.Local.Typ.String(), "protocol": selected.Local.Protocol.String()}
		if pair, ok := dtls.ICETransport().GetSelectedCandidatePairStats(); ok {
			entry["pairId"] = pair.ID
			if candidate, ok := pc.GetStats()[pair.LocalCandidateID].(webrtc.ICECandidateStats); ok {
				entry["relayProtocol"] = candidate.RelayProtocol
				entry["server"] = candidate.URL
			}
		}
		connections[target] = entry
	}
	return connections
}
func (c *Client) receive(t *webrtc.TrackRemote, r *webrtc.RTPReceiver) {
	mid := ""
	for _, tr := range c.sub.GetTransceivers() {
		if tr.Receiver() == r {
			mid = tr.Mid()
			break
		}
	}
	c.mu.RLock()
	info, ok := c.infos[mid]
	c.mu.RUnlock()
	if !ok {
		return
	}
	video := t.Kind() == webrtc.RTPCodecTypeVideo
	var builder *samplebuilder.SampleBuilder
	if video {
		builder = samplebuilder.New(128, &codecs.VP8Packet{}, 90000)
		c.requestKey(t)
		ticker := time.NewTicker(3 * time.Second)
		defer ticker.Stop()
		done := make(chan struct{})
		defer close(done)
		go func() {
			for {
				select {
				case <-ticker.C:
					c.requestKey(t)
				case <-done:
					return
				case <-c.ctx.Done():
					return
				}
			}
		}()
	}
	emit := func(data []byte, stamp uint32) {
		clear, e := c.crypto.OpenMedia(data, video)
		if e != nil {
			return
		}
		frame := Frame{"frame", info.PeerID, info.Source, clear, stamp}
		select {
		case c.frames <- frame:
		default:
		}
	}
	for {
		packet, _, e := t.ReadRTP()
		if e != nil {
			return
		}
		if !video {
			emit(packet.Payload, packet.Timestamp)
			continue
		}
		builder.Push(packet)
		for {
			sample := builder.Pop()
			if sample == nil {
				break
			}
			emit(sample.Data, sample.PacketTimestamp)
		}
	}
}
func (c *Client) requestKey(t *webrtc.TrackRemote) {
	_ = c.sub.WriteRTCP([]rtcp.Packet{&rtcp.PictureLossIndication{MediaSSRC: uint32(t.SSRC())}})
}
