package apierr

import (
	"errors"
	"net/http"
)

const (
	CodeInvalidRequest   = "invalid_request"
	CodeInvalidID        = "invalid_id"
	CodeSizeMismatch     = "size_mismatch"
	CodeInvalidCreds     = "invalid_credentials"
	CodeUnauthorized     = "unauthorized"
	CodeNotFound         = "not_found"
	CodeMethodNotAllowed = "method_not_allowed"
	CodeIDConflict       = "id_conflict"
	CodeAlreadyCommitted = "already_committed"
	CodeMissingChunks    = "missing_chunks"
	CodeKeyCheckMissing  = "key_check_missing"
	CodeKeyCheckExists   = "key_check_exists"
	CodeItemsExist       = "items_exist"
	CodePinnedLimit      = "pinned_limit"
	CodeBodyTooLarge     = "body_too_large"
	CodeItemTooLarge     = "item_too_large"
	CodeRateLimited      = "rate_limited"
	CodeInternal         = "internal"
	CodeDiskLow          = "disk_low"
)

type Error struct {
	Status  int
	Code    string
	Message string
	Details any
	Header  http.Header
}

func (e *Error) Error() string { return e.Code + ": " + e.Message }

func New(status int, code, message string) *Error {
	return &Error{Status: status, Code: code, Message: message}
}

func (e *Error) WithDetails(d any) *Error {
	c := *e
	c.Details = d
	return &c
}

func (e *Error) WithHeader(key, value string) *Error {
	c := *e
	c.Header = http.Header{}
	for k, v := range e.Header {
		c.Header[k] = v
	}
	c.Header.Set(key, value)
	return &c
}

func As(err error) (*Error, bool) {
	var e *Error
	if errors.As(err, &e) {
		return e, true
	}
	return nil, false
}

func InvalidRequest(message string) *Error {
	return New(http.StatusBadRequest, CodeInvalidRequest, message)
}

func InvalidID() *Error {
	return New(http.StatusBadRequest, CodeInvalidID, "id must be a lowercase UUIDv7")
}

func Unauthorized() *Error {
	return New(http.StatusUnauthorized, CodeUnauthorized, "missing, invalid or revoked token")
}

func NotFound(message string) *Error {
	return New(http.StatusNotFound, CodeNotFound, message)
}

func ItemNotFound() *Error { return NotFound("item not found") }

func KeyCheckMissing() *Error {
	return New(http.StatusConflict, CodeKeyCheckMissing, "set the key check before uploading items")
}

func AlreadyCommitted() *Error {
	return New(http.StatusConflict, CodeAlreadyCommitted, "item is already committed")
}

func IDConflict() *Error {
	return New(http.StatusConflict, CodeIDConflict, "an item with this id already exists with different content")
}

func BodyTooLarge(message string) *Error {
	return New(http.StatusRequestEntityTooLarge, CodeBodyTooLarge, message)
}

func ItemTooLarge() *Error {
	return New(http.StatusRequestEntityTooLarge, CodeItemTooLarge, "item does not fit in storage even after retention")
}

func DiskLow() *Error {
	return New(http.StatusInsufficientStorage, CodeDiskLow, "server disk is low; uploads larger than 1 MiB are rejected")
}

func Internal() *Error {
	return New(http.StatusInternalServerError, CodeInternal, "internal server error")
}
