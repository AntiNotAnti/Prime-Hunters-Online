# Selective reliable control (protocol 17, unreleased)

Only low-rate admission, session/roster, ownership/map, lobby commands/results,
load reports, disconnect/refusal and match-end events are reliable. Intent,
SlotIntent, Snapshot, Ping and Pong remain disposable. Each reliable payload has a
uint event ID after the normal sequenced header. Retransmits get new datagram
sequences; any acknowledged attempt completes its event. An old ACK cannot complete
a different event that reused a sent-ring index.

There are 32 ordinary pending slots and eight reserved critical slots, 40 total.
Identical outstanding publications share an event ID. Distinct events are never
coalesced. Retry intervals are 150/300/600/1200 ms, capped at 1200, with a 15-second
connection failure deadline. Payloads are owned copies, allocated only for control
events. Critical capacity failure disconnects the affected connection through the
simulation inbox. Removed peers retain only the bounded retiring transport state
needed to finish a kick/refusal, until ACK or expiry.

Receiver history is 256 IDs, independent of the 32-packet ACK bitmap. Sender event
span is bounded to that history while any older event is outstanding. Out-of-order
control delivery is supported; application revisions/lifecycle identity remain
mandatory. A receiver only acknowledges a new event when it can retain its
application. ACKs piggyback, with idle ACK-only packets for reliable completion.

`--reliable` uses the production channel, datagram state and seeded fault scheduler
under 5% loss, 80 ms jitter, 3% reordering and 1% duplication. It checks exactly-once
application, retry completion, reserves, dedup span and bounded failure.
