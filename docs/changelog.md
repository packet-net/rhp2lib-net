# Release notes

This page mirrors the [GitHub Releases](https://github.com/M0LTE/rhp2lib-net/releases)
feed, with a short summary per version.

## Unreleased

### Fixed

* A throwing user event handler (`Received`, `Accepted`, etc.) no longer
  faults the read loop — previously one misbehaving subscriber tore down
  the whole RHP connection and failed every in-flight request.
* Undecodable frames now surface their raw text through
  `UnknownReceived` (`UnknownMessage.Raw["raw"]`) instead of an empty
  placeholder, so consumers can log something actionable.
* `UnknownMessage` no longer throws on forward-compatible frames whose
  `id`/`seqno` aren't numeric.

### Added

* `RhpClient.MaxSendDataLength` (default 8100): client-side guard
  against xrouter's silent drop of `send.data` above ~8 KB (issue #7).
  Oversized sends now throw `ArgumentException` instead of hanging the
  awaiting caller forever. Set to `null` to disable.
* `MockRhpServer.BroadcastRawAsync` for injecting raw / malformed frames
  in tests.

## 0.2.2 — first NuGet.org publish

Released 2026-05-04.

Same source as 0.2.1; re-cut to exercise the full release flow now
that the `NUGET_API_KEY` repository secret is configured. The
`RhpV2.Client` package is published to <https://www.nuget.org/> from
this version onward.

## 0.2.1 — real-xrouter integration

Released 2026-05-04.

This release is mostly about driving the library against real XRouter
(via a Testcontainers-based integration suite) and aligning the wire
format with what the live server actually emits.  Two breaking
changes; everything else is additive.

### Breaking

* `AcceptMessage.Port` is now `string?` (was `int?`). Real XRouter
  sends `accept.port` as a JSON string, not the unquoted number the
  PWP-0222 example shows. The library reads either shape via a
  `StringOrIntConverter`. If you were reading `AcceptMessage.Port`
  as `int?`, switch to `string` and parse if you need an integer.
* `QueryStatusAsync` now returns `Task<StatusFlags>` (was
  `Task<StatusReplyMessage>`) and accepts an optional
  `responseTimeout`. The previous shape hung in the success case
  because per spec the server replies to a successful status query
  with a `status` notification (no request `id`), not a
  `statusReply`. The new method races the notification path against
  the error path and throws `RhpProtocolException` on timeout.

### Added

* `RhpV2.Client` now multi-targets `net8.0` and `net10.0`. The
  package ships both `lib/net8.0/` and `lib/net10.0/` assemblies;
  consumers on .NET 8, 9 or 10 pick up a matching one via NuGet's
  TFM resolution. The CLI (`RhpV2.Tools`) stays single-targeted on
  net10.0 since it's a self-contained binary, not a library.
* `release.yml` gained a `publish-nuget` job that pushes the packed
  `.nupkg` to nuget.org on tag-triggered runs. The push is gated on
  the `NUGET_API_KEY` repository secret — if it's not set, the job
  logs a warning and exits cleanly so the rest of the release flow
  still completes (so the workflow can be exercised before the key
  is configured).
* New `RhpV2.Client.IntegrationTests` project: drives the published
  `ghcr.io/packethacking/xrouter` image via Testcontainers to pin
  client behaviour against a real RHP server. Includes a
  two-container fixture connected by AXUDP that exercises the full
  data path (`RhpClient → RHP → AX.25 L2 → AXUDP → peer node`) — real
  SABM/UA handshake, real I-frame send/recv, real orderly close.
  Requires a running Docker daemon; the fixtures fail loudly on
  startup error rather than silently skipping, so a green run
  actually means the integration paths were exercised.
* `RhpErrorCode.NotConnected` (17): real XRouter returns this on
  `send` against a stream socket whose downlink is not connected.
  The PWP-0222 / PWP-0245 error tables stop at 16; added to the
  client's enum and `Text(...)` lookup.
* `RecvMessage` extensions: `Tseq`, `Ilen`, `Pid`, `Ptcl` for
  TRACE-mode I-frames, plus `Local` / `Remote` for DGRAM-mode
  receive addressing.
* `StringOrIntConverter` for fields that are wire-typed
  inconsistently across modes (`accept.port`, `recv.port` —
  string in DGRAM, number in TRACE).

### Fixed / aligned with reality

* `connectReply` workaround: real XRouter returns a successful
  `connectReply` with `errCode = handle` (rather than 0) but
  `errText = "Ok"`. The library now treats any `connectReply` whose
  text is `"Ok"` as success regardless of the numeric code so callers
  don't see spurious `RhpServerException` throws on a working AX.25
  connect. Real failures (`"No Route"`, `"Not bound"`, etc.) still
  raise as before.
* Wire-format alignment: every reply now serialises `errCode` /
  `errText` with capital C/T, matching what XRouter actually emits.
  The published spec only mentions this as an AUTHREPLY quirk, but
  integration testing confirmed it applies to every reply. Reads
  remain case-insensitive so older / lowercase wire forms still
  parse.
* Mock alignment: `MockRhpServer` no longer echoes the request `id`
  on notification-shaped replies (anything carrying a `seqno`),
  matching real XRouter's wire behaviour.

### Integration coverage

The integration suite verifies (against a live XRouter container):

* AX.25 stream connect / send / recv / close end-to-end across
  AXUDP — real SABM/UA/I/RR exchange.
* Passive listener accepts an inbound peer connection;
  peer-initiated close fires the listener-side `Closed` event.
* TRACE-mode listener captures real frames with decoded fields.
* RAW-mode listener surfaces complete on-the-wire AX.25 bytes.
* DGRAM `sendto` with byte-perfect binary round-trip.
* NetRom stream connect routing through AX.25 to the peer node.
* `pfam=inet` stream HTTP/1.0 GET to XRouter's own HTTP server,
  including server-initiated close.
* `seqpkt` / `custom` socket allocation; `dgram` listen rejection.
* Connect-to-unreachable lifecycle: `connectReply` ok →
  `status flags=0` → `close` after FRACK retries.
* BUSY flag in `sendReply.status` on multi-KB writes;
  duplicate-listen detection; concurrent streams on one RHP TCP.

### Documentation

* Protocol primer extended with the spec-vs-reality deltas
  surfaced by the integration suite (filed as issues #2–#7):
  every reply uses capital `errCode`/`errText`,
  `connectReply.errCode` mirrors `handle` on success,
  `accept.port` is wire-typed as a string, `recv.port` differs
  between TRACE and DGRAM, undocumented `errCode 17 "Not
  connected"`, the ~8 KB `send.data` cliff above which XRouter
  silently drops the request, the global handle namespace, the
  post-bad-auth lockout, and the `RHPPORT=9000` config requirement.
* Initial public docs site (mkdocs-material) wired up at
  <https://rhp2lib.pages.dev/>.

## 0.1.0 — initial cut

* `RhpV2.Client` library targeting `net10.0`:
    * 2-byte big-endian framing.
    * Strongly-typed DTOs for all 22 RHPv2 message types.
    * `RhpClient` with async request/reply correlation and event-style
      notifications (`Received`, `Accepted`, `StatusChanged`, `Closed`,
      `Disconnected`, `UnknownReceived`).
    * Tolerates spec quirks (`errCode` vs `errcode`, `ConnectReply`
      PascalCase) on read.
* `MockRhpServer` shipped with the library.
* `rhp` CLI with `probe`, `chat`, `mon`, `send`, `serve`.
* CI matrix on ubuntu / windows / macOS.
* Release workflow that publishes self-contained single-file binaries
  for `linux-x64`, `linux-arm64`, `linux-musl-x64`, `win-x64`,
  `win-arm64`, `osx-x64`, `osx-arm64`, and packs the NuGet.
* 31-test xunit suite.
