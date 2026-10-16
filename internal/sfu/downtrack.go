package sfu

import (
	"strings"
	"sync"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

const downQueue = 512

type pkt struct {
	seq         uint16
	ts          uint32
	marker      bool
	padding     bool
	paddingSize uint8
	payload     []byte
}

type DownTrack struct {
	id       string
	streamID string
	kind     webrtc.RTPCodecType
	codec    webrtc.RTPCodecCapability

	mu     sync.Mutex
	bound  bool
	bindID string
	ssrc   uint32
	pt     uint8
	writer webrtc.TrackLocalWriter

	started bool
	seqOff  uint16
	tsOff   uint32
	lastSeq uint16
	lastTS  uint32
	lastAt  time.Time

	queue  chan pkt
	closed chan struct{}
	once   sync.Once
}

func NewDownTrack(codec webrtc.RTPCodecCapability, id, streamID string) *DownTrack {
	kind := webrtc.RTPCodecTypeVideo
	if strings.HasPrefix(strings.ToLower(codec.MimeType), "audio/") {
		kind = webrtc.RTPCodecTypeAudio
	}
	d := &DownTrack{
		id: id, streamID: streamID, kind: kind, codec: codec,
		queue:  make(chan pkt, downQueue),
		closed: make(chan struct{}),
	}
	go d.writeLoop()
	return d
}

func (d *DownTrack) ID() string                { return d.id }
func (d *DownTrack) RID() string               { return "" }
func (d *DownTrack) StreamID() string          { return d.streamID }
func (d *DownTrack) Kind() webrtc.RTPCodecType { return d.kind }

func (d *DownTrack) Bind(ctx webrtc.TrackLocalContext) (webrtc.RTPCodecParameters, error) {
	for _, c := range ctx.CodecParameters() {
		if !strings.EqualFold(c.MimeType, d.codec.MimeType) {
			continue
		}
		d.mu.Lock()
		d.bound, d.bindID = true, ctx.ID()
		d.ssrc, d.pt = uint32(ctx.SSRC()), uint8(c.PayloadType)
		d.writer = ctx.WriteStream()
		d.mu.Unlock()
		return c, nil
	}
	return webrtc.RTPCodecParameters{}, webrtc.ErrUnsupportedCodec
}

func (d *DownTrack) Unbind(ctx webrtc.TrackLocalContext) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	if !d.bound || d.bindID != ctx.ID() {
		return webrtc.ErrUnbindFailed
	}
	d.bound, d.writer = false, nil
	return nil
}

func (d *DownTrack) Enqueue(p *rtp.Packet) {
	item := pkt{
		seq:         p.SequenceNumber,
		ts:          p.Timestamp,
		marker:      p.Marker,
		padding:     p.Padding,
		paddingSize: p.PaddingSize,
		payload:     append([]byte(nil), p.Payload...),
	}
	select {
	case d.queue <- item:
	case <-d.closed:
	default:
	}
}

func (d *DownTrack) Close() {
	d.once.Do(func() { close(d.closed) })
}

func (d *DownTrack) writeLoop() {
	for {
		select {
		case <-d.closed:
			return
		case it := <-d.queue:
			d.forward(it)
		}
	}
}

func seqNewer(a, b uint16) bool { return a != b && a-b < 0x8000 }
func tsNewer(a, b uint32) bool  { return a != b && a-b < 0x80000000 }

func (d *DownTrack) forward(it pkt) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if !d.bound || d.writer == nil {
		return
	}
	if !d.started {
		d.started = true
	}
	hdr := rtp.Header{
		Version:        2,
		Marker:         it.marker,
		PayloadType:    d.pt,
		SequenceNumber: it.seq - d.seqOff,
		Timestamp:      it.ts - d.tsOff,
		SSRC:           d.ssrc,
		Padding:        it.padding,
		PaddingSize:    it.paddingSize,
	}
	if d.lastAt.IsZero() || seqNewer(hdr.SequenceNumber, d.lastSeq) {
		d.lastSeq = hdr.SequenceNumber
		if d.lastAt.IsZero() || tsNewer(hdr.Timestamp, d.lastTS) {
			d.lastTS = hdr.Timestamp
		}
		d.lastAt = time.Now()
	}
	_, _ = d.writer.WriteRTP(&hdr, it.payload)
}
