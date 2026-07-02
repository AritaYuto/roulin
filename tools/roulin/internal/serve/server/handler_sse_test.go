package server

import (
	"bufio"
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/sse"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

// POST /patches relays its body verbatim to every SSE subscriber. POST
// /parcels (full-build channel) is intentionally NOT broadcast — verified
// separately.
func TestSse_BroadcastOnPostPatches(t *testing.T) {
	dir := t.TempDir()
	broadcaster := sse.New()
	store := local.NewFile(dir)
	srv := New(store, &Writer{
		Storage:     store,
		Broadcaster: broadcaster,
	}, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)

	reader := openSseStream(t, ts, broadcaster)

	body := PatchEvent{
		Platform: "WindowsPlayer",
		Changes: []PatchChange{
			{
				Address:    "a/icon",
				NewBlobHex: strings.Repeat("ab", 32),
			},
		},
	}
	postPatches(t, ts, &body, http.StatusOK)

	got := readDataLine(t, reader, time.Second)
	want := `{"platform":"WindowsPlayer","changes":[{"address":"a/icon","new_blob_hex":"` +
		strings.Repeat("ab", 32) + `"}]}`
	if got != want {
		t.Fatalf("event payload = %q\n want %q", got, want)
	}
}

// POST /parcels must not produce an SSE event — full builds are not
// hot-reload. Post one and confirm the stream stays quiet for 300ms.
func TestSse_PostParcelDoesNotBroadcast(t *testing.T) {
	dir := t.TempDir()
	broadcaster := sse.New()
	store := local.NewFile(dir)
	srv := New(store, &Writer{
		Storage:     store,
		Broadcaster: broadcaster,
	}, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)

	reader := openSseStream(t, ts, broadcaster)

	postParcel(t, ts, "rev-no-broadcast", &build.Parcel{}, http.StatusCreated)

	dataCh := make(chan string, 1)
	errCh := make(chan error, 1)
	go func() {
		for {
			line, err := reader.ReadString('\n')
			if err != nil {
				errCh <- err
				return
			}
			if strings.HasPrefix(line, "data: ") {
				dataCh <- strings.TrimSpace(line)
				return
			}
		}
	}()
	select {
	case got := <-dataCh:
		t.Fatalf("unexpected SSE data line after POST /parcels: %q", got)
	case <-errCh:
		// stream closed early, treat as no data — also acceptable
	case <-time.After(300 * time.Millisecond):
		// expected: silence
	}
}

func TestSse_UnsubscribeIdempotent(t *testing.T) {
	broadcaster := sse.New()
	channel := broadcaster.Subscribe()
	if broadcaster.ClientCount() != 1 {
		t.Fatalf("ClientCount = %d, want 1", broadcaster.ClientCount())
	}
	broadcaster.Unsubscribe(channel)
	broadcaster.Unsubscribe(channel) // should not panic
	if broadcaster.ClientCount() != 0 {
		t.Fatalf("ClientCount = %d, want 0", broadcaster.ClientCount())
	}
}

// Saturated subscriber doesn't block the broadcaster.
func TestSse_SlowSubscriberIsDropped(t *testing.T) {
	broadcaster := sse.New()
	channel := broadcaster.Subscribe()
	t.Cleanup(func() { broadcaster.Unsubscribe(channel) })

	for i := 0; i < 8; i++ {
		broadcaster.Broadcast([]byte("x"))
	}
	done := make(chan struct{})
	go func() {
		broadcaster.Broadcast([]byte("dropped"))
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(200 * time.Millisecond):
		t.Fatal("Broadcast blocked on slow subscriber")
	}
}

// openSseStream connects, drains the initial `retry:` hint, and waits until
// the broadcaster registers the client. Returns a reader positioned at the
// first event line.
func openSseStream(t *testing.T, ts *httptest.Server, broadcaster *sse.Broadcaster) *bufio.Reader {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, ts.URL+"/watch/changes", nil)
	resp, err := ts.Client().Do(req)
	if err != nil {
		t.Fatalf("subscribe: %v", err)
	}
	t.Cleanup(func() { _ = resp.Body.Close() })
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	if got := resp.Header.Get("Content-Type"); got != "text/event-stream" {
		t.Fatalf("Content-Type = %q, want text/event-stream", got)
	}

	reader := bufio.NewReader(resp.Body)
	// Drain "retry: 2000\n" + "\n"
	if _, err := reader.ReadString('\n'); err != nil {
		t.Fatalf("read retry: %v", err)
	}
	if _, err := reader.ReadString('\n'); err != nil {
		t.Fatalf("read sep: %v", err)
	}

	deadline := time.Now().Add(200 * time.Millisecond)
	for broadcaster.ClientCount() == 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if broadcaster.ClientCount() == 0 {
		t.Fatal("client did not register on broadcaster")
	}
	return reader
}

func readDataLine(t *testing.T, reader *bufio.Reader, timeout time.Duration) string {
	t.Helper()
	dataCh := make(chan string, 1)
	errCh := make(chan error, 1)
	go func() {
		for {
			line, err := reader.ReadString('\n')
			if err != nil {
				errCh <- err
				return
			}
			if strings.HasPrefix(line, "data: ") {
				dataCh <- strings.TrimSuffix(strings.TrimPrefix(line, "data: "), "\n")
				return
			}
		}
	}()
	select {
	case got := <-dataCh:
		return got
	case err := <-errCh:
		t.Fatalf("read sse: %v", err)
	case <-time.After(timeout):
		t.Fatal("timed out waiting for sse event")
	}
	return ""
}
