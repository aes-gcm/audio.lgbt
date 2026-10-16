package accounts

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"

	"golang.org/x/crypto/bcrypt"
)

const CookieName = "audio_session"
const sessionLifetime = 12 * time.Hour

var usernamePattern = regexp.MustCompile(`^[a-zA-Z0-9_.-]{1,64}$`)

type File struct {
	Users map[string]string `json:"users"`
}
type session struct {
	Username, Hash string
	Expires        time.Time
}
type attempt struct {
	Count int
	Since time.Time
}
type Store struct {
	path             string
	required, secure bool
	mu               sync.Mutex
	sessions         map[[32]byte]session
	attempts         map[string]attempt
	slots            chan struct{}
	dummy            []byte
}

func Read(path string) (*File, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	var data File
	if err = json.NewDecoder(io.LimitReader(f, 1<<20)).Decode(&data); err != nil {
		return nil, err
	}
	if data.Users == nil {
		return nil, errors.New("accounts file must contain users")
	}
	for name, hash := range data.Users {
		cost, err := bcrypt.Cost([]byte(hash))
		if !usernamePattern.MatchString(name) || err != nil || cost < 10 || cost > 14 {
			return nil, errors.New("invalid username or password hash in accounts file")
		}
	}
	return &data, nil
}

func SetUser(path, name, password string, remove bool) error {
	if !usernamePattern.MatchString(name) {
		return errors.New("username: 1–64 letters, digits, dots, underscores or hyphens")
	}
	data, err := Read(path)
	if os.IsNotExist(err) {
		data = &File{Users: map[string]string{}}
	} else if err != nil {
		return err
	}
	if remove {
		delete(data.Users, name)
	} else {
		if len(password) < 12 || len(password) > 72 {
			return errors.New("password must contain 12–72 bytes")
		}
		hash, err := bcrypt.GenerateFromPassword([]byte(password), 12)
		if err != nil {
			return err
		}
		data.Users[name] = string(hash)
	}
	raw, err := json.MarshalIndent(data, "", "  ")
	if err != nil {
		return err
	}
	if err = os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	f, err := os.CreateTemp(filepath.Dir(path), ".accounts-*")
	if err != nil {
		return err
	}
	tmp := f.Name()
	defer os.Remove(tmp)
	if err = f.Chmod(0600); err == nil {
		_, err = f.Write(append(raw, '\n'))
	}
	if err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	return os.Rename(tmp, path)
}

func New(path string, required, secure bool) (*Store, error) {
	if required {
		data, err := Read(path)
		if err != nil {
			return nil, fmt.Errorf("load accounts: %w", err)
		}
		if len(data.Users) == 0 {
			return nil, errors.New("restricted server requires at least one account")
		}
	}
	dummy, err := bcrypt.GenerateFromPassword([]byte("not-a-real-account-password"), 12)
	if err != nil {
		return nil, err
	}
	return &Store{path: path, required: required, secure: secure, sessions: map[[32]byte]session{}, attempts: map[string]attempt{}, slots: make(chan struct{}, 4), dummy: dummy}, nil
}

func (s *Store) token(r *http.Request) string {
	if value := r.Header.Get("Authorization"); strings.HasPrefix(value, "Bearer ") {
		return strings.TrimPrefix(value, "Bearer ")
	}
	if cookie, err := r.Cookie(CookieName); err == nil {
		return cookie.Value
	}
	return ""
}
func (s *Store) Username(r *http.Request) string {
	token := s.token(r)
	if len(token) != 43 {
		return ""
	}
	key := sha256.Sum256([]byte(token))
	s.mu.Lock()
	item, ok := s.sessions[key]
	s.mu.Unlock()
	if !ok || time.Now().After(item.Expires) {
		return ""
	}

	data, err := Read(s.path)
	if err != nil || data.Users[item.Username] != item.Hash {
		return ""
	}
	return item.Username
}
func (s *Store) CanCreate(r *http.Request) bool { return s.Username(r) != "" }
func (s *Store) Status(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		w.WriteHeader(405)
		return
	}
	respond(w, 200, map[string]any{"authenticationRequired": s.required, "username": s.Username(r)})
}
func sameOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true
	}
	u, err := url.Parse(origin)
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	return err == nil && u.Scheme == scheme && strings.EqualFold(u.Host, r.Host) && u.User == nil && u.Path == "" && u.RawQuery == "" && u.Fragment == ""
}
func validMutation(w http.ResponseWriter, r *http.Request) bool {
	if r.Method != "POST" {
		w.WriteHeader(405)
		return false
	}
	media, _, _ := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if !sameOrigin(r) || media != "application/json" {
		respond(w, 403, map[string]string{"error": "Запрос отклонён."})
		return false
	}
	return true
}
func (s *Store) allow(ip string) bool {
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	for key, a := range s.attempts {
		if now.Sub(a.Since) > time.Minute {
			delete(s.attempts, key)
		}
	}
	a := s.attempts[ip]
	if a.Since.IsZero() {
		if len(s.attempts) >= 4096 {
			return false
		}
		a.Since = now
	}
	a.Count++
	s.attempts[ip] = a
	return a.Count <= 8
}
func (s *Store) Login(w http.ResponseWriter, r *http.Request) {
	if !validMutation(w, r) {
		return
	}
	if !s.required {
		respond(w, 400, map[string]string{"error": "На этом сервере вход не требуется."})
		return
	}
	ip, _, _ := net.SplitHostPort(r.RemoteAddr)
	if !s.allow(ip) {
		w.Header().Set("Retry-After", "60")
		respond(w, 429, map[string]string{"error": "Слишком много попыток. Подождите минуту."})
		return
	}
	select {
	case s.slots <- struct{}{}:
		defer func() { <-s.slots }()
	default:
		respond(w, 429, map[string]string{"error": "Подождите и попробуйте снова."})
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	var input struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if decoder.Decode(&input) != nil || decoder.Decode(new(any)) != io.EOF || len(input.Password) > 72 || !usernamePattern.MatchString(input.Username) {
		respond(w, 400, map[string]string{"error": "Проверьте логин и пароль."})
		return
	}
	data, err := Read(s.path)
	if err != nil {
		respond(w, 503, map[string]string{"error": "Учётные записи временно недоступны."})
		return
	}
	hash, exists := data.Users[input.Username]
	check := []byte(hash)
	if !exists {
		check = s.dummy
	}
	err = bcrypt.CompareHashAndPassword(check, []byte(input.Password))
	if err != nil || !exists {
		respond(w, 401, map[string]string{"error": "Неверный логин или пароль."})
		return
	}
	var random [32]byte
	if _, err = rand.Read(random[:]); err != nil {
		w.WriteHeader(500)
		return
	}
	token := base64.RawURLEncoding.EncodeToString(random[:])
	now := time.Now()
	s.mu.Lock()
	for key, item := range s.sessions {
		if now.After(item.Expires) {
			delete(s.sessions, key)
		}
	}
	if old := s.token(r); old != "" {
		delete(s.sessions, sha256.Sum256([]byte(old)))
	}
	if len(s.sessions) >= 4096 {
		s.mu.Unlock()
		respond(w, 503, map[string]string{"error": "Сервер занят. Попробуйте позже."})
		return
	}
	s.sessions[sha256.Sum256([]byte(token))] = session{input.Username, hash, now.Add(sessionLifetime)}
	s.mu.Unlock()
	s.cookie(w, token, int(sessionLifetime.Seconds()))
	respond(w, 200, map[string]string{"username": input.Username})
}
func (s *Store) Logout(w http.ResponseWriter, r *http.Request) {
	if !validMutation(w, r) {
		return
	}
	s.mu.Lock()
	delete(s.sessions, sha256.Sum256([]byte(s.token(r))))
	s.mu.Unlock()
	s.cookie(w, "", -1)
	respond(w, 200, map[string]bool{"ok": true})
}
func (s *Store) cookie(w http.ResponseWriter, token string, age int) {
	http.SetCookie(w, &http.Cookie{Name: CookieName, Value: token, Path: "/", HttpOnly: true, Secure: s.secure, SameSite: http.SameSiteStrictMode, MaxAge: age})
}
func respond(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
