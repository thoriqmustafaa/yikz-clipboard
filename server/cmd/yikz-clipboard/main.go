package main

import (
	"context"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/app"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/config"
)

var version = "1.0.0"

func main() {
	if len(os.Args) > 1 {
		switch os.Args[1] {
		case "healthcheck":
			os.Exit(healthcheck())
		case "version", "--version", "-v":
			fmt.Println(version)
			return
		case "serve":
		default:
			fmt.Fprintf(os.Stderr, "usage: %s [serve|healthcheck|version]\n", os.Args[0])
			os.Exit(2)
		}
	}
	os.Exit(serve())
}

func serve() int {
	cfg, err := config.Load()
	if err != nil {
		fmt.Fprintf(os.Stderr, "invalid configuration:\n%v\n", err)
		return 1
	}
	log := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: cfg.LogLevel}))
	slog.SetDefault(log)

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	a, err := app.New(ctx, app.Options{Config: cfg, Version: version, Logger: log})
	if err != nil {
		log.Error("startup failed", "err", err)
		return 1
	}
	defer a.Close()
	log.Info("yikz-clipboard starting", "version", version, "retention_days", cfg.RetentionDays,
		"storage_max_bytes", cfg.StorageMaxBytes, "pinned_max_bytes", cfg.PinnedMaxBytes, "min_free_disk_bytes", cfg.MinFreeDiskBytes)
	if err := a.Run(ctx); err != nil {
		log.Error("server stopped with error", "err", err)
		return 1
	}
	return 0
}

func healthcheck() int {
	listen := os.Getenv("CC_LISTEN")
	if listen == "" {
		listen = ":8080"
	}
	client := &http.Client{Timeout: 3 * time.Second}
	resp, err := client.Get(config.HealthcheckURL(listen))
	if err != nil {
		fmt.Fprintln(os.Stderr, "healthcheck failed:", err)
		return 1
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		fmt.Fprintln(os.Stderr, "healthcheck failed: status", resp.StatusCode)
		return 1
	}
	return 0
}
