package cli

import (
	"fmt"
	"time"

	"github.com/spf13/cobra"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/device"
)

const hostPairPort = 12766
const sendDeadline = time.Second

var watchCmd = &cobra.Command{
	Use:   "watch",
	Short: "Watch for pair targets and auto-pair with roulin-server",
	Long: `Continuously polls for pair targets (USB-connected iOS/Android
devices and the host loopback for engine editors / standalone PC builds) and
sends the roulin-server address whenever a target appears or restarts.

Requires:
  iOS:     iproxy + idevice_id  (brew install libimobiledevice)
  Android: adb                   (brew install android-platform-tools)`,
	RunE: runWatch,
}

var (
	watchFlagPort     int
	watchFlagPairPort int
	watchFlagHost     string
	watchFlagInterval int
	watchFlagUSB      bool
	watchFlagRevision string
)

func init() {
	watchCmd.Flags().IntVar(&watchFlagPort, "port", 8765, "roulin-server port")
	watchCmd.Flags().IntVar(&watchFlagPairPort, "pair-port", device.DefaultPairPort, "Pairing handshake port (USB device)")
	watchCmd.Flags().StringVar(&watchFlagHost, "host", "", "Host IP (auto-detected if empty)")
	watchCmd.Flags().IntVar(&watchFlagInterval, "interval", 3, "Poll interval in seconds")
	watchCmd.Flags().BoolVar(&watchFlagUSB, "usb", false, "Android: use adb reverse so the game connects via USB (localhost) instead of WiFi — useful when VPN blocks the host IP")
	watchCmd.Flags().StringVarP(&watchFlagRevision, "revision", "r", "", "Optional revision hint sent to the game (cached in persistentDataPath; cleared via the receiver's pairing API)")
}

func runWatch(cmd *cobra.Command, _ []string) error {
	host := watchFlagHost
	if host == "" {
		var err error
		host, err = device.LocalIP()
		if err != nil {
			return fmt.Errorf("cannot detect local IP: %w\n  Use --host to specify", err)
		}
	}

	addr := fmt.Sprintf("http://%s:%d", host, watchFlagPort)
	out := cmd.OutOrStdout()

	fmt.Fprintf(out, "roulin watch started\n  server:    %s\n  pair-port: %d\n  interval:  %ds\n  usb:       %v\n  revision:  %s\n\n",
		addr, watchFlagPairPort, watchFlagInterval, watchFlagUSB, revisionDisplay(watchFlagRevision))

	targets := []device.Target{
		device.NewIOSTarget(watchFlagPairPort),
		device.NewAndroidTarget(watchFlagPairPort, watchFlagPort, watchFlagUSB),
		device.NewHostTarget(hostPairPort),
	}

	for {
		for _, t := range targets {
			if t.Detect(out) {
				_ = t.Setup()
				t.Send(out, addr, watchFlagRevision, time.Now().Add(sendDeadline))
			} else {
				t.Teardown()
			}
		}
		time.Sleep(time.Duration(watchFlagInterval) * time.Second)
	}
}

func revisionDisplay(rev string) string {
	if rev == "" {
		return "(none)"
	}
	return rev
}
