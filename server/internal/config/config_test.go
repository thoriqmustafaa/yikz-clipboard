package config

import (
	"strings"
	"testing"
)

func lookup(m map[string]string) func(string) (string, bool) {
	return func(k string) (string, bool) {
		v, ok := m[k]
		return v, ok
	}
}

func TestDefaults(t *testing.T) {
	cfg, err := FromLookup(lookup(map[string]string{"CC_USERNAME": "thoriq", "CC_PASSWORD": "login-password-example"}))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Listen != ":8080" || cfg.DataDir != "/data" || cfg.RetentionDays != 7 {
		t.Fatalf("unexpected defaults: %+v", cfg)
	}
	if cfg.StorageMaxBytes != 5368709120 || cfg.PinnedMaxBytes != 1073741824 || cfg.MinFreeDiskBytes != 2147483648 {
		t.Fatalf("unexpected size defaults: %+v", cfg)
	}
	if len(cfg.TrustedProxies) == 0 {
		t.Fatal("no trusted proxies")
	}
	if cfg.ReleaseToken != "" || cfg.ReleasesKeep != 5 {
		t.Fatalf("unexpected release defaults: %+v", cfg)
	}
}

func TestReleaseConfig(t *testing.T) {
	cfg, err := FromLookup(lookup(map[string]string{"CC_USERNAME": "u", "CC_PASSWORD": "12345678", "CC_RELEASE_TOKEN": " secret-token ", "CC_RELEASES_KEEP": "3"}))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ReleaseToken != "secret-token" || cfg.ReleasesKeep != 3 {
		t.Fatalf("unexpected: %+v", cfg)
	}
	if _, err := FromLookup(lookup(map[string]string{"CC_USERNAME": "u", "CC_PASSWORD": "12345678", "CC_RELEASES_KEEP": "0"})); err == nil || !strings.Contains(err.Error(), "CC_RELEASES_KEEP") {
		t.Fatalf("expected CC_RELEASES_KEEP error, got %v", err)
	}
}

func TestOverrides(t *testing.T) {
	cfg, err := FromLookup(lookup(map[string]string{
		"CC_USERNAME": "u", "CC_PASSWORD": "12345678", "CC_LISTEN": "127.0.0.1:9000", "CC_DATA_DIR": "/tmp/x",
		"CC_RETENTION_DAYS": "3", "CC_STORAGE_MAX_GB": "0.5", "CC_PINNED_MAX_GB": "0.25", "CC_MIN_FREE_DISK_GB": "0",
		"CC_TRUSTED_PROXIES": "10.1.2.3, 192.168.0.0/24", "CC_LOG_LEVEL": "debug",
	}))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.StorageMaxBytes != 1<<29 || cfg.PinnedMaxBytes != 1<<28 || cfg.MinFreeDiskBytes != 0 || cfg.RetentionDays != 3 {
		t.Fatalf("unexpected: %+v", cfg)
	}
	if len(cfg.TrustedProxies) != 2 || cfg.TrustedProxies[0].Bits() != 32 {
		t.Fatalf("proxies: %v", cfg.TrustedProxies)
	}
}

func TestValidationErrors(t *testing.T) {
	_, err := FromLookup(lookup(map[string]string{
		"CC_PASSWORD": "short", "CC_LISTEN": "nope", "CC_RETENTION_DAYS": "0", "CC_STORAGE_MAX_GB": "x",
		"CC_PINNED_MAX_GB": "-1", "CC_TRUSTED_PROXIES": "999.1.1.1", "CC_LOG_LEVEL": "loud",
	}))
	if err == nil {
		t.Fatal("expected error")
	}
	for _, want := range []string{"CC_USERNAME", "CC_PASSWORD", "CC_LISTEN", "CC_RETENTION_DAYS", "CC_STORAGE_MAX_GB", "CC_PINNED_MAX_GB", "CC_TRUSTED_PROXIES", "CC_LOG_LEVEL"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("error does not mention %s: %v", want, err)
		}
	}
	_, err = FromLookup(lookup(map[string]string{"CC_USERNAME": "u", "CC_PASSWORD": "12345678", "CC_STORAGE_MAX_GB": "1", "CC_PINNED_MAX_GB": "2"}))
	if err == nil || !strings.Contains(err.Error(), "must not exceed") {
		t.Fatalf("expected pinned > storage error, got %v", err)
	}
}

func TestHealthcheckURL(t *testing.T) {
	cases := map[string]string{
		":8080":          "http://127.0.0.1:8080/healthz",
		"0.0.0.0:9000":   "http://127.0.0.1:9000/healthz",
		"[::]:8080":      "http://127.0.0.1:8080/healthz",
		"127.0.0.1:8081": "http://127.0.0.1:8081/healthz",
	}
	for in, want := range cases {
		if got := HealthcheckURL(in); got != want {
			t.Errorf("HealthcheckURL(%q) = %s, want %s", in, got, want)
		}
	}
}
