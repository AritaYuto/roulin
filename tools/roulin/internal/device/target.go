// Package device provides pair targets — USB-connected mobile devices and
// host loopback — used by `roulin-cli watch` to deliver server addr and
// revision hints to receiver-side pairing listeners.
package device

import (
	"io"
	"time"
)

// Target is a pair-able destination (USB device or host loopback).
// Each implementation owns detection, port forwarding, and pair delivery.
type Target interface {
	Name() string
	Detect(out io.Writer) bool
	Setup() error
	Teardown()
	Send(out io.Writer, addr, revision string, deadline time.Time) bool
}

// nowStamp is the timestamp prefix used in transition logs across all targets.
func nowStamp() string {
	return time.Now().Format("15:04:05")
}
