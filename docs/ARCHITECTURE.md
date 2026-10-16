# Implementation notes

The browser and native client use separate publishing/subscribing PeerConnections. The SFU routes tracks by participant and source: `mic`, `cam`, `screen`, `screen-audio`. Screen sound is independent of microphone muting. The clients encrypt encoded frames and chat with AES-GCM using keys derived by HKDF from the invitation fragment. Authentication sessions and room encryption keys are separate; account credentials never enter invitations.

Room creation authorization is enforced inside the hub's room lookup/creation lock. Existing rooms admit invited guests without an account. Capacity and lock state are rechecked while adding participants to prevent races. WebSocket origins are checked before upgrading. Local accounts contain bcrypt hashes; sessions are random opaque tokens, kept in server memory and transported via HttpOnly cookies or the native engine's Authorization header. Account removal/password replacement revokes sessions at subsequent authorization checks.

The native UI passes its selected origin and session to the Go engine through redirected stdin, never command-line arguments. Server URLs require HTTPS except loopback development addresses. Redirects are disabled for login and discovery. Each server selection constructs a fresh CookieContainer; only the URL is persisted. Official update URLs remain fixed to audio.lgbt and are not inherited from the call server.

Windows monitor capture uses DXGI Desktop Duplication and never falls back to Windows Graphics Capture. WGC border suppression can reset after exclusive-fullscreen games, so switching monitor capture back to WGC reintroduces the yellow-border issue. Window capture still uses WGC. Capture owners serialize acquisition/disposal and keep one duplication per process/output, shared with previews.

Camera views display the complete transmitted frame with `object-fit: contain`, no CSS mirroring and labels outside the frame. Portrait layout changes tile dimensions instead of cropping the camera. The SFU does not negotiate CVO because its forwarder does not preserve that RTP extension; sources must apply camera rotation to pixels. Synthetic framing tests are not substitutes for physical phone/browser testing.

The two Pion module replacements are intentional. See `third_party/PATCHES.md` before upgrading or removing them: Auto/TLS switching must verify actual selected relay/TLS candidates, not merely continued media.

The version-one HKDF context is a fixed wire identifier, represented by byte escapes. Renaming a product must not change those bytes: existing invitation links and older clients derive the same room, media and chat keys. New browser preferences use the audio namespace and migrate the previous name key.
