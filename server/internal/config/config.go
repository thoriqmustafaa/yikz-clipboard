package config

import (
	"errors"
	"fmt"
	"log/slog"
	"math"
	"net"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"time"
)

const GiB = 1 << 30

const DefaultTrustedProxies = "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,fc00::/7"

type Config struct {
	Username         string
	Password         string
	Listen           string
	DataDir          string
	RetentionDays    int
	StorageMaxBytes  int64
	PinnedMaxBytes   int64
	MinFreeDiskBytes int64
	TrustedProxies   []netip.Prefix
	LogLevel         slog.Level
	ShutdownTimeout  time.Duration
}

func Load() (Config, error) {
	return FromLookup(os.LookupEnv)
}

func FromLookup(lookup func(string) (string, bool)) (Config, error) {
	get := func(key, def string) string {
		if v, ok := lookup(key); ok && strings.TrimSpace(v) != "" {
			return strings.TrimSpace(v)
		}
		return def
	}
	var errs []error
	cfg := Config{ShutdownTimeout: 15 * time.Second}

	cfg.Username = get("CC_USERNAME", "")
	if cfg.Username == "" {
		errs = append(errs, errors.New("CC_USERNAME is required"))
	}
	if v, ok := lookup("CC_PASSWORD"); ok {
		cfg.Password = v
	}
	switch {
	case cfg.Password == "":
		errs = append(errs, errors.New("CC_PASSWORD is required"))
	case len(cfg.Password) < 8:
		errs = append(errs, errors.New("CC_PASSWORD must be at least 8 characters"))
	}

	cfg.Listen = get("CC_LISTEN", ":8080")
	if _, _, err := net.SplitHostPort(cfg.Listen); err != nil {
		errs = append(errs, fmt.Errorf("CC_LISTEN %q is not a valid host:port: %v", cfg.Listen, err))
	}

	cfg.DataDir = get("CC_DATA_DIR", "/data")

	days, err := strconv.Atoi(get("CC_RETENTION_DAYS", "7"))
	if err != nil || days < 1 || days > 36500 {
		errs = append(errs, fmt.Errorf("CC_RETENTION_DAYS must be an integer between 1 and 36500"))
	}
	cfg.RetentionDays = days

	cfg.StorageMaxBytes, err = parseGiB(get("CC_STORAGE_MAX_GB", "5"), false)
	if err != nil {
		errs = append(errs, fmt.Errorf("CC_STORAGE_MAX_GB: %v", err))
	}
	cfg.PinnedMaxBytes, err = parseGiB(get("CC_PINNED_MAX_GB", "1"), true)
	if err != nil {
		errs = append(errs, fmt.Errorf("CC_PINNED_MAX_GB: %v", err))
	}
	cfg.MinFreeDiskBytes, err = parseGiB(get("CC_MIN_FREE_DISK_GB", "2"), true)
	if err != nil {
		errs = append(errs, fmt.Errorf("CC_MIN_FREE_DISK_GB: %v", err))
	}
	if cfg.StorageMaxBytes > 0 && cfg.PinnedMaxBytes > cfg.StorageMaxBytes {
		errs = append(errs, errors.New("CC_PINNED_MAX_GB must not exceed CC_STORAGE_MAX_GB"))
	}

	cfg.TrustedProxies, err = ParsePrefixes(get("CC_TRUSTED_PROXIES", DefaultTrustedProxies))
	if err != nil {
		errs = append(errs, fmt.Errorf("CC_TRUSTED_PROXIES: %v", err))
	}

	switch strings.ToLower(get("CC_LOG_LEVEL", "info")) {
	case "debug":
		cfg.LogLevel = slog.LevelDebug
	case "info":
		cfg.LogLevel = slog.LevelInfo
	case "warn", "warning":
		cfg.LogLevel = slog.LevelWarn
	case "error":
		cfg.LogLevel = slog.LevelError
	default:
		errs = append(errs, errors.New("CC_LOG_LEVEL must be one of debug, info, warn, error"))
	}

	if len(errs) > 0 {
		return Config{}, errors.Join(errs...)
	}
	return cfg, nil
}

func parseGiB(s string, allowZero bool) (int64, error) {
	v, err := strconv.ParseFloat(s, 64)
	if err != nil || math.IsNaN(v) || math.IsInf(v, 0) {
		return 0, fmt.Errorf("%q is not a number", s)
	}
	if v < 0 || (!allowZero && v == 0) {
		return 0, fmt.Errorf("%q must be positive", s)
	}
	if v > 1<<20 {
		return 0, fmt.Errorf("%q is too large", s)
	}
	return int64(math.Round(v * GiB)), nil
}

func ParsePrefixes(s string) ([]netip.Prefix, error) {
	var out []netip.Prefix
	for _, part := range strings.Split(s, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		if strings.Contains(part, "/") {
			p, err := netip.ParsePrefix(part)
			if err != nil {
				return nil, fmt.Errorf("invalid prefix %q", part)
			}
			out = append(out, p.Masked())
			continue
		}
		a, err := netip.ParseAddr(part)
		if err != nil {
			return nil, fmt.Errorf("invalid address %q", part)
		}
		out = append(out, netip.PrefixFrom(a.Unmap(), a.Unmap().BitLen()))
	}
	return out, nil
}

func HealthcheckURL(listen string) string {
	host, port, err := net.SplitHostPort(listen)
	if err != nil {
		return "http://127.0.0.1:8080/healthz"
	}
	switch host {
	case "", "0.0.0.0", "::", "[::]":
		host = "127.0.0.1"
	}
	return "http://" + net.JoinHostPort(host, port) + "/healthz"
}
