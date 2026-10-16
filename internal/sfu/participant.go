package sfu

import (
	"sync"
	"time"

	"github.com/pion/logging"
	"github.com/pion/rtcp"
	"github.com/pion/webrtc/v4"
)

const pliMinInterval = 500 * time.Millisecond

type PubTrack struct {
	key      string
	owner    *Participant
	source   string
	kind     webrtc.RTPCodecType
	codec    webrtc.RTPCodecCapability
	track    *webrtc.TrackRemote
	receiver *webrtc.RTPReceiver
	ssrc     uint32

	mu      sync.RWMutex
	downs   map[string]*DownTrack
	lastPLI time.Time
}

func (t *PubTrack) addDown(subID string, d *DownTrack) {
	t.mu.Lock()
	t.downs[subID] = d
	t.mu.Unlock()
}

func (t *PubTrack) delDown(subID string) {
	t.mu.Lock()
	if d, ok := t.downs[subID]; ok {
		d.Close()
		delete(t.downs, subID)
	}
	t.mu.Unlock()
}

func (t *PubTrack) requestKeyframe() {
	t.mu.Lock()
	if time.Since(t.lastPLI) < pliMinInterval {
		t.mu.Unlock()
		return
	}
	t.lastPLI = time.Now()
	t.mu.Unlock()
	t.owner.post(func() {
		if t.owner.pub == nil {
			return
		}
		_ = t.owner.pub.WriteRTCP([]rtcp.Packet{&rtcp.PictureLossIndication{MediaSSRC: t.ssrc}})
	})
}

type subscription struct {
	pt *PubTrack
	dt *DownTrack
	tr *webrtc.RTPTransceiver
}

type Participant struct {
	id   string
	name string
	room *Room
	hub  *Hub
	log  logging.LeveledLogger

	pub *webrtc.PeerConnection
	sub *webrtc.PeerConnection

	out func(Msg)

	closeWS func()

	mbMu sync.Mutex
	mbox []func()
	sig  chan struct{}
	done chan struct{}
	once sync.Once

	labels         map[string]string
	published      map[string]*PubTrack
	subs           map[string]*subscription
	negotiating    bool
	subDirty       bool
	subRestart     bool
	lastSubRestart time.Time
	negoTimer      *time.Timer
	negoSeq        uint64

	presMu sync.Mutex
	mutes  map[string]bool
	hand   bool
}

func newParticipant(h *Hub, r *Room, id, name string) *Participant {
	return &Participant{
		id: id, name: name, room: r, hub: h,
		log:       h.log,
		sig:       make(chan struct{}, 1),
		done:      make(chan struct{}),
		labels:    map[string]string{},
		published: map[string]*PubTrack{},
		subs:      map[string]*subscription{},
		mutes:     map[string]bool{},
	}
}

const mailboxWarn = 4096

func (p *Participant) post(f func()) {
	p.mbMu.Lock()
	select {
	case <-p.done:
		p.mbMu.Unlock()
		return
	default:
	}
	p.mbox = append(p.mbox, f)
	n := len(p.mbox)
	p.mbMu.Unlock()
	if n == mailboxWarn {
		p.log.Warnf("participant %s mailbox backlog %d", p.id, n)
	}
	select {
	case p.sig <- struct{}{}:
	default:
	}
}

func (p *Participant) run() {
	for {
		select {
		case <-p.sig:
		case <-p.done:
			return
		}
		for {
			p.mbMu.Lock()
			if len(p.mbox) == 0 {
				p.mbMu.Unlock()
				break
			}
			f := p.mbox[0]
			p.mbox[0] = nil
			p.mbox = p.mbox[1:]
			p.mbMu.Unlock()
			f()
		}
	}
}

func (p *Participant) send(m Msg) {
	if p.out != nil {
		p.out(m)
	}
}

func (p *Participant) info() PeerInfo {
	p.presMu.Lock()
	mutes := make(map[string]bool, len(p.mutes))
	for k, v := range p.mutes {
		mutes[k] = v
	}
	name, hand := p.name, p.hand
	p.presMu.Unlock()
	return PeerInfo{ID: p.id, Name: name, Mutes: mutes, Hand: hand, Host: p.room.isHost(p.id)}
}

func (p *Participant) start() error {
	pub, err := p.hub.engine.NewPeerConnection()
	if err != nil {
		return err
	}
	sub, err := p.hub.engine.NewPeerConnection()
	if err != nil {
		_ = pub.Close()
		return err
	}
	p.pub, p.sub = pub, sub

	pub.OnICECandidate(func(c *webrtc.ICECandidate) { p.sendCandidate("pub", c) })
	sub.OnICECandidate(func(c *webrtc.ICECandidate) { p.sendCandidate("sub", c) })
	pub.OnTrack(func(t *webrtc.TrackRemote, recv *webrtc.RTPReceiver) {
		p.post(func() { p.onPublished(t, recv) })
	})
	closeFn := func(s webrtc.PeerConnectionState) {
		if s == webrtc.PeerConnectionStateClosed || s == webrtc.PeerConnectionStateFailed {
			p.Close()
		}
	}
	pub.OnConnectionStateChange(closeFn)
	sub.OnConnectionStateChange(closeFn)

	existing, err := p.room.add(p)
	if err != nil {
		_ = pub.Close()
		_ = sub.Close()
		return err
	}

	var ice []ICEServer
	if p.hub.iceServers != nil {
		ice = p.hub.iceServers(p.id)
	}
	peers := make([]PeerInfo, 0, len(existing))
	for _, o := range existing {
		peers = append(peers, o.info())
	}
	locked := p.room.isLocked()
	p.send(Msg{Type: "welcome", SelfID: p.id, Peers: peers, ICEServers: ice, MaxRoom: p.hub.maxRoom, Host: p.room.isHost(p.id), Locked: &locked})

	self := p.info()
	p.room.broadcast(p.id, Msg{Type: "peer-joined", Peer: &self})
	for _, pt := range p.room.trackList() {
		if pt.owner.id != p.id {
			pt := pt
			p.post(func() { p.subscribe(pt) })
		}
	}
	return nil
}

func (p *Participant) Close() {
	p.once.Do(func() {
		newHost := p.room.remove(p.id)
		p.room.broadcast(p.id, Msg{Type: "peer-left", PeerID: p.id})
		if newHost != "" {
			p.room.broadcast("", Msg{Type: "host", PeerID: newHost})
		}

		p.post(func() {
			p.teardown()
			close(p.done)
		})
	})
}

func (p *Participant) teardown() {

	for _, pt := range p.published {
		pt := pt
		for _, o := range p.room.others(p.id) {
			o := o
			o.post(func() { o.unsubscribe(pt.key) })
		}
		p.room.delTrack(pt.key)
		pt.mu.Lock()
		for _, d := range pt.downs {
			d.Close()
		}
		pt.downs = map[string]*DownTrack{}
		pt.mu.Unlock()
	}

	for _, s := range p.subs {
		s.pt.delDown(p.id)
		s.dt.Close()
	}
	if p.pub != nil {
		_ = p.pub.Close()
	}
	if p.sub != nil {
		_ = p.sub.Close()
	}
	if p.closeWS != nil {

		cw := p.closeWS
		time.AfterFunc(500*time.Millisecond, cw)
	}
}

func (p *Participant) sendCandidate(target string, c *webrtc.ICECandidate) {
	cand := &Candidate{Candidate: ""}
	if c != nil {
		init := c.ToJSON()
		cand.Candidate = init.Candidate
		cand.SDPMid = init.SDPMid
		cand.SDPMLineIndex = init.SDPMLineIndex
		cand.UsernameFragment = init.UsernameFragment
	}
	p.send(Msg{Type: "ice", Target: target, Candidate: cand})
}
