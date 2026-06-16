package device

import (
	"fmt"
	"net"
	"time"
)

const DefaultPairPort = 12765

// LocalIP returns the first non-loopback IPv4 address on this machine.
func LocalIP() (string, error) {
	addrs, err := net.InterfaceAddrs()
	if err != nil {
		return "", err
	}
	for _, a := range addrs {
		ipNet, ok := a.(*net.IPNet)
		if !ok {
			continue
		}
		if ip := ipNet.IP.To4(); ip != nil && !ip.IsLoopback() {
			return ip.String(), nil
		}
	}
	return "", fmt.Errorf("no non-loopback IPv4 address found")
}

// Send dials localhost:port and writes the pairing payload, retrying until deadline.
// Wire format: "<addr>\n<revision>\n" — revision may be empty.
func Send(addr, revision string, port int, deadline time.Time) bool {
	target := fmt.Sprintf("localhost:%d", port)
	for time.Now().Before(deadline) {
		conn, err := net.DialTimeout("tcp", target, time.Second)
		if err != nil {
			time.Sleep(500 * time.Millisecond)
			continue
		}
		conn.SetDeadline(time.Now().Add(2 * time.Second))
		_, err = fmt.Fprintf(conn, "%s\n%s\n", addr, revision)
		conn.Close()
		if err == nil {
			return true
		}
	}
	return false
}
