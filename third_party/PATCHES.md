# Pion ICE policy correction

These directories are copies of the checksum-verified Go modules:

- `github.com/pion/webrtc/v4 v4.2.23` (MIT)
- `github.com/pion/ice/v4 v4.4.7` (MIT)

Their upstream license files are retained. The application imports them through explicit `replace` directives in `go.mod`; the machine's module cache is unchanged.

Windows integration exposed a real Auto → TLS failure: `SetConfiguration` updated the TURN URLs, but an existing ICE agent kept gathering host candidates. Both selected connections stayed `host/udp` after the restart.

Changes:

- `webrtc/icegatherer.go`: pass the new candidate policy together with URLs when updating the existing agent.
- `ice/agent_options.go`, `ice/agent.go`, `ice/gather.go`: publish a copied candidate-type list atomically for future gathering; an in-progress gathering uses its own snapshot. Initial construction semantics, including an explicitly empty list, are retained.
- `ice/agent_test.go`: candidate policy is no longer listed among immutable options.

Remove these replacements when an upstream release provides and verifies equivalent behavior. The native integration test must confirm the selected relay/TLS pair for both publish and subscribe and the subsequent return to Auto; continued media alone is insufficient evidence.
