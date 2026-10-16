package accounts

import (
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestAccountSessions(t *testing.T) {
	path := filepath.Join(t.TempDir(), "accounts.json")
	if err := SetUser(path, "admin", "a-test-password-123", false); err != nil {
		t.Fatal(err)
	}
	store, err := New(path, true, true)
	if err != nil {
		t.Fatal(err)
	}
	login := func(body, origin string) *httptest.ResponseRecorder {
		r := httptest.NewRequest("POST", "https://call.example/api/login", strings.NewReader(body))
		r.Header.Set("Content-Type", "application/json")
		r.Header.Set("Origin", origin)
		w := httptest.NewRecorder()
		store.Login(w, r)
		return w
	}
	if w := login(`{"username":"admin","password":"a-test-password-123"}`, "https://evil.example"); w.Code != 403 {
		t.Fatal("cross-origin login accepted")
	}
	if w := login(`{"username":"admin","password":"wrong"}`, "https://call.example"); w.Code != 401 {
		t.Fatal("wrong password accepted")
	}
	if w := login(`{"username":"unknown","password":"wrong"}`, "https://call.example"); w.Code != 401 {
		t.Fatal("unknown user disclosed")
	}
	w := login(`{"username":"admin","password":"a-test-password-123"}`, "https://call.example")
	if w.Code != 200 {
		t.Fatal(w.Body.String())
	}
	cookie := w.Result().Cookies()[0]
	if !cookie.HttpOnly || !cookie.Secure || cookie.SameSite != http.SameSiteStrictMode || cookie.Path != "/" {
		t.Fatal("unsafe cookie")
	}
	r := httptest.NewRequest("GET", "https://call.example/ws", nil)
	r.AddCookie(cookie)
	if store.Username(r) != "admin" {
		t.Fatal("cookie session rejected")
	}
	native := httptest.NewRequest("GET", "https://call.example/ws", nil)
	native.Header.Set("Authorization", "Bearer "+cookie.Value)
	if store.Username(native) != "admin" {
		t.Fatal("native bearer rejected")
	}
	restarted, _ := New(path, true, true)
	if restarted.CanCreate(r) {
		t.Fatal("session survived restart")
	}
	store.mu.Lock()
	for key, item := range store.sessions {
		item.Expires = time.Now().Add(-time.Second)
		store.sessions[key] = item
	}
	store.mu.Unlock()
	if store.CanCreate(r) {
		t.Fatal("expired session accepted")
	}
	w = login(`{"username":"admin","password":"a-test-password-123"}`, "https://call.example")
	r = httptest.NewRequest("GET", "https://call.example/ws", nil)
	r.AddCookie(w.Result().Cookies()[0])
	if err := SetUser(path, "admin", "new-test-password-456", false); err != nil {
		t.Fatal(err)
	}
	if store.CanCreate(r) {
		t.Fatal("password change did not revoke session")
	}
	w = login(`{"username":"admin","password":"new-test-password-456"}`, "https://call.example")
	cookie = w.Result().Cookies()[0]
	r = httptest.NewRequest("POST", "https://call.example/api/logout", strings.NewReader(`{}`))
	r.Header.Set("Content-Type", "application/json")
	r.AddCookie(cookie)
	store.Logout(httptest.NewRecorder(), r)
	if store.CanCreate(r) {
		t.Fatal("logout failed")
	}
	if err := SetUser(path, "admin", "", true); err != nil {
		t.Fatal(err)
	}
	if _, err := New(path, true, true); err == nil {
		t.Fatal("restricted server started without accounts")
	}
}
func TestBoundedLoginAndFiles(t *testing.T) {
	path := filepath.Join(t.TempDir(), "accounts.json")
	if err := SetUser(path, "../bad", "long-test-password", false); err == nil {
		t.Fatal("invalid username")
	}
	if err := SetUser(path, "admin", "short", false); err == nil {
		t.Fatal("short password")
	}
	if _, err := New(path, true, false); err == nil {
		t.Fatal("missing file accepted")
	}
	s, _ := New(path, false, false)
	for i := 0; i < 8; i++ {
		if !s.allow("ip") {
			t.Fatal("rate limit too early")
		}
	}
	if s.allow("ip") {
		t.Fatal("rate limit missing")
	}
	r := httptest.NewRequest("POST", "http://call.example/api/login", strings.NewReader(`{}`))
	w := httptest.NewRecorder()
	s.Login(w, r)
	if w.Code != 403 {
		t.Fatal("form POST accepted")
	}
}
