package main

import (
	"bufio"
	"context"
	"encoding/json"
	"os"
	"sync"
	"zvonok/internal/nativeclient"
)

func main() {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var mu sync.Mutex
	writer := bufio.NewWriter(os.Stdout)
	emit := func(v any) {
		mu.Lock()
		defer mu.Unlock()
		if json.NewEncoder(writer).Encode(v) != nil || writer.Flush() != nil {
			cancel()
		}
	}
	var client *nativeclient.Client
	scan := bufio.NewScanner(os.Stdin)
	scan.Buffer(make([]byte, 65536), 4<<20)
	for scan.Scan() {
		var cmd nativeclient.Command
		if json.Unmarshal(scan.Bytes(), &cmd) != nil {
			continue
		}
		if cmd.Type == "start" {
			if client != nil {
				client.Close()
			}
			var e error
			client, e = nativeclient.New(ctx, cmd, emit)
			if e != nil {
				emit(map[string]any{"type": "error", "error": e.Error()})
				continue
			}
			go client.Run()
			continue
		}
		if cmd.Type == "stop" {
			if client != nil {
				client.Close()
				client = nil
			}
			continue
		}
		if client != nil {
			if e := client.Command(cmd); e != nil {
				emit(map[string]any{"type": "error", "error": e.Error()})
			}
		}
	}
	if client != nil {
		client.Close()
	}
}
