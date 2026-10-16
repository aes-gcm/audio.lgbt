package server

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestClientAssetsRevalidateTogether(t *testing.T) {
	handler := noCacheHTML(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusOK) }))
	for _, path := range []string{"/", "/index.html", "/app.js", "/recent-rooms.js", "/style.css", "/e2ee-worker.js", "/download.html", "/download.js", "/downloads/windows-x64.json", "/downloads/audio.lgbt-Setup.exe", "/downloads/audio.lgbt-Setup.exe.sha256"} {
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, httptest.NewRequest("GET", path, nil))
		if w.Header().Get("Cache-Control") != "no-cache" {
			t.Errorf("%s can become stale", path)
		}
	}
}

func TestDownloadPageServedAtCleanURL(t *testing.T) {
	for _, method := range []string{"GET", "HEAD"} {
		w := httptest.NewRecorder()
		serveDownload(w, httptest.NewRequest(method, "/download", nil))
		if w.Code != http.StatusOK || w.Header().Get("Cache-Control") != "no-cache" {
			t.Fatalf("%s /download: status=%d cache=%q", method, w.Code, w.Header().Get("Cache-Control"))
		}
		if method == "GET" && !strings.Contains(w.Body.String(), "/downloads/audio.lgbt-Setup.exe") {
			t.Fatal("download page missing branded installer")
		}
	}
}
