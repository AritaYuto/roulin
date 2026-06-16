package roulin_test

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/server"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/sse"
)

// POST /patches relays its body verbatim to every SSE subscriber. POST /parcels
// (full-build channel) is intentionally NOT broadcast — verified separately.
func TestSse_BroadcastOnPostPatches(t *testing.T) {
	dir := t.TempDir()
	bc := sse.New()
	st := local.NewFile(dir)
	srv := server.New(st, &server.Writer{
		Storage:     st,
		Broadcaster: bc,
	}, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)

	reader := openSseStream(t, ts, bc)

	// Trigger a relay broadcast via POST /patches.
	body := server.PatchEvent{
		Platform: "WindowsPlayer",
		Changes: []server.PatchChange{
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

// POST /parcels must not produce an SSE event — full builds are not hot-reload.
// We post one and then prove the stream stays quiet for 300ms.
func TestSse_PostParcelDoesNotBroadcast(t *testing.T) {
	dir := t.TempDir()
	bc := sse.New()
	st := local.NewFile(dir)
	srv := server.New(st, &server.Writer{
		Storage:     st,
		Broadcaster: bc,
	}, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)

	reader := openSseStream(t, ts, bc)

	postParcel(t, ts, "rev-no-broadcast", &build.Parcel{}, http.StatusCreated)

	// Expect NO data line within 300ms — POST /parcels is silent on SSE.
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

// Subscribe + Unsubscribe is idempotent.
func TestSse_UnsubscribeIdempotent(t *testing.T) {
	bc := sse.New()
	ch := bc.Subscribe()
	if bc.ClientCount() != 1 {
		t.Fatalf("ClientCount = %d, want 1", bc.ClientCount())
	}
	bc.Unsubscribe(ch)
	bc.Unsubscribe(ch) // should not panic
	if bc.ClientCount() != 0 {
		t.Fatalf("ClientCount = %d, want 0", bc.ClientCount())
	}
}

// Saturated subscriber doesn't block the broadcaster.
func TestSse_SlowSubscriberIsDropped(t *testing.T) {
	bc := sse.New()
	ch := bc.Subscribe()
	t.Cleanup(func() { bc.Unsubscribe(ch) })

	for i := 0; i < 8; i++ {
		bc.Broadcast([]byte("x"))
	}
	done := make(chan struct{})
	go func() {
		bc.Broadcast([]byte("dropped"))
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(200 * time.Millisecond):
		t.Fatal("Broadcast blocked on slow subscriber")
	}
}

// POST /patches body validation: missing platform / empty changes / non-hex.
func TestPatches_RejectsBadBody(t *testing.T) {
	dir := t.TempDir()
	bc := sse.New()
	st := local.NewFile(dir)
	srv := server.New(st, &server.Writer{
		Storage:     st,
		Broadcaster: bc,
	}, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)

	cases := []struct {
		name string
		body server.PatchEvent
	}{
		{"missing platform", server.PatchEvent{
			Changes: []server.PatchChange{{Address: "a/icon", NewBlobHex: strings.Repeat("ab", 32)}},
		}},
		{"empty changes", server.PatchEvent{Platform: "WindowsPlayer"}},
		{"empty address", server.PatchEvent{
			Platform: "WindowsPlayer",
			Changes:  []server.PatchChange{{Address: "", NewBlobHex: strings.Repeat("ab", 32)}},
		}},
		{"short hash", server.PatchEvent{
			Platform: "WindowsPlayer",
			Changes:  []server.PatchChange{{Address: "a/icon", NewBlobHex: "ab"}},
		}},
		{"non-hex", server.PatchEvent{
			Platform: "WindowsPlayer",
			Changes:  []server.PatchChange{{Address: "a/icon", NewBlobHex: strings.Repeat("zz", 32)}},
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			postPatches(t, ts, &c.body, http.StatusBadRequest)
		})
	}
}

// ---- helpers --------------------------------------------------------------

// openSseStream connects, drains the initial `retry:` hint, and waits until
// the broadcaster registers the client. Returns a reader positioned at the
// first event line.
func openSseStream(t *testing.T, ts *httptest.Server, bc *sse.Broadcaster) *bufio.Reader {
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
	for bc.ClientCount() == 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if bc.ClientCount() == 0 {
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

func postPatches(t *testing.T, ts *httptest.Server, body *server.PatchEvent, wantStatus int) {
	t.Helper()
	buf, err := json.Marshal(body)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	resp, err := http.Post(ts.URL+"/patches", "application/json", bytes.NewReader(buf))
	if err != nil {
		t.Fatalf("post patches: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != wantStatus {
		t.Fatalf("status = %d, want %d", resp.StatusCode, wantStatus)
	}
}
