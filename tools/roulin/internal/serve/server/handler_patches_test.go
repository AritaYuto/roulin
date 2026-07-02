package server

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/sse"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

// POST /patches body validation: missing platform / empty changes / non-hex.
func TestPatches_RejectsBadBody(t *testing.T) {
	dir := t.TempDir()
	broadcaster := sse.New()
	store := local.NewFile(dir)
	srv := New(store, &Writer{
		Storage:     store,
		Broadcaster: broadcaster,
	}, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)

	cases := []struct {
		name string
		body PatchEvent
	}{
		{"missing platform", PatchEvent{
			Changes: []PatchChange{{Address: "a/icon", NewBlobHex: strings.Repeat("ab", 32)}},
		}},
		{"empty changes", PatchEvent{Platform: "WindowsPlayer"}},
		{"empty address", PatchEvent{
			Platform: "WindowsPlayer",
			Changes:  []PatchChange{{Address: "", NewBlobHex: strings.Repeat("ab", 32)}},
		}},
		{"short hash", PatchEvent{
			Platform: "WindowsPlayer",
			Changes:  []PatchChange{{Address: "a/icon", NewBlobHex: "ab"}},
		}},
		{"non-hex", PatchEvent{
			Platform: "WindowsPlayer",
			Changes:  []PatchChange{{Address: "a/icon", NewBlobHex: strings.Repeat("zz", 32)}},
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			postPatches(t, ts, &tc.body, http.StatusBadRequest)
		})
	}
}

func postPatches(t *testing.T, ts *httptest.Server, body *PatchEvent, wantStatus int) {
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
