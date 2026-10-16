package sfu

import (
	"strings"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/webrtc/v4"
)

const negoDebounce = 30 * time.Millisecond

func (p *Participant) handle(m Msg) {
	switch m.Type {
	case "publish-offer":
		p.onPublishOffer(m)
	case "restart-sub-ice":
		if time.Since(p.lastSubRestart) >= time.Second {
			p.lastSubRestart = time.Now()
			p.subRestart = true
			p.subDirty = true
			p.scheduleNego()
		}
	case "subscribe-answer":
		p.onSubscribeAnswer(m)
	case "ice":
		p.onRemoteCandidate(m)
	case "mute":
		p.onMute(m)
	case "hand":
		p.onHand(m)
	case "speaking":
		if m.Speaking != nil {
			p.room.broadcast(p.id, Msg{Type: "speaking", PeerID: p.id, Speaking: m.Speaking})
		}
	case "chat":
		if m.Chat != "" && len(m.Chat) <= 16384 {
			p.room.broadcast(p.id, Msg{Type: "chat", PeerID: p.id, Chat: m.Chat})
		}
	case "reaction":
		if r := m.Reaction; len(r) > 0 && len(r) <= 16 {
			p.room.broadcast(p.id, Msg{Type: "reaction", PeerID: p.id, Reaction: r})
		}
	case "mute-all", "remove", "lock", "end":
		p.onModeration(m)
	case "rename":
		if name := clip(m.Name, 64); name != "" {
			p.presMu.Lock()
			p.name = name
			p.presMu.Unlock()
			info := p.info()
			p.room.broadcast(p.id, Msg{Type: "peer-updated", Peer: &info})
		}
	}
}

func clip(s string, n int) string {
	s = strings.TrimSpace(s)
	r := []rune(s)
	if len(r) > n {
		r = r[:n]
	}
	return string(r)
}

func validSource(s string) bool {
	return s == "mic" || s == "cam" || s == "screen" || s == "screen-audio"
}

func (p *Participant) onPublishOffer(m Msg) {
	if p.pub == nil {
		return
	}
	labels, err := validatePublish(m)
	if err != nil {
		p.hub.meter.Add("invalid_publish_rejected", 1)
		p.Close()
		return
	}
	p.labels = labels
	if err := p.pub.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: m.SDP}); err != nil {
		p.log.Warnf("pub SetRemoteDescription: %v", err)
		return
	}

	for _, tr := range p.pub.GetTransceivers() {
		if p.labels[tr.Mid()] == "screen-audio" && tr.Receiver() != nil {
			codecs := tr.Receiver().GetParameters().Codecs
			for i := range codecs {
				if strings.EqualFold(codecs[i].MimeType, webrtc.MimeTypeOpus) {
					codecs[i].SDPFmtpLine = "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxaveragebitrate=192000;usedtx=0"
				}
			}
			if err := tr.SetCodecPreferences(codecs); err != nil {
				p.log.Warnf("display audio codec: %v", err)
			}
		}
	}
	answer, err := p.pub.CreateAnswer(nil)
	if err != nil {
		p.log.Warnf("pub CreateAnswer: %v", err)
		return
	}
	if err := p.pub.SetLocalDescription(answer); err != nil {
		p.log.Warnf("pub SetLocalDescription: %v", err)
		return
	}
	p.send(Msg{Type: "publish-answer", Target: "pub", SDP: p.pub.LocalDescription().SDP})
}

func (p *Participant) onPublished(track *webrtc.TrackRemote, recv *webrtc.RTPReceiver) {
	mid := ""
	for _, tr := range p.pub.GetTransceivers() {
		if tr.Receiver() == recv {
			mid = tr.Mid()
			break
		}
	}
	source := p.labels[mid]
	if !validSource(source) {
		p.Close()
		return
	}
	for _, published := range p.published {
		if published.source == source {
			if published.receiver == recv {
				p.Close()
				return
			}
			p.onUnpublished(published)
			_ = published.receiver.Stop()
		}
	}
	if len(p.published) >= 4 {
		p.Close()
		return
	}

	key := p.id + ":" + source + "#" + mid
	pt := &PubTrack{
		key:      key,
		owner:    p,
		source:   source,
		kind:     track.Kind(),
		codec:    track.Codec().RTPCodecCapability,
		track:    track,
		receiver: recv,
		ssrc:     uint32(track.SSRC()),
		downs:    map[string]*DownTrack{},
	}
	p.published[key] = pt
	p.room.addTrack(pt)

	go p.readRTP(pt)
	go p.readReceiverRTCP(pt)

	for _, o := range p.room.others(p.id) {
		o, pt := o, pt
		o.post(func() { o.subscribe(pt) })
	}
}

func (p *Participant) readRTP(pt *PubTrack) {
	for {
		pkt, _, err := pt.track.ReadRTP()
		if err != nil {
			p.post(func() { p.onUnpublished(pt) })
			return
		}
		pt.mu.RLock()
		for _, d := range pt.downs {
			d.Enqueue(pkt)
		}
		pt.mu.RUnlock()
	}
}

func (p *Participant) onUnpublished(pt *PubTrack) {
	if p.published[pt.key] != pt {
		return
	}
	delete(p.published, pt.key)
	p.room.delTrack(pt.key)
	pt.mu.Lock()
	for _, d := range pt.downs {
		d.Close()
	}
	pt.downs = map[string]*DownTrack{}
	pt.mu.Unlock()
	for _, o := range p.room.others(p.id) {
		o := o
		o.post(func() { o.unsubscribe(pt.key) })
	}
}

func (p *Participant) readReceiverRTCP(pt *PubTrack) {
	for {
		if _, _, err := pt.receiver.ReadRTCP(); err != nil {
			return
		}
	}
}

func (p *Participant) subscribe(pt *PubTrack) {
	if pt.owner.id == p.id {
		return
	}
	if _, ok := p.subs[pt.key]; ok {
		return
	}
	dt := NewDownTrack(pt.codec, pt.key, pt.owner.id)
	tr, err := p.sub.AddTransceiverFromTrack(dt, webrtc.RTPTransceiverInit{
		Direction: webrtc.RTPTransceiverDirectionSendonly,
	})
	if err != nil {
		dt.Close()
		p.log.Warnf("AddTransceiverFromTrack: %v", err)
		return
	}
	p.subs[pt.key] = &subscription{pt: pt, dt: dt, tr: tr}
	pt.addDown(p.id, dt)
	go p.readSenderRTCP(pt, tr.Sender())
	pt.requestKeyframe()
	p.subDirty = true
	p.scheduleNego()
}

func (p *Participant) unsubscribe(key string) {
	s, ok := p.subs[key]
	if !ok {
		return
	}
	delete(p.subs, key)
	s.pt.delDown(p.id)
	s.dt.Close()
	if p.sub != nil && s.tr.Sender() != nil {
		_ = p.sub.RemoveTrack(s.tr.Sender())
	}
	p.subDirty = true
	p.scheduleNego()
}

func (p *Participant) readSenderRTCP(pt *PubTrack, sender *webrtc.RTPSender) {
	for {
		pkts, _, err := sender.ReadRTCP()
		if err != nil {
			return
		}
		for _, pk := range pkts {
			switch pk.(type) {
			case *rtcp.PictureLossIndication, *rtcp.FullIntraRequest:
				pt.requestKeyframe()
			}
		}
	}
}

func (p *Participant) scheduleNego() {
	if p.negoTimer != nil {
		return
	}
	p.negoTimer = time.AfterFunc(negoDebounce, func() {
		p.post(func() {
			p.negoTimer = nil
			p.doNego()
		})
	})
}

func (p *Participant) doNego() {
	if p.sub == nil {
		return
	}
	if p.negotiating {
		p.subDirty = true
		return
	}
	if !p.subDirty {
		return
	}
	offer, err := p.sub.CreateOffer(&webrtc.OfferOptions{ICERestart: p.subRestart})
	if err != nil {
		p.log.Warnf("sub CreateOffer: %v", err)
		return
	}
	if err := p.sub.SetLocalDescription(offer); err != nil {
		p.log.Warnf("sub SetLocalDescription: %v", err)
		return
	}
	p.negotiating = true
	p.subDirty = false
	p.subRestart = false

	p.negoSeq++
	seq := p.negoSeq
	time.AfterFunc(10*time.Second, func() {
		p.post(func() {
			if p.negotiating && p.negoSeq == seq {
				p.log.Warnf("sub negotiation timed out for %s; retrying", p.id)
				p.negotiating = false
				p.subDirty = true
				p.scheduleNego()
			}
		})
	})

	tracks := make([]TrackInfo, 0, len(p.subs))
	for _, s := range p.subs {
		mid := s.tr.Mid()
		if mid == "" {
			continue
		}
		kind := "video"
		if s.pt.kind == webrtc.RTPCodecTypeAudio {
			kind = "audio"
		}
		tracks = append(tracks, TrackInfo{Mid: mid, PeerID: s.pt.owner.id, Source: s.pt.source, Kind: kind})
	}
	p.send(Msg{Type: "subscribe-offer", Target: "sub", SDP: p.sub.LocalDescription().SDP, Tracks: tracks})
}

func (p *Participant) onSubscribeAnswer(m Msg) {
	if p.sub == nil {
		return
	}
	if !p.negotiating {
		return
	}
	if err := p.sub.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: m.SDP}); err != nil {
		p.log.Warnf("sub SetRemoteDescription: %v", err)

		p.negotiating = false
		p.subDirty = true
		p.scheduleNego()
		return
	}
	p.negotiating = false
	if p.subDirty {
		p.scheduleNego()
	}
}

func (p *Participant) onRemoteCandidate(m Msg) {
	if m.Candidate == nil {
		return
	}
	init := webrtc.ICECandidateInit{
		Candidate:        m.Candidate.Candidate,
		SDPMid:           m.Candidate.SDPMid,
		SDPMLineIndex:    m.Candidate.SDPMLineIndex,
		UsernameFragment: m.Candidate.UsernameFragment,
	}
	var pc *webrtc.PeerConnection
	if m.Target == "pub" {
		pc = p.pub
	} else {
		pc = p.sub
	}
	if pc == nil || pc.RemoteDescription() == nil {
		return
	}
	if err := pc.AddICECandidate(init); err != nil {
		p.log.Warnf("AddICECandidate(%s): %v", m.Target, err)
	}
}

func (p *Participant) onMute(m Msg) {
	if !validSource(m.Source) || m.Muted == nil {
		return
	}
	p.presMu.Lock()
	p.mutes[m.Source] = *m.Muted
	p.presMu.Unlock()
	p.room.broadcast(p.id, Msg{Type: "mute", PeerID: p.id, Source: m.Source, Muted: m.Muted})
}

func (p *Participant) onHand(m Msg) {
	if m.Up == nil {
		return
	}
	p.presMu.Lock()
	p.hand = *m.Up
	p.presMu.Unlock()
	p.room.broadcast(p.id, Msg{Type: "hand", PeerID: p.id, Up: m.Up})
}

func (p *Participant) onModeration(m Msg) {
	if !p.room.isHost(p.id) {
		return
	}
	switch m.Type {
	case "mute-all":

		p.room.broadcast(p.id, Msg{Type: "mute-request", Source: "mic"})
	case "lock":
		if m.Locked != nil {
			p.room.setLocked(*m.Locked)
			p.room.broadcast("", Msg{Type: "locked", Locked: m.Locked})
		}
	case "remove":
		if target := p.room.get(m.PeerID); target != nil && target.id != p.id {
			target.send(Msg{Type: "removed"})
			target.Close()
		}
	case "end":
		p.room.setLocked(true)
		for _, o := range p.room.allParticipants() {
			o.send(Msg{Type: "ended"})
			o.Close()
		}
	}
}
