package app

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/api"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/blob"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/config"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/disk"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/hub"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/ratelimit"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/retention"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/service"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/webui"
)

type Options struct {
	Config            config.Config
	Version           string
	Clock             clock.Clock
	Rand              io.Reader
	Logger            *slog.Logger
	DiskFree          disk.FreeFunc
	Hub               hub.Config
	RetentionInterval time.Duration
	DiskCheckInterval time.Duration
	UploadTTL         time.Duration
	WebUI             http.Handler
}

type App struct {
	Config  config.Config
	Store   *store.Store
	Blobs   *blob.Store
	Disk    *disk.Guard
	Hub     *hub.Hub
	Service *service.Service
	Handler http.Handler

	log               *slog.Logger
	retentionInterval time.Duration
	diskInterval      time.Duration
}

func New(ctx context.Context, o Options) (*App, error) {
	cfg := o.Config
	if o.Clock == nil {
		o.Clock = clock.Real{}
	}
	if o.Logger == nil {
		o.Logger = slog.Default()
	}
	if o.Version == "" {
		o.Version = "1.0.0"
	}
	if o.RetentionInterval <= 0 {
		o.RetentionInterval = 15 * time.Minute
	}
	if o.DiskCheckInterval <= 0 {
		o.DiskCheckInterval = time.Minute
	}
	if err := os.MkdirAll(cfg.DataDir, 0o700); err != nil {
		return nil, fmt.Errorf("create data dir %s: %w", cfg.DataDir, err)
	}
	probe := filepath.Join(cfg.DataDir, ".write-test")
	if err := os.WriteFile(probe, []byte("ok"), 0o600); err != nil {
		return nil, fmt.Errorf("data dir %s is not writable: %w", cfg.DataDir, err)
	}
	os.Remove(probe)

	st, err := store.Open(ctx, filepath.Join(cfg.DataDir, "yikz-clipboard.db"))
	if err != nil {
		return nil, err
	}
	blobs, err := blob.Open(cfg.DataDir)
	if err != nil {
		st.Close()
		return nil, err
	}
	guard := disk.NewGuard(disk.Options{
		Path:         cfg.DataDir,
		MinFreeBytes: cfg.MinFreeDiskBytes,
		Free:         o.DiskFree,
		Clock:        o.Clock,
		Logger:       o.Logger,
	})
	hubCfg := o.Hub
	hubCfg.ServerVersion = o.Version
	h := hub.New(hubCfg, o.Clock, o.Logger)
	svc, err := service.New(service.Options{
		Settings: service.Settings{
			Username:         cfg.Username,
			Password:         cfg.Password,
			ServerVersion:    o.Version,
			RetentionDays:    cfg.RetentionDays,
			StorageMaxBytes:  cfg.StorageMaxBytes,
			PinnedMaxBytes:   cfg.PinnedMaxBytes,
			MinFreeDiskBytes: cfg.MinFreeDiskBytes,
			UploadTTL:        o.UploadTTL,
		},
		Store:   st,
		Blobs:   blobs,
		Disk:    guard,
		Events:  h,
		Limiter: ratelimit.New(10, 10*time.Minute, o.Clock),
		Clock:   o.Clock,
		Rand:    o.Rand,
		Logger:  o.Logger,
	})
	if err != nil {
		st.Close()
		return nil, err
	}
	guard.OnChange(func(s disk.Status) { h.Broadcast(svc.StorageWarning(s)) })
	if err := svc.Init(ctx); err != nil {
		st.Close()
		return nil, err
	}
	guard.Refresh()

	ui := o.WebUI
	if ui == nil {
		w, err := webui.New()
		if err != nil {
			st.Close()
			return nil, err
		}
		ui = w
	}
	handler := api.New(api.Options{
		Service:        svc,
		Hub:            h,
		WebUI:          ui,
		TrustedProxies: cfg.TrustedProxies,
		Logger:         o.Logger,
		ServerVersion:  o.Version,
	})
	return &App{
		Config:            cfg,
		Store:             st,
		Blobs:             blobs,
		Disk:              guard,
		Hub:               h,
		Service:           svc,
		Handler:           handler,
		log:               o.Logger,
		retentionInterval: o.RetentionInterval,
		diskInterval:      o.DiskCheckInterval,
	}, nil
}

func (a *App) Close() error {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := a.Store.Checkpoint(ctx); err != nil {
		a.log.Warn("wal checkpoint", "err", err)
	}
	return a.Store.Close()
}

func (a *App) StartBackground(ctx context.Context) {
	go retention.Schedule(ctx, a.Service, a.retentionInterval, a.log)
	go a.Disk.Run(ctx, a.diskInterval)
	go func() {
		if err := a.Service.RunRetention(ctx, ""); err != nil && ctx.Err() == nil {
			a.log.Error("initial retention run failed", "err", err)
		}
	}()
}

func (a *App) Run(ctx context.Context) error {
	ln, err := net.Listen("tcp", a.Config.Listen)
	if err != nil {
		return fmt.Errorf("listen on %s: %w", a.Config.Listen, err)
	}
	return a.Serve(ctx, ln)
}

func (a *App) Serve(ctx context.Context, ln net.Listener) error {
	bgCtx, cancelBg := context.WithCancel(ctx)
	defer cancelBg()
	a.StartBackground(bgCtx)

	srv := &http.Server{
		Handler:           a.Handler,
		ReadHeaderTimeout: 15 * time.Second,
		IdleTimeout:       120 * time.Second,
		MaxHeaderBytes:    64 << 10,
		ErrorLog:          slog.NewLogLogger(a.log.Handler(), slog.LevelWarn),
	}
	errCh := make(chan error, 1)
	go func() { errCh <- srv.Serve(ln) }()
	a.log.Info("server listening", "addr", ln.Addr().String(), "data_dir", a.Config.DataDir)

	select {
	case err := <-errCh:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
	}
	a.log.Info("shutting down")
	timeout := a.Config.ShutdownTimeout
	if timeout <= 0 {
		timeout = 15 * time.Second
	}
	shCtx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	cancelBg()
	hubDone := make(chan struct{})
	go func() {
		a.Hub.Shutdown(shCtx)
		close(hubDone)
	}()
	err := srv.Shutdown(shCtx)
	<-hubDone
	if err != nil && !errors.Is(err, http.ErrServerClosed) {
		return fmt.Errorf("shutdown: %w", err)
	}
	a.log.Info("shutdown complete")
	return nil
}
