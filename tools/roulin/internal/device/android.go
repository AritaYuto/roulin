package device

import (
	"fmt"
	"io"
	"os/exec"
	"strings"
	"time"
)

// listAndroid returns the set of connected Android device serials (via adb).
func listAndroid() map[string]bool {
	out, err := exec.Command("adb", "devices").Output()
	if err != nil {
		return map[string]bool{}
	}
	ids := map[string]bool{}
	for _, line := range strings.Split(string(out), "\n")[1:] {
		line = strings.TrimRight(line, "\r\n ")
		if strings.HasSuffix(line, "\tdevice") {
			ids[strings.Fields(line)[0]] = true
		}
	}
	return ids
}

// AndroidTarget pairs USB-connected Android devices via adb forward.
// When useUSB is true, additionally sets up adb reverse so the game connects
// back to the host through the USB tunnel (helpful in VPN environments where
// the device can't reach the host's WiFi IP).
type AndroidTarget struct {
	pairPort, serverPort int
	useUSB               bool
	forwarding, reversed bool
	prevIDs              map[string]bool
}

func NewAndroidTarget(pairPort, serverPort int, useUSB bool) *AndroidTarget {
	return &AndroidTarget{
		pairPort:   pairPort,
		serverPort: serverPort,
		useUSB:     useUSB,
		prevIDs:    map[string]bool{},
	}
}

func (t *AndroidTarget) Name() string { return "Android" }

func (t *AndroidTarget) Detect(out io.Writer) bool {
	ids := listAndroid()
	for id := range ids {
		if !t.prevIDs[id] {
			fmt.Fprintf(out, "[%s] Android connected:    %s\n", nowStamp(), id)
		}
	}
	for id := range t.prevIDs {
		if !ids[id] {
			fmt.Fprintf(out, "[%s] Android disconnected: %s\n", nowStamp(), id)
		}
	}
	t.prevIDs = ids
	return len(ids) > 0
}

func (t *AndroidTarget) Setup() error {
	pp := fmt.Sprintf("tcp:%d", t.pairPort)
	if !t.forwarding {
		exec.Command("adb", "forward", pp, pp).Run()
		t.forwarding = true
	}
	if t.useUSB && !t.reversed {
		rp := fmt.Sprintf("tcp:%d", t.serverPort)
		exec.Command("adb", "reverse", rp, rp).Run()
		t.reversed = true
	}
	return nil
}

func (t *AndroidTarget) Teardown() {
	if t.forwarding {
		pp := fmt.Sprintf("tcp:%d", t.pairPort)
		exec.Command("adb", "forward", "--remove", pp).Run()
		t.forwarding = false
	}
	if t.reversed {
		rp := fmt.Sprintf("tcp:%d", t.serverPort)
		exec.Command("adb", "reverse", "--remove", rp).Run()
		t.reversed = false
	}
}

func (t *AndroidTarget) Send(out io.Writer, addr, revision string, deadline time.Time) bool {
	sendAddr := addr
	if t.useUSB {
		sendAddr = fmt.Sprintf("http://localhost:%d", t.serverPort)
	}
	if Send(sendAddr, revision, t.pairPort, deadline) {
		fmt.Fprintf(out, "[%s] Android paired → %s\n", nowStamp(), sendAddr)
		return true
	}
	return false
}
