package observe

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestHTTPMetricsBoundedAndPrivate(t *testing.T) {
	m := New()
	h := m.HTTP(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(429); w.Write([]byte("limited")) }))
	r := httptest.NewRequest("GET", "/private-room-secret?password=private-password", nil)
	r.Header.Set("Authorization", "Bearer private-token")
	h.ServeHTTP(httptest.NewRecorder(), r)
	c, g := m.Snapshot()
	if c["http_429"] != 1 || c["http_response_bytes"] != 7 || g["http_handlers"] != 0 {
		t.Fatal(c, g)
	}
	raw, _ := json.Marshal(c)
	if strings.Contains(string(raw), "private") {
		t.Fatal("sensitive data recorded")
	}
	c, _ = m.Snapshot()
	if len(c) != 0 {
		t.Fatal("counts not reset")
	}
}
