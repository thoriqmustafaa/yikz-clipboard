package main

import (
	"flag"
	"fmt"
	"os"
)

func main() {
	out := flag.String("out", "../vectors", "directory to write vector files into")
	flag.Parse()
	must(os.MkdirAll(*out, 0o755))
	f := buildFixture()
	writeJSON(*out, "kdf.json", kdfVectors())
	writeJSON(*out, "keys.json", keyVectors(f))
	writeJSON(*out, "aead.json", aeadVectors(f))
	writeJSON(*out, "uuidv7.json", uuidVectors())
	writeJSON(*out, "files_archive.json", archiveVectors(f))
	writeJSON(*out, "chunking.json", chunkingVectors())
	writeJSON(*out, "preview.json", previewVectors())
	writeJSON(*out, "token.json", tokenVectors(f))
	writeJSON(*out, "items.json", itemsVectors(f))
	writeJSON(*out, "http.json", httpVectors(f))
	writeJSON(*out, "ws.json", wsVectors(f))
	fmt.Println("vectors written to", *out)
}
