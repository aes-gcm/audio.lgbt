package server

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestDownloadsUseGitHubWithoutBundledInstaller(t *testing.T) {
	w := httptest.NewRecorder()
	downloadHandler("").ServeHTTP(w, httptest.NewRequest("GET", "/downloads/audio.lgbt-Setup.exe", nil))
	if w.Code != http.StatusTemporaryRedirect || !strings.HasPrefix(w.Header().Get("Location"), "https://github.com/aes-gcm/audio.lgbt-windows/releases/download/") {
		t.Fatal("installer must come from GitHub")
	}
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "audio.lgbt-0.3.0-preview.7-x64.exe"), []byte("legacy mirror fixture"), 0600); err != nil {
		t.Fatal(err)
	}
	w = httptest.NewRecorder()
	downloadHandler(dir).ServeHTTP(w, httptest.NewRequest("GET", "/downloads/audio.lgbt-0.3.0-preview.7-x64.exe", nil))
	if w.Code != 200 || w.Body.String() != "legacy mirror fixture" {
		t.Fatal("older clients need a direct download during migration")
	}
	w = httptest.NewRecorder()
	downloadHandler(dir).ServeHTTP(w, httptest.NewRequest("GET", "/downloads/nested/audio.lgbt-0.3.0-preview.7-x64.exe", nil))
	if w.Code != 404 {
		t.Fatal("nested mirror paths must be rejected")
	}
}

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
		if method == "GET" && !strings.Contains(w.Body.String(), "github.com/aes-gcm/audio.lgbt-windows/releases/download/") {
			t.Fatal("download page missing branded installer")
		}
	}
}
