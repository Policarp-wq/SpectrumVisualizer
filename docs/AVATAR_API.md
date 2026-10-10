# TrackPeek Avatar API v1

This is a read-only local feed for any companion, avatar, visualizer, or other client that wants to react to TrackPeek's audio analysis. It exposes normalized metrics and media-session metadata; it does not send raw audio or prescribe animation behavior.

## Connection

- Health: `GET http://127.0.0.1:47831/v1/health`
- WebSocket: `ws://127.0.0.1:47831/v1/ws`
- The listener binds only to IPv4 loopback. It is not reachable from the LAN.
- Native clients must omit the `Origin` header. Requests with an `Origin` are rejected so ordinary web pages cannot read the local feed.
- The API is read-only. It accepts WebSocket close frames; other client messages are rejected. There are no control commands.
- Up to eight clients can connect. TrackPeek broadcasts a latest-state snapshot at 10 Hz; reconnecting clients receive the next snapshot, not a replay of missed frames.

The health endpoint returns:

```json
{
  "status": "ok",
  "protocolVersion": 1,
  "webSocketUrl": "ws://127.0.0.1:47831/v1/ws",
  "readOnly": true
}
```

## WebSocket messages

The first text message is `hello`:

```json
{
  "type": "hello",
  "protocolVersion": 1,
  "sampleRateHz": 10,
  "readOnly": true
}
```

After that, the server sends `state` snapshots:

```json
{
  "type": "state",
  "protocolVersion": 1,
  "sequence": 42,
  "timestampUtc": "2026-10-10T12:30:00.000Z",
  "audio": {
    "active": true,
    "energy": 0.31,
    "bass": 0.52,
    "mid": 0.28,
    "treble": 0.13,
    "bands": [0.12, 0.18, 0.31, 0.45, 0.52, 0.44, 0.39, 0.34, 0.28, 0.24, 0.21, 0.18, 0.16, 0.14, 0.12, 0.11, 0.1, 0.09, 0.08, 0.07],
    "bpm": 120.0,
    "bpmConfidence": 0.83,
    "beatId": 18
  },
  "media": {
    "playbackState": "playing",
    "title": "Example track",
    "artist": "Example artist",
    "sourceAppId": "Spotify.exe",
    "positionMs": 32000,
    "durationMs": 198000
  }
}
```

### Field semantics

- `sequence` increases for each broadcast during this TrackPeek process. A client may see gaps; use the newest snapshot rather than expecting every sequence.
- `timestampUtc` is when the audio analyzer produced the snapshot, not when the client received it.
- `audio.bands` always has 20 normalized values in `[0, 1]`, low to high, covering approximately 55 Hz–11 kHz. These values are independent of the visualizer sensitivity setting.
- `energy`, `bass`, `mid`, and `treble` are averages of `bands`. `bass` uses bands 0–4, `mid` 5–13, and `treble` 14–19. `active` becomes true when average band energy is at least 0.025.
- `beatId` increments when the onset detector accepts a beat. Detect a new beat by comparing IDs; do not trigger on every `state` message while the ID is unchanged.
- `bpm` is `null` until enough regular onsets have been observed, or when confidence is below 0.25. Treat both BPM and `bpmConfidence` as estimates, not ground truth. The current detector estimates roughly 40–240 BPM from audio transients; it does not identify genre or mood.
- `media` is `null` when Windows has no current media session. Its position and duration are milliseconds or `null` if Windows does not provide a valid timeline. Playback state is one of `playing`, `paused`, `stopped`, or `unknown`.
- Track title, artist, and source app ID are local media metadata. Keep them optional in consumers that only need movement cues.

## Consumer guidance

1. Connect as a native local client without an `Origin` header.
2. Wait for `hello`, then consume `state` messages. Reconnect with a short backoff if TrackPeek exits or restarts.
3. Use `beatId` changes for beat-synchronized actions. Use `bpmConfidence` before relying on tempo; low confidence should fall back to energy-only movement.
4. Smooth `energy` and the bands for continuous motion, but handle beat changes immediately. Do not infer a single “mood” from BPM alone.
5. Do not add a second audio-capture pipeline in the avatar. TrackPeek already listens to system output.

## Handoff prompt for an avatar agent

> Implement only the avatar consumer, not the TrackPeek API or another audio-capture service. Read `docs/AVATAR_API.md` and connect to `ws://127.0.0.1:47831/v1/ws` as a native local WebSocket client with no `Origin` header. Handle the `hello` message and protocol version, consume the latest `state` snapshots, reconnect with backoff, trigger beat actions only when `audio.beatId` changes, and use `bpmConfidence` to decide whether tempo is reliable. Keep the animation layer independent of the transport so a future avatar can change without changing the API. Do not send commands; the v1 API is read-only.
