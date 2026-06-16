// Package sse provides a server-sent events fan-out broadcaster.
package sse

import "sync"

// Broadcaster is a fan-out hub: every Subscribe call returns a channel that
// receives all subsequent Broadcast payloads. Slow subscribers are dropped
// per-broadcast (their buffered channel is bypassed for that one message)
// to keep the broadcaster responsive — no head-of-line blocking.
type Broadcaster struct {
	mu      sync.Mutex
	clients map[chan []byte]struct{}
}

func New() *Broadcaster {
	return &Broadcaster{clients: make(map[chan []byte]struct{})}
}

// Subscribe registers a new client and returns its channel. Caller must call
// Unsubscribe to release resources; channel is buffered (cap 8) so short
// bursts don't drop.
func (b *Broadcaster) Subscribe() chan []byte {
	ch := make(chan []byte, 8)
	b.mu.Lock()
	b.clients[ch] = struct{}{}
	b.mu.Unlock()
	return ch
}

// Unsubscribe removes the client and closes its channel. Idempotent.
func (b *Broadcaster) Unsubscribe(ch chan []byte) {
	b.mu.Lock()
	if _, ok := b.clients[ch]; ok {
		delete(b.clients, ch)
		close(ch)
	}
	b.mu.Unlock()
}

// Broadcast delivers payload to every subscriber. Non-blocking per-client:
// if the subscriber's channel buffer is full, this message is dropped for
// that subscriber.
func (b *Broadcaster) Broadcast(payload []byte) {
	b.mu.Lock()
	defer b.mu.Unlock()
	for ch := range b.clients {
		select {
		case ch <- payload:
		default:
			// drop; subscriber is slow
		}
	}
}

// ClientCount returns the current subscriber count (diagnostics).
func (b *Broadcaster) ClientCount() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return len(b.clients)
}
