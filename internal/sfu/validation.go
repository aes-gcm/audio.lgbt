package sfu

import (
	"errors"
	"strings"

	"github.com/pion/sdp/v3"
)

var errInvalidMedia = errors.New("invalid media description")

func validatePublish(m Msg) (map[string]string, error) {
	if len(m.SDP) > 128<<10 || len(m.Tracks) > 4 {
		return nil, errInvalidMedia
	}
	var description sdp.SessionDescription
	if description.Unmarshal([]byte(m.SDP)) != nil || len(description.MediaDescriptions) > 64 {
		return nil, errInvalidMedia
	}
	labels, sources := map[string]string{}, map[string]bool{}
	for _, track := range m.Tracks {
		if !validSource(track.Source) || track.Mid == "" || len(track.Mid) > 32 || labels[track.Mid] != "" || sources[track.Source] {
			return nil, errInvalidMedia
		}
		labels[track.Mid], sources[track.Source] = track.Source, true
	}
	active, candidates := 0, 0
	for _, media := range description.MediaDescriptions {
		kind := media.MediaName.Media
		if kind != "audio" && kind != "video" {
			return nil, errInvalidMedia
		}
		mid, _ := media.Attribute("mid")
		if mid == "" || len(mid) > 32 || len(media.Attributes) > 256 {
			return nil, errInvalidMedia
		}
		for _, attr := range media.Attributes {
			if len(attr.Value) > 8192 {
				return nil, errInvalidMedia
			}
			if attr.Key == "candidate" {
				candidates++
			}
			if attr.Key == "ssrc" && strings.Contains(attr.Value, " ") && len(attr.Value) > 256 {
				return nil, errInvalidMedia
			}
		}
		_, inactive := media.Attribute("inactive")
		_, recvonly := media.Attribute("recvonly")
		_, bundleOnly := media.Attribute("bundle-only")
		if (media.MediaName.Port.Value == 0 && !bundleOnly) || inactive || recvonly {
			continue
		}
		active++
		source := labels[mid]
		if source == "" || ((source == "mic" || source == "screen-audio") != (kind == "audio")) {
			return nil, errInvalidMedia
		}
	}
	if active > 4 || candidates > 128 {
		return nil, errInvalidMedia
	}
	return labels, nil
}
