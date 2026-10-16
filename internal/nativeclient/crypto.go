package nativeclient

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base32"
	"encoding/base64"
	"errors"
	"golang.org/x/crypto/hkdf"
	"io"
	"strings"
)

type Crypto struct {
	Room        string
	media, chat cipher.AEAD
}

func NewCrypto(key string) (*Crypto, error) {
	raw, e := base64.RawURLEncoding.DecodeString(strings.TrimRight(key, "="))
	if e != nil || len(raw) != 16 {
		return nil, errors.New("invalid room key")
	}
	derive := func(info string) []byte {
		out := make([]byte, 16)
		io.ReadFull(hkdf.New(sha256.New, raw, nil, []byte(info)), out)
		return out
	}
	a, _ := aes.NewCipher(derive("zvonok/media/v1"))
	media, _ := cipher.NewGCM(a)
	b, _ := aes.NewCipher(derive("zvonok/chat/v1"))
	chat, _ := cipher.NewGCM(b)
	return &Crypto{strings.ToLower(base32.StdEncoding.WithPadding(base32.NoPadding).EncodeToString(derive("zvonok/room/v1"))), media, chat}, nil
}
func prefix(data []byte, video bool) int {
	if !video {
		return 1
	}
	if len(data) > 0 && data[0]&1 == 0 {
		return 10
	}
	return 3
}
func (c *Crypto) SealMedia(data []byte, video bool) []byte {
	n := prefix(data, video)
	if len(data) <= n {
		return nil
	}
	iv := make([]byte, 12)
	if _, e := rand.Read(iv); e != nil {
		return nil
	}
	out := append([]byte{}, data[:n]...)
	out = c.media.Seal(out, iv, data[n:], data[:n])
	return append(out, iv...)
}
func (c *Crypto) OpenMedia(data []byte, video bool) ([]byte, error) {
	n := prefix(data, video)
	if len(data) < n+28 {
		return nil, errors.New("truncated encrypted frame")
	}
	return c.media.Open(append([]byte{}, data[:n]...), data[len(data)-12:], data[n:len(data)-12], data[:n])
}
func (c *Crypto) SealChat(data []byte) string {
	iv := make([]byte, 12)
	if _, e := rand.Read(iv); e != nil {
		return ""
	}
	return base64.StdEncoding.EncodeToString(c.chat.Seal(iv, iv, data, nil))
}
func (c *Crypto) OpenChat(s string) ([]byte, error) {
	raw, e := base64.StdEncoding.DecodeString(s)
	if e != nil || len(raw) < 28 {
		return nil, errors.New("invalid chat")
	}
	return c.chat.Open(nil, raw[:12], raw[12:], nil)
}
