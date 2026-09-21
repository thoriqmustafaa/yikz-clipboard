package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
)

type Account struct {
	Username   string
	Salt       []byte
	ServerID   string
	KeyCheck   *string
	CurrentSeq int64
	StateRev   int64
}

func (s *Store) InitAccount(ctx context.Context, username string, newSalt func() ([]byte, error), newServerID func() (string, error)) (Account, error) {
	err := s.inTx(ctx, func(tx *sql.Tx) error {
		var existing string
		err := tx.QueryRowContext(ctx, "SELECT username FROM account WHERE id = 1").Scan(&existing)
		switch {
		case errors.Is(err, sql.ErrNoRows):
			salt, err := newSalt()
			if err != nil {
				return err
			}
			serverID, err := newServerID()
			if err != nil {
				return err
			}
			_, err = tx.ExecContext(ctx, "INSERT INTO account (id, username, salt, server_id) VALUES (1, ?, ?, ?)", username, salt, serverID)
			return err
		case err != nil:
			return err
		case existing != username:
			_, err = tx.ExecContext(ctx, "UPDATE account SET username = ? WHERE id = 1", username)
			return err
		}
		return nil
	})
	if err != nil {
		return Account{}, fmt.Errorf("init account: %w", err)
	}
	return s.Account(ctx)
}

func (s *Store) Account(ctx context.Context) (Account, error) {
	return scanAccount(s.db.QueryRowContext(ctx, "SELECT username, salt, server_id, key_check, current_seq, state_rev FROM account WHERE id = 1"))
}

func scanAccount(row *sql.Row) (Account, error) {
	var a Account
	var kc sql.NullString
	if err := row.Scan(&a.Username, &a.Salt, &a.ServerID, &kc, &a.CurrentSeq, &a.StateRev); err != nil {
		return Account{}, fmt.Errorf("read account: %w", err)
	}
	if kc.Valid {
		v := kc.String
		a.KeyCheck = &v
	}
	return a, nil
}

func (s *Store) SetKeyCheck(ctx context.Context, value string) error {
	_, err := s.db.ExecContext(ctx, "UPDATE account SET key_check = ? WHERE id = 1", value)
	return err
}

func (s *Store) ClearKeyCheck(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, "UPDATE account SET key_check = NULL WHERE id = 1")
	return err
}

func (s *Store) CountItemsAndPending(ctx context.Context) (int64, error) {
	var n int64
	err := s.db.QueryRowContext(ctx, "SELECT (SELECT COUNT(*) FROM items) + (SELECT COUNT(*) FROM pending)").Scan(&n)
	return n, err
}
