package klondike

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// The fixtures of ../testdata/messages are the server messages of
// sampleMessages encoded by this package: <n>-<type>.json (format=json) and
// <n>-<type>.capnp (format=capnp, packed). The tests of the other libraries
// check that both files decode to the same message.
// KLONDIKE_UPDATE_FIXTURES=1 go test rewrites them.
func TestFixtures(t *testing.T) {
	dir := filepath.Join("..", "testdata", "messages")
	update := os.Getenv("KLONDIKE_UPDATE_FIXTURES") != ""
	if update {
		os.MkdirAll(dir, 0o755)
	}
	for n, m := range sampleMessages() {
		js, err := json.Marshal(m)
		if err != nil {
			t.Fatal(err)
		}
		cp, err := m.MarshalCapnp()
		if err != nil {
			t.Fatal(err)
		}
		files := map[string][]byte{".json": append(js, '\n'), ".capnp": cp}
		for ext, want := range files {
			name := filepath.Join(dir, fixtureName(n, m)+ext)
			if update {
				if err := os.WriteFile(name, want, 0o644); err != nil {
					t.Fatal(err)
				}
				continue
			}
			got, err := os.ReadFile(name)
			if err != nil {
				t.Fatalf("%v (KLONDIKE_UPDATE_FIXTURES=1 go test to write the fixtures)", err)
			}
			if !bytes.Equal(got, want) {
				t.Errorf("%s differs from the encoding of the sample message", name)
			}
		}
	}
}

func fixtureName(n int, m Message) string {
	return string(rune('0'+n)) + "-" + string(m.Type)
}
