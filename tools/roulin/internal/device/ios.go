package device

import (
	"fmt"
	"io"
	"os/exec"
	"strings"
	"time"
)

// listIOS returns the set of connected iOS device UDIDs (via libimobiledevice's idevice_id).
func listIOS() map[string]bool {
	out, err := exec.Command("idevice_id", "-l").Output()
	if err != nil {
		return map[string]bool{}
	}
	ids := map[string]bool{}
	for _, line := range strings.Split(strings.TrimSpace(string(out)), "\n") {
		if line != "" {
			ids[line] = true
		}
	}
	return ids
}

// Iproxy manages a running iproxy process that forwards localhost:port → device:port.
type Iproxy struct {
	cmd  *exec.Cmd
	done chan struct{}
}

// IproxyStart launches iproxy and returns a handle to manage its lifecycle.
func IproxyStart(port int) *Iproxy {
	pp := fmt.Sprintf("%d", port)
	cmd := exec.Command("iproxy", pp, pp)
	h := &Iproxy{cmd: cmd, done: make(chan struct{})}
	if err := cmd.Start(); err != nil {
		close(h.done)
		return h
	}
	go func() {
		cmd.Wait()
		close(h.done)
	}()
	return h
}

func (h *Iproxy) Alive() bool {
	select {
	case <-h.done:
		return false
	default:
		return true
	}
}

func (h *Iproxy) Kill() {
	if h.cmd.Process != nil {
		h.cmd.Process.Kill()
	}
	<-h.done
}

// IOSTarget pairs USB-connected iOS devices via iproxy (libimobiledevice).
type IOSTarget struct {
	pairPort int
	iproxy   *Iproxy
	prevIDs  map[string]bool
}

func NewIOSTarget(pairPort int) *IOSTarget {
	return &IOSTarget{pairPort: pairPort, prevIDs: map[string]bool{}}
}

func (t *IOSTarget) Name() string { return "iOS" }

func (t *IOSTarget) Detect(out io.Writer) bool {
	ids := listIOS()
	for id := range ids {
		if !t.prevIDs[id] {
			fmt.Fprintf(out, "[%s] iOS connected:    %s\n", nowStamp(), id)
		}
	}
	for id := range t.prevIDs {
		if !ids[id] {
			fmt.Fprintf(out, "[%s] iOS disconnected: %s\n", nowStamp(), id)
		}
	}
	t.prevIDs = ids
	return len(ids) > 0
}

func (t *IOSTarget) Setup() error {
	if t.iproxy == nil || !t.iproxy.Alive() {
		if t.iproxy != nil {
			t.iproxy.Kill()
		}
		t.iproxy = IproxyStart(t.pairPort)
		// Brief wait for iproxy's TCP listener to come up before the
		// first Send. Empirically 300ms is enough on macOS.
		time.Sleep(300 * time.Millisecond)
	}
	return nil
}

func (t *IOSTarget) Teardown() {
	if t.iproxy != nil {
		t.iproxy.Kill()
		t.iproxy = nil
	}
}

func (t *IOSTarget) Send(out io.Writer, addr, revision string, deadline time.Time) bool {
	if Send(addr, revision, t.pairPort, deadline) {
		fmt.Fprintf(out, "[%s] iOS paired → %s\n", nowStamp(), addr)
		return true
	}
	return false
}
