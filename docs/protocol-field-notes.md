# Protocol field notes

[PWP-0222](https://wiki.oarc.uk/packet:white-papers:pwp-0222) closes by
describing itself as a first draft and soliciting feedback.  This page is
that feedback, from one implementation effort: a collection of
observations made while writing this library, its mock server, and the
integration suite that drives the real xrouter — together with some
ideas for what a hypothetical RHPv2.1 or v3 might tighten up.

!!! note "Spirit of this page"
    RHPv2 is a *good* protocol, and this page only exists because it was
    pleasant enough to implement that we kept going until the corners
    were reachable.  Everything below was found by an implementation
    written against the published papers and a live xrouter; some of it
    may turn out to be a misreading of intent rather than a gap, and
    corrections are very welcome.  Where an observation already has a
    workaround in this library, it's linked — none of this blocks
    building real applications today.

## What RHPv2 gets right

It's worth being explicit about the foundations, because they're the
reason the rest of this page is about *refinements* rather than
*problems*:

* **JSON over TCP with a 2-byte length prefix** is debuggable with
  `tcpdump`, implementable in any language in an evening, and a huge
  step up from binary RHPv1.  Keeping it should be a design goal of any
  future revision.
* **The combined `open`** (socket + bind + listen/connect in one
  message) is genuinely good ergonomics — most applications never need
  anything else.
* **`id`-correlated request/reply plus `seqno`-tagged notifications**
  is a sound async model that maps directly onto modern client
  libraries (it became one `RequestAsync` method and five events here).
* **The family/mode matrix** (`ax25`/`netrom`/`inet`/`unix` ×
  `stream`/`dgram`/`trace`/`raw`/…) cleanly exposes everything xrouter
  can do without inventing per-protocol message types.

The whole client, mock server and CLI fit in a few thousand lines.
Small protocol, small implementations — that's the compliment.

## Observations

These are ordered roughly by how much they affected the implementation,
not by severity.  The [protocol primer](protocol.md#spec-quirks-the-library-tolerates)
lists the raw spec-vs-reality deltas; this section is about the *design*
consequences.

### 1. A successful status query has no correlatable reply

Per the spec, `statusReply` is only sent when a status *query* fails;
on success the server emits a `status` **notification** — which carries
a `seqno` but not the request's `id`.  A client therefore cannot
distinguish "the answer to my query" from "an unrelated link-state
change that happened to arrive at the same moment".

This library's
[`QueryStatusAsync`](https://github.com/M0LTE/rhp2lib-net/blob/main/src/RhpV2.Client/RhpClient.cs)
has to race an event subscription against the error-reply path, and its
own doc comment admits the race can be satisfied by a coincidental
push.  Roughly forty lines of careful code stand in for one echoed
field.

**Suggestion:** every request that carries an `id` gets exactly one
reply echoing that `id` — including the success case of `status`.
Nothing stops the server *also* pushing the usual notification.  This
is the single highest-value, lowest-cost change on this page.

### 2. `connectReply.errCode` mirrors the handle on success

A successful `connect` returns `errCode` equal to the socket handle
(rather than `0`), with `errText:"Ok"`.  This breaks the otherwise
universal "`errCode == 0` means success" contract, and it forces
clients to fall back to *string-comparing `errText`* as the success
signal — which is what this library does
([pinned at the wire level](https://github.com/M0LTE/rhp2lib-net/blob/main/tests/RhpV2.Client.IntegrationTests/Ax25OverAxudpTests.cs)
so we notice when it's fixed).

**Suggestion:** `errCode: 0` on success, like every other reply.

### 3. Oversized `send.data` vanishes silently

`send.data` above ~8 KB (the cliff sits between 8 100 and 8 200 bytes)
is dropped with no `sendReply`, no error, and a healthy-looking TCP
connection.  A client awaiting the reply hangs forever.  Compounding
it, the limit isn't advertised anywhere, so a client can't even avoid
the cliff on purpose — it has to know folklore.

**Suggestion:** reply with `errCode: 13` ("No buffers" — it already
exists and fits), and advertise the actual limit somewhere a client
can read it (see the next item).

### 4. There's no way to ask the server what it supports

No version number, no capability list, no limits.  A client can't learn
which families/modes this xrouter build supports, what the maximum
`data` size is, or whether any given quirk on this page has been fixed
— except by trying things and watching what happens.  This also makes
the protocol hard to *evolve*: any behavioural fix risks breaking
deployed clients, because there's no way for either side to detect the
other's vintage.

This matters more now that RHP is no longer a one-server protocol:
besides xrouter there's BPQ's server-side implementation (deliberately
partial — enough to support WhatsPac), WhatsPac as a client, and the
Samoyed soundmodem's implementation in progress.  A client today can't
even discover *which subset* it's talking to.

There is, happily, an accident of the current implementation that makes
this retrofittable: xrouter answers an unknown `type` by appending
`Reply` to it and setting `errCode: 2`.  So a hypothetical

```json
{ "type": "hello", "id": 1 }
```

is *already* cleanly detectable today: an old server answers
`helloReply` / `errCode: 2` (→ assume baseline v2), a new server could
answer `errCode: 0` plus capability fields:

```json
{
  "type": "helloReply", "id": 1, "errCode": 0, "errText": "Ok",
  "proto": "2.1", "impl": "xrouter 504j",
  "pfams": ["ax25", "netrom", "inet", "unix"],
  "maxData": 8100,
  "enc": ["latin1", "b64"]
}
```

**Suggestion:** an optional `hello`/`helloReply` exchange.  It costs a
few lines in the server, is fully backwards compatible, and is the
prerequisite for fixing everything else without requiring every client
and node on the network to upgrade at the same moment.

*Postscript:* this proposal now has a live answer.  pdn — the second
server-side implementation introduced before observation 13 — answers
`hello` with `errCode: 0` and exactly the fields sketched above
(`proto`, `impl`, `pfams`, `maxData`, `enc`), while xrouter's
unknown-type fallback keeps answering `helloReply` / `errCode: 2`.  A
client probing today gets a capability list from one server and a
clean "assume baseline v2" from the other — the backwards-compatibility
story above, demonstrated rather than predicted.

### 5. Binary payloads are underspecified

The spec says control characters in `data` must be JSON-escaped, but
JSON strings are sequences of Unicode code points, not bytes — so for
bytes 0x80–0xFF there are two equally spec-compliant serialisations of
the *same* JSON document that put *different octets* on the wire: the
six-character escape `ÿ`, or the two-byte UTF-8 encoding of U+00FF.
A conforming JSON library is free to emit either.

This library adopts the convention that works against the real xrouter:
bytes map 1:1 to code points U+0000–U+00FF (Latin-1) and everything
non-ASCII is `\u00XX`-escaped, which
[round-trips byte-perfectly over real RF-path AX.25](https://github.com/M0LTE/rhp2lib-net/blob/main/tests/RhpV2.Client.IntegrationTests/Ax25OverAxudpTests.cs)
(see `Binary_Bytes_Round_Trip_Via_Dgram_Through_Real_Xrouter`).  But
nothing in the published papers says this is *the* convention, and the
cost is real: six wire bytes per high byte means a worst-case binary
payload inflates 6:1 — under a 65 535-byte frame cap and an ~8 KB
`data` ceiling.  Compressed FBB forwarding, YAPP, anything 8-bit hits
this.

**Suggestion:** two parts.  For v2.1, write the existing code-point
convention into the spec as normative, so all implementations agree.
Alongside it, add an optional `"enc": "b64"` field on
`send`/`sendto`/`recv` (negotiated via `hello`) — base64 is 1.33:1
instead of 6:1, and removes the encoding ambiguity entirely.

### 6. A failed connect never says why

An AX.25 `connect` to an unreachable station returns
`connectReply`/`"Ok"` immediately (correctly — the handshake is
asynchronous), and then, after FRACK × N retries, the failure arrives
as a bare `status` with `flags: 0` followed by a `close` notification.
No reason is given.  Retry-exhausted, DM received, busy, link reset —
AX.25 has distinct failure modes, and applications genuinely want them:
a BBS forwarding scheduler treats "busy, try later" very differently
from "no such route".  Notably, `errCode: 15` ("No Route") already
exists in the spec and would be a natural fit, but doesn't appear on
this path.

**Suggestion:** an optional `reason` field (numeric, reusing the
existing error-code table where it fits) on server-initiated `status`
and `close` notifications.

### 7. Socket handles are global, not per-connection

Handles are allocated from a single pool inside xrouter, and — as the
[integration suite pins](https://github.com/M0LTE/rhp2lib-net/blob/main/tests/RhpV2.Client.IntegrationTests/RealXRouterTests.cs)
(`Handles_Are_Globally_Numbered_Across_Connections`) — a handle
allocated on one RHP connection can be *operated on* from another.
With a single trusted client this is harmless; with several clients on
one node (a BBS, a chat server, and a monitor, say) it means any of
them can close or send on the others' sockets, by accident or
otherwise, by using small integers.

**Suggestion:** track the owning RHP connection per handle and reject
operations from elsewhere (`errCode: 3` fits).  Per-connection handle
*numbering* would be the v3 version, but ownership enforcement alone
removes the sharp edge compatibly.

### 8. One failed `auth` wedges the whole connection

After a rejected `auth`, every subsequent request on that TCP
connection — whatever its `type` — is answered with
`authReply`/`errCode: 14`.  The only recovery is to reconnect.  A
mistyped password in an interactive client costs the user their whole
session, and (in our case) it dictated test-harness design: each
error-path test needs a fresh connection.

**Suggestion:** a failed `auth` fails that request; the client may
retry `auth` on the same connection.

### 9. Two complete lifecycles do the same job

The combined `open` and the BSD-style
`socket`/`bind`/`listen`/`connect` path are alternative ways to reach
identical states.  Supporting both doubles the message catalogue, the
documentation, the test surface, and the room for divergence — and
empirically the divergence is real: most of the quirks on this page
(`connectReply` mirroring, the dgram-`listen` rejection, the inet bind
nondeterminism) live on the BSD path.  The only thing the long way
genuinely adds is being able to tell a bind failure from a connect
failure.

**Suggestion:** bless one path (the combined `open` seems the natural
keeper, perhaps with an optional richer error report) and document the
other as legacy.  Even just declaring which path is canonical would
help future implementers.

### 10. Flow control is advisory, and arrives after the fact

The `BUSY` flag — in async `status` pushes and in `sendReply.status` —
tells a client the transmit queue is saturated, but only after the
sends that saturated it have been issued.  A client pipelining sends at
TCP speed into a 1200-baud link can overrun the queue before the first
`BUSY` arrives, and (per observation 3) the overflow may then vanish
silently.  There's no window, no advertised queue depth, no hard
contract for how much may be outstanding.

**Suggestion (v3-sized):** credit-based flow control — `openReply`
grants an initial byte budget, the server tops it up with small
`credit` notifications as the queue drains, and a client never sends
beyond its credit.  This is the standard solution wherever a fast
transport feeds a slow link (SSH channels, HTTP/2, AMQP), and packet
radio is about as extreme a fast-feeds-slow boundary as exists.

### 11. Notes on the security model

Three things worth flagging, all about the TCP/WebSocket side (where —
unlike the RF side — encryption and real credentials are legal):

* **RFC1918 is not a trust boundary.**  CGNAT ranges, shared LANs,
  hotel/club networks and container networks all put strangers inside
  10/8 and 192.168/16.  An explicit allowlist (the `ACCESS.SYS`
  mechanism) is a stronger default than implicit RFC1918 trust.
* **The WebSocket endpoint needs an `Origin` check.**  The same-origin
  policy does *not* stop a web page from opening a WebSocket to
  `ws://node:9000/rhp` — any site visited by a browser on the LAN
  could drive the node (and its transmitter) unless the server
  validates the `Origin` header.  Worth doing before the WS endpoint
  sees wide use.
* **Plaintext `auth` is a stated, reasonable tradeoff** for the
  amateur context — but a simple challenge-response (server sends a
  nonce, client returns a hash) would cost little and avoid replayable
  credentials on the wire, and TLS on the TCP listener is always an
  option for internet-exposed nodes.

### 12. `port` and special addresses live outside the spec

The `port` field is, in practice, an opaque server-defined identifier:
on xrouter and BPQ it's a radio port number, but a single-interface
packet engine (a soundmodem, say) has nothing natural to put there, and
the spec doesn't say what a server should do with a null or omitted
`port`.  In xrouter, `port` `"0"` (or omitting it) means "all ports"
for datagram and raw binds, while NetRom streams don't use it at all —
none of which is written down.  There's also at least one magic
address: a `remote` of `SWITCH` connects to a node's command
interpreter.  That's a node convention rather than part of RHP (it has
no meaning on a pure packet engine, and BPQ ties its null-`port`
handling to it), which is exactly why it belongs in the spec as a
*non-normative* note — an implementer can't currently learn it exists
without reading other people's source.

**Suggestion:** define `port` as an opaque, server-defined string;
specify null/omitted-`port` behaviour per mode; and add a non-normative
appendix listing well-known conventions (`SWITCH`, port `"0"`) so
implementations can interoperate with nodes without reverse-engineering
them.

Observations 13–15 arrived later, and from a different angle.  By
mid-2026 a *second* server-side implementation of RHPv2 existed — pdn,
a packet node from the same stable as this library — and wire-diffing
it against the pinned xrouter container
(`ghcr.io/packethacking/xrouter`, image label 505c) and against
[RHPTEST](protocol.md#intended-behaviour-per-rhptest), the protocol
author's own test harness, surfaced a class of issue the first pass
couldn't see: places where the protocol's two de-facto authorities —
the author's harness and the author's shipping binary — disagree with
*each other*.

### 13. A second `listen` is an error in RHPTEST but Ok on the wire

RHPTEST says a listener socket rejects everything except `accept` and
`close` with error 16 — explicitly including a second `listen` on the
same socket (the rule is recorded in the
[primer](protocol.md#intended-behaviour-per-rhptest)).  The live
container observably does something else: a second `listen` on an
already-listening socket answers `errCode: 0` / `"Ok"`, idempotently,
with no double registration and no other visible effect.

RHPTEST v1 was tested against XRouter v505d; the container is labelled
505c, one build earlier.  So either the behaviour changed between 505c
and 505d, or RHPTEST documents intent the binary doesn't deliver — and
from outside there's no way to tell which.  For an implementer this is
a genuinely new situation: everywhere else on this page, when paper
and wire disagreed, the wire was the arbiter; here the tie is between
two artefacts from the same author.  pdn currently matches the
observed wire (idempotent Ok) and will move to the intended semantics
the moment they're confirmed.

**Suggestion:** a one-line ruling from the author settles the intent;
a 505d container would settle the empirics — the packethacking
containerisation can take a 505d binary as soon as one is available.

### 14. `send` on a listener: 17 on the wire, 16 in RHPTEST

The same class of conflict, one message over.  RHPTEST's
listener-rejects-everything rule has `send` on a listening socket
returning 16 ("Operation not supported"); the live 505c container
answers 17 ("Not connected").

The two codes tell an application different stories.  17 says "this
socket could carry data but isn't connected yet" — reasonable for a
stream mid-handshake, misleading for a listener, which will never be
connected.  pdn sided with RHPTEST's 16, on the reasoning that a
listener is not a not-yet-connected stream — but that's a judgement
call, flagged here for confirmation rather than asserted.

**Suggestion:** the ruling asked for in observation 13 could cover
this too — ideally one normative sentence on listener-socket
semantics, taking in `send`, `sendto`, `connect` and re-`listen`
together.

### 15. Two listeners can claim one callsign, and nobody says who wins

A second socket — on the same RHP connection or a different one — can
`listen` on a callsign another socket has already claimed, and the
live wire answers `errCode: 0` / `"Ok"`.  Nothing in the spec or in
RHPTEST then defines which listener receives the next inbound
connect; accept routing between the two registrations is undefined,
and neither client can detect that it has entered the ambiguous
state.

Unlike observations 13 and 14, this doesn't read like an intent
question at all — it looks defect-shaped.  Happily the spec already
owns the natural fix: `errCode: 9` ("Duplicate socket") is sitting in
the published table and reads as though it was minted for exactly
this case.  A deterministic refusal is strictly more useful than an
Ok with undefined routing, and it's what pdn does.

**Suggestion:** a normative ruling — `errCode: 9` on the second
`listen` (the natural pick), or, if coexisting listeners are actually
intended, defined semantics for who gets the next `accept`.

## A hypothetical v2.1 (backwards compatible)

Everything here can ship without breaking a single deployed client:

| # | Change | Fixes observation |
|---|--------|-------------------|
| 1 | Every `id`-carrying request gets exactly one `id`-echoing reply | 1 |
| 2 | `connectReply.errCode = 0` on success | 2 |
| 3 | Oversized `send` → `errCode: 13`, never silence | 3 |
| 4 | Optional `hello`/`helloReply` with version, capabilities, `maxData` | 4 |
| 5 | Normative byte ↔ code-point mapping for `data`; optional `enc:"b64"` | 5 |
| 6 | Optional `reason` on server-initiated `status`/`close` | 6 |
| 7 | Handle ownership enforcement across RHP connections | 7 |
| 8 | Failed `auth` fails the request, not the connection | 8 |
| 9 | Document the published errata (casing, `port` types, TRACE fields, `errCode: 17`) as normative | — |
| 10 | One normative ruling on listener sockets: re-`listen`, `send`-on-listener, duplicate claims | 13, 14, 15 |

Item 9 deserves a sentence: this library's
[integration suite](https://github.com/M0LTE/rhp2lib-net/tree/main/tests/RhpV2.Client.IntegrationTests)
is in effect a machine-checkable errata document for PWP-0222 — several
tests are deliberately written to *fail when xrouter changes
behaviour*, so a v2.1 spec could largely be transcribed from it, and
conformance of future builds checked against it.

## A hypothetical v3 (breaking, someday, maybe)

If a breaking revision is ever on the table, the v2.1 list plus:

* Keep JSON and the length-prefix framing — readability is the
  protocol's superpower; CBOR would buy little.
* Base64 `data` everywhere; drop the code-point convention.
* One lifecycle: keep `open`/`accept`, retire the BSD message family.
* `id` required on every request; a uniform reply envelope
  (`type`, `id`, `errCode`, `errText`, then payload fields) with no
  field that means different things on success and failure.
* Credit-based flow control (observation 10).
* Per-connection handle namespaces.
* Token or challenge-response auth; `Origin` validation on WebSocket;
  optional TLS.

## Closing

None of this is a request so much as a record: these are the points
where an independent implementation had to stop and experiment rather
than read.  RHPv2 already does the most important thing a protocol can
do — it exists, it works, and real applications talk to real radios
through it.  The hope is that this page makes the next implementer's
evening even shorter, and gives the protocol's author useful raw
material if and when a revision happens.

Discussion welcome — the OARC `#packet` channels or this repo's
[issue tracker](https://github.com/M0LTE/rhp2lib-net/issues) are both
good places.
