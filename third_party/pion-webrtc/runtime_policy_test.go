package webrtc

import (
	"testing"
	"time"
)

func TestRuntimeGatherPolicy(t *testing.T) {
	pc, err := NewPeerConnection(Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	if _, err = pc.AddTransceiverFromKind(RTPCodecTypeAudio); err != nil {
		t.Fatal(err)
	}
	gather := func(restart bool) int {
		t.Helper()
		done := GatheringCompletePromise(pc)
		var e error
		if restart {
			e = pc.iceTransport.restart()
		} else {
			e = pc.iceGatherer.Gather()
		}
		if e != nil {
			t.Fatal(e)
		}
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			t.Fatal("gather timeout")
		}
		candidates, e := pc.iceGatherer.GetLocalCandidates()
		if e != nil {
			t.Fatal(e)
		}
		return len(candidates)
	}
	if gather(false) == 0 {
		t.Skip("no interfaces")
	}
	if err = pc.SetConfiguration(Configuration{ICETransportPolicy: ICETransportPolicyRelay}); err != nil {
		t.Fatal(err)
	}
	if n := gather(true); n != 0 {
		t.Fatalf("relay-only gathering leaked %d direct candidates", n)
	}
	if err = pc.SetConfiguration(Configuration{ICETransportPolicy: ICETransportPolicyAll}); err != nil {
		t.Fatal(err)
	}
	if n := gather(true); n == 0 {
		t.Fatal("Auto did not resume direct candidate gathering")
	}
}
