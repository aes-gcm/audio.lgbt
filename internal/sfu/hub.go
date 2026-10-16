package sfu

import (
	"errors"
	"net/http"
	"sync"

	"github.com/aes-gcm/audio.lgbt/internal/observe"
	"github.com/pion/logging"
)

var (
	errRoomFull     = errors.New("room is full")
	errRoomLocked   = errors.New("room is locked")
	errBadToken     = errors.New("a valid token is required to create a room")
	errNoSuchKey    = errors.New("unknown room")
	errTooManyRooms = errors.New("server is at capacity")
)

type Hub struct {
	meter       *observe.Meter
	requireAuth bool
	canCreate   func(*http.Request) bool
	engine      *Engine
	log         logging.LeveledLogger
	maxRoom     int
	maxRooms    int
	roomSecret  string

	iceServers func(participantID string) []ICEServer

	mu    sync.Mutex
	rooms map[string]*Room
}

type HubConfig struct {
	Meter       *observe.Meter
	RequireAuth bool
	CanCreate   func(*http.Request) bool
	Engine      *Engine
	Log         logging.LoggerFactory
	MaxRoom     int
	MaxRooms    int
	RoomSecret  string
	ICEServers  func(participantID string) []ICEServer
}

func NewHub(cfg HubConfig) *Hub {
	lf := cfg.Log
	if lf == nil {
		lf = logging.NewDefaultLoggerFactory()
	}
	if cfg.MaxRoom < 1 {
		cfg.MaxRoom = 25
	}
	if cfg.MaxRooms < 1 {
		cfg.MaxRooms = 2000
	}
	return &Hub{
		meter:       cfg.Meter,
		requireAuth: cfg.RequireAuth, canCreate: cfg.CanCreate,
		engine:     cfg.Engine,
		log:        lf.NewLogger("hub"),
		maxRoom:    cfg.MaxRoom,
		maxRooms:   cfg.MaxRooms,
		roomSecret: cfg.RoomSecret,
		iceServers: cfg.ICEServers,
		rooms:      map[string]*Room{},
	}
}

type Room struct {
	id  string
	hub *Hub

	mu     sync.RWMutex
	parts  map[string]*Participant
	tracks map[string]*PubTrack
	hostID string
	locked bool
}

func (h *Hub) joinRoom(id, token string) (*Room, error) {
	return h.joinAuthorized(id, token, false)
}
func (h *Hub) joinAuthorized(id, token string, authorized bool) (*Room, error) {
	if id == "" {
		return nil, errNoSuchKey
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	r, ok := h.rooms[id]
	if !ok {
		if h.requireAuth && !authorized || !h.requireAuth && h.roomSecret != "" && token != h.roomSecret {
			return nil, errBadToken
		}
		if len(h.rooms) >= h.maxRooms {
			return nil, errTooManyRooms
		}
		r = &Room{id: id, hub: h, parts: map[string]*Participant{}, tracks: map[string]*PubTrack{}}
		h.rooms[id] = r
	}
	r.mu.RLock()
	locked := r.locked
	full := len(r.parts) >= h.maxRoom
	r.mu.RUnlock()
	if locked {
		return nil, errRoomLocked
	}
	if full {
		return nil, errRoomFull
	}
	return r, nil
}

func (r *Room) add(p *Participant) ([]*Participant, error) {

	r.hub.mu.Lock()
	defer r.hub.mu.Unlock()
	if r.hub.rooms[r.id] != r {
		return nil, errNoSuchKey
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.locked {
		return nil, errRoomLocked
	}
	if len(r.parts) >= r.hub.maxRoom {
		return nil, errRoomFull
	}
	existing := make([]*Participant, 0, len(r.parts))
	for _, o := range r.parts {
		existing = append(existing, o)
	}
	if r.hostID == "" {
		r.hostID = p.id
	}
	r.parts[p.id] = p
	return existing, nil
}

func (r *Room) isHost(id string) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.hostID == id
}

func (r *Room) setLocked(b bool) {
	r.mu.Lock()
	r.locked = b
	r.mu.Unlock()
}

func (r *Room) isLocked() bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.locked
}

func (r *Room) get(id string) *Participant {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.parts[id]
}

func (r *Room) allParticipants() []*Participant {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]*Participant, 0, len(r.parts))
	for _, p := range r.parts {
		out = append(out, p)
	}
	return out
}

func (r *Room) remove(id string) (newHost string) {
	r.mu.Lock()
	delete(r.parts, id)
	for k, t := range r.tracks {
		if t.owner.id == id {
			delete(r.tracks, k)
		}
	}
	if r.hostID == id {
		r.hostID = ""
		for pid := range r.parts {
			r.hostID = pid
			newHost = pid
			break
		}
	}
	empty := len(r.parts) == 0
	r.mu.Unlock()
	if empty {
		r.hub.mu.Lock()

		if cur, ok := r.hub.rooms[r.id]; ok && cur == r {
			r.mu.RLock()
			n := len(r.parts)
			r.mu.RUnlock()
			if n == 0 {
				delete(r.hub.rooms, r.id)
			}
		}
		r.hub.mu.Unlock()
	}
	return newHost
}

func (r *Room) others(id string) []*Participant {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]*Participant, 0, len(r.parts))
	for pid, p := range r.parts {
		if pid != id {
			out = append(out, p)
		}
	}
	return out
}

func (r *Room) trackList() []*PubTrack {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]*PubTrack, 0, len(r.tracks))
	for _, t := range r.tracks {
		out = append(out, t)
	}
	return out
}

func (r *Room) addTrack(t *PubTrack) {
	r.mu.Lock()
	r.tracks[t.key] = t
	r.mu.Unlock()
}

func (r *Room) delTrack(key string) {
	r.mu.Lock()
	delete(r.tracks, key)
	r.mu.Unlock()
}

func (r *Room) broadcast(skip string, m Msg) {
	for _, p := range r.others(skip) {
		p.send(m)
	}
}
