package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
)

type kv struct {
	K string
	V any
}

type obj []kv

func (o obj) MarshalJSON() ([]byte, error) {
	var buf bytes.Buffer
	buf.WriteByte('{')
	for i, e := range o {
		if i > 0 {
			buf.WriteByte(',')
		}
		k, err := marshalCompact(e.K)
		if err != nil {
			return nil, err
		}
		buf.Write(k)
		buf.WriteByte(':')
		v, err := marshalCompact(e.V)
		if err != nil {
			return nil, err
		}
		buf.Write(v)
	}
	buf.WriteByte('}')
	return buf.Bytes(), nil
}

func (o obj) with(extra ...kv) obj {
	out := append(obj{}, o...)
	return append(out, extra...)
}

func (o obj) without(keys ...string) obj {
	skip := map[string]bool{}
	for _, k := range keys {
		skip[k] = true
	}
	var out obj
	for _, e := range o {
		if !skip[e.K] {
			out = append(out, e)
		}
	}
	return out
}

func marshalCompact(v any) ([]byte, error) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(v); err != nil {
		return nil, err
	}
	return bytes.TrimRight(buf.Bytes(), "\n"), nil
}

func writeJSON(dir, name string, v any) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	enc.SetIndent("", "  ")
	must(enc.Encode(v))
	must(os.WriteFile(filepath.Join(dir, name), buf.Bytes(), 0o644))
}
