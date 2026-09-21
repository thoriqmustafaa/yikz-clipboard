package service

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"errors"
	"net/http"
	"strconv"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/apierr"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/hub"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/ratelimit"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

type LoginRequest struct {
	Username   *string `json:"username"`
	Password   *string `json:"password"`
	DeviceName *string `json:"device_name"`
	Platform   *string `json:"platform"`
	DeviceID   *string `json:"device_id"`
}

type LoginResponse struct {
	DeviceID        string    `json:"device_id"`
	Token           string    `json:"token"`
	Username        string    `json:"username"`
	Salt            string    `json:"salt"`
	KDF             proto.KDF `json:"kdf"`
	KeyCheck        *string   `json:"key_check"`
	ServerID        string    `json:"server_id"`
	ServerVersion   string    `json:"server_version"`
	ProtocolVersion int       `json:"protocol_version"`
}

type MeResponse struct {
	Username        string       `json:"username"`
	Device          proto.Device `json:"device"`
	Salt            string       `json:"salt"`
	KDF             proto.KDF    `json:"kdf"`
	KeyCheck        *string      `json:"key_check"`
	ServerID        string       `json:"server_id"`
	ServerVersion   string       `json:"server_version"`
	ProtocolVersion int          `json:"protocol_version"`
	Limits          proto.Limits `json:"limits"`
}

func equalConstantTime(a, b string) bool {
	ha := sha256.Sum256([]byte(a))
	hb := sha256.Sum256([]byte(b))
	return subtle.ConstantTimeCompare(ha[:], hb[:]) == 1
}

func (s *Service) Authenticate(ctx context.Context, token string) (store.Device, error) {
	if !proto.ValidToken(token) {
		return store.Device{}, apierr.Unauthorized()
	}
	dev, err := s.store.DeviceByTokenHash(ctx, proto.HashToken(token))
	if errors.Is(err, store.ErrNotFound) {
		return store.Device{}, apierr.Unauthorized()
	}
	if err != nil {
		return store.Device{}, err
	}
	return dev, nil
}

func (s *Service) Login(ctx context.Context, req LoginRequest, clientIP string) (LoginResponse, error) {
	if req.Username == nil || *req.Username == "" {
		return LoginResponse{}, apierr.InvalidRequest("username is required")
	}
	if req.Password == nil || *req.Password == "" {
		return LoginResponse{}, apierr.InvalidRequest("password is required")
	}
	if req.DeviceName == nil {
		return LoginResponse{}, apierr.InvalidRequest("device_name is required")
	}
	name, ok := proto.NormalizeDeviceName(*req.DeviceName)
	if !ok {
		return LoginResponse{}, apierr.InvalidRequest("device_name must be 1 to 64 characters without control characters")
	}
	if req.Platform == nil || !proto.ValidPlatform(*req.Platform) {
		return LoginResponse{}, apierr.InvalidRequest("platform must be one of macos, android, windows, web")
	}
	platform := *req.Platform

	if wait, limited := s.limiter.RetryAfter(clientIP); limited {
		return LoginResponse{}, apierr.New(http.StatusTooManyRequests, apierr.CodeRateLimited, "too many failed login attempts").
			WithHeader("Retry-After", strconv.Itoa(ratelimit.Seconds(wait)))
	}
	userOK := equalConstantTime(*req.Username, s.cfg.Username)
	passOK := equalConstantTime(*req.Password, s.cfg.Password)
	if !userOK || !passOK {
		s.limiter.Fail(clientIP)
		s.log.Warn("login failed", "ip", clientIP)
		return LoginResponse{}, apierr.New(http.StatusUnauthorized, apierr.CodeInvalidCreds, "invalid username or password")
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	now := s.clock.Now()
	var deviceID string
	reuse := false
	if req.DeviceID != nil && *req.DeviceID != "" {
		if _, err := s.store.Device(ctx, *req.DeviceID); err == nil {
			deviceID = *req.DeviceID
			reuse = true
		} else if !errors.Is(err, store.ErrNotFound) {
			return LoginResponse{}, err
		}
	}
	if reuse {
		token, hash, err := s.newToken()
		if err != nil {
			return LoginResponse{}, err
		}
		if err := s.store.ReissueDevice(ctx, deviceID, name, platform, hash, now); err != nil {
			return LoginResponse{}, err
		}
		s.events.CloseDevice(deviceID, hub.CloseUnauthorized, "token replaced")
		s.events.RenameDevice(deviceID, name)
		s.forgetTouch(deviceID)
		s.log.Info("device logged in again", "device_id", deviceID, "platform", platform)
		return s.loginResponse(ctx, deviceID, token)
	}
	id, err := s.newUUID()
	if err != nil {
		return LoginResponse{}, err
	}
	token, hash, err := s.newToken()
	if err != nil {
		return LoginResponse{}, err
	}
	dev := store.Device{ID: id, Name: name, Platform: platform, CreatedAt: now, LastSeenAt: now}
	if err := s.store.CreateDevice(ctx, dev, hash); err != nil {
		return LoginResponse{}, err
	}
	s.log.Info("device created", "device_id", id, "platform", platform)
	return s.loginResponse(ctx, id, token)
}

func (s *Service) loginResponse(ctx context.Context, deviceID, token string) (LoginResponse, error) {
	s.events.Broadcast(proto.DevicesChanged{Type: "devices_changed"})
	acct, err := s.store.Account(ctx)
	if err != nil {
		return LoginResponse{}, err
	}
	return LoginResponse{
		DeviceID:        deviceID,
		Token:           token,
		Username:        acct.Username,
		Salt:            proto.EncodeBase64(acct.Salt),
		KDF:             proto.DefaultKDF(),
		KeyCheck:        acct.KeyCheck,
		ServerID:        acct.ServerID,
		ServerVersion:   s.cfg.ServerVersion,
		ProtocolVersion: proto.ProtocolVersion,
	}, nil
}

func (s *Service) deviceJSON(d store.Device, currentID string, online map[string]bool) proto.Device {
	return proto.Device{
		ID:         d.ID,
		Name:       d.Name,
		Platform:   d.Platform,
		CreatedAt:  proto.Time{Time: d.CreatedAt},
		LastSeenAt: proto.Time{Time: d.LastSeenAt},
		Online:     online[d.ID],
		Revoked:    d.Revoked,
		Current:    d.ID == currentID,
	}
}

func (s *Service) Me(ctx context.Context, caller store.Device) (MeResponse, error) {
	acct, err := s.store.Account(ctx)
	if err != nil {
		return MeResponse{}, err
	}
	dev, err := s.store.Device(ctx, caller.ID)
	if err != nil {
		return MeResponse{}, err
	}
	online := map[string]bool{dev.ID: s.events.IsOnline(dev.ID)}
	return MeResponse{
		Username:        acct.Username,
		Device:          s.deviceJSON(dev, caller.ID, online),
		Salt:            proto.EncodeBase64(acct.Salt),
		KDF:             proto.DefaultKDF(),
		KeyCheck:        acct.KeyCheck,
		ServerID:        acct.ServerID,
		ServerVersion:   s.cfg.ServerVersion,
		ProtocolVersion: proto.ProtocolVersion,
		Limits:          proto.DefaultLimits(),
	}, nil
}

func (s *Service) SetKeyCheck(ctx context.Context, value *string) error {
	if value == nil || !proto.ValidHex64(*value) {
		return apierr.InvalidRequest("key_check must be 64 lowercase hex characters")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	acct, err := s.store.Account(ctx)
	if err != nil {
		return err
	}
	if acct.KeyCheck != nil {
		if *acct.KeyCheck == *value {
			return nil
		}
		return apierr.New(http.StatusConflict, apierr.CodeKeyCheckExists, "a different key check is already stored")
	}
	if err := s.store.SetKeyCheck(ctx, *value); err != nil {
		return err
	}
	s.log.Info("key check stored")
	return nil
}

func (s *Service) ResetKeyCheck(ctx context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	n, err := s.store.CountItemsAndPending(ctx)
	if err != nil {
		return err
	}
	if n > 0 {
		return apierr.New(http.StatusConflict, apierr.CodeItemsExist, "delete all items before resetting the key check")
	}
	if err := s.store.ClearKeyCheck(ctx); err != nil {
		return err
	}
	s.log.Info("key check cleared")
	return nil
}

func (s *Service) Devices(ctx context.Context, caller store.Device) ([]proto.Device, error) {
	list, err := s.store.Devices(ctx)
	if err != nil {
		return nil, err
	}
	online := s.events.OnlineIDs()
	out := make([]proto.Device, 0, len(list))
	for _, d := range list {
		out = append(out, s.deviceJSON(d, caller.ID, online))
	}
	return out, nil
}

func (s *Service) RenameDevice(ctx context.Context, caller store.Device, id string, name *string) (proto.Device, error) {
	if name == nil {
		return proto.Device{}, apierr.InvalidRequest("name is required")
	}
	clean, ok := proto.NormalizeDeviceName(*name)
	if !ok {
		return proto.Device{}, apierr.InvalidRequest("name must be 1 to 64 characters without control characters")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.store.RenameDevice(ctx, id, clean); err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return proto.Device{}, apierr.NotFound("device not found")
		}
		return proto.Device{}, err
	}
	d, err := s.store.Device(ctx, id)
	if err != nil {
		return proto.Device{}, err
	}
	s.events.RenameDevice(id, clean)
	s.events.Broadcast(proto.DevicesChanged{Type: "devices_changed"})
	return s.deviceJSON(d, caller.ID, map[string]bool{id: s.events.IsOnline(id)}), nil
}

func (s *Service) DeleteDevice(ctx context.Context, id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	d, err := s.store.Device(ctx, id)
	if errors.Is(err, store.ErrNotFound) {
		return apierr.NotFound("device not found")
	}
	if err != nil {
		return err
	}
	if d.Revoked {
		if err := s.store.DeleteDevice(ctx, id); err != nil && !errors.Is(err, store.ErrNotFound) {
			return err
		}
		s.log.Info("device removed", "device_id", id)
	} else {
		if err := s.store.RevokeDevice(ctx, id); err != nil && !errors.Is(err, store.ErrNotFound) {
			return err
		}
		s.events.CloseDevice(id, hub.CloseUnauthorized, "token revoked")
		s.log.Info("device revoked", "device_id", id)
	}
	s.forgetTouch(id)
	s.events.Broadcast(proto.DevicesChanged{Type: "devices_changed"})
	return nil
}

func (s *Service) Logout(ctx context.Context, caller store.Device) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.store.RevokeDevice(ctx, caller.ID); err != nil && !errors.Is(err, store.ErrNotFound) {
		return err
	}
	s.events.CloseDevice(caller.ID, hub.CloseUnauthorized, "logged out")
	s.forgetTouch(caller.ID)
	s.events.Broadcast(proto.DevicesChanged{Type: "devices_changed"})
	s.log.Info("device logged out", "device_id", caller.ID)
	return nil
}
