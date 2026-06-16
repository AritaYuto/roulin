package device

import (
	"fmt"
	"io"
	"time"
)

// HostTarget delivers pair to a loopback TCP listener.
// Detect always returns true — Send handles the silent-failure case when nothing is listening.
type HostTarget struct {
	pairPort   int
	prevPaired bool
}

func NewHostTarget(pairPort int) *HostTarget {
	return &HostTarget{pairPort: pairPort}
}

func (t *HostTarget) Name() string { return "Host" }

func (t *HostTarget) Detect(_ io.Writer) bool { return true }

func (t *HostTarget) Setup() error { return nil }

func (t *HostTarget) Teardown() {}

func (t *HostTarget) Send(out io.Writer, addr, revision string, deadline time.Time) bool {
	if Send(addr, revision, t.pairPort, deadline) {
		if !t.prevPaired {
			fmt.Fprintf(out, "[%s] Host paired → %s\n", nowStamp(), addr)
		}
		t.prevPaired = true
		return true
	}
	t.prevPaired = false
	return false
}
