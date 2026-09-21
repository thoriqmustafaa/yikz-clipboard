package api

import (
	"net/http/httptest"
	"net/netip"
	"net/url"
	"testing"
)

func TestClientIP(t *testing.T) {
	a := &API{proxies: []netip.Prefix{netip.MustParsePrefix("127.0.0.0/8"), netip.MustParsePrefix("172.16.0.0/12")}}
	cases := []struct {
		remote, xff, want string
	}{
		{"203.0.113.9:1234", "1.2.3.4", "203.0.113.9"},
		{"127.0.0.1:1234", "", "127.0.0.1"},
		{"127.0.0.1:1234", "198.51.100.7", "198.51.100.7"},
		{"127.0.0.1:1234", "6.6.6.6, 198.51.100.7, 172.18.0.1", "198.51.100.7"},
		{"127.0.0.1:1234", "garbage", "127.0.0.1"},
	}
	for _, c := range cases {
		r := httptest.NewRequest("GET", "/", nil)
		r.RemoteAddr = c.remote
		if c.xff != "" {
			r.Header.Set("X-Forwarded-For", c.xff)
		}
		if got := a.clientIP(r); got != c.want {
			t.Errorf("clientIP(%s, %q) = %s, want %s", c.remote, c.xff, got, c.want)
		}
	}
}

func TestRedactedURI(t *testing.T) {
	u, _ := url.Parse("/ws?token=yc_secret&x=1")
	if got := RedactedURI(u); got != "/ws?token=REDACTED&x=1" {
		t.Fatalf("got %s", got)
	}
}
