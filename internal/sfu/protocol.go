package sfu

type Msg struct {
	Type string `json:"type"`

	Room  string `json:"room,omitempty"`
	Name  string `json:"name,omitempty"`
	Token string `json:"token,omitempty"`

	SelfID     string      `json:"selfId,omitempty"`
	Peers      []PeerInfo  `json:"peers,omitempty"`
	ICEServers []ICEServer `json:"iceServers,omitempty"`
	MaxRoom    int         `json:"maxRoom,omitempty"`

	SDP    string      `json:"sdp,omitempty"`
	Target string      `json:"target,omitempty"`
	Tracks []TrackInfo `json:"tracks,omitempty"`

	Candidate *Candidate `json:"candidate,omitempty"`

	Peer   *PeerInfo `json:"peer,omitempty"`
	PeerID string    `json:"peerId,omitempty"`

	Source   string `json:"source,omitempty"`
	Muted    *bool  `json:"muted,omitempty"`
	Up       *bool  `json:"up,omitempty"`
	Speaking *bool  `json:"speaking,omitempty"`
	Chat     string `json:"chat,omitempty"`
	Reaction string `json:"reaction,omitempty"`

	Host   bool  `json:"host,omitempty"`
	Locked *bool `json:"locked,omitempty"`

	Error string `json:"error,omitempty"`
}

type PeerInfo struct {
	ID    string          `json:"id"`
	Name  string          `json:"name"`
	Mutes map[string]bool `json:"mutes,omitempty"`
	Hand  bool            `json:"hand,omitempty"`
	Host  bool            `json:"host,omitempty"`
}

type TrackInfo struct {
	Mid    string `json:"mid"`
	PeerID string `json:"peerId"`
	Source string `json:"source"`
	Kind   string `json:"kind"`
}

type Candidate struct {
	Candidate        string  `json:"candidate"`
	SDPMid           *string `json:"sdpMid,omitempty"`
	SDPMLineIndex    *uint16 `json:"sdpMLineIndex,omitempty"`
	UsernameFragment *string `json:"usernameFragment,omitempty"`
}

type ICEServer struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}
