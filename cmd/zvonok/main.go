package main

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"zvonok/internal/config"
	"zvonok/internal/server"
)

func main() {
	if len(os.Args) == 3 && os.Args[1] == "healthcheck" {
		client := &http.Client{Timeout: 5 * time.Second}
		response, err := client.Get(os.Args[2])
		if err != nil {
			os.Exit(1)
		}
		defer response.Body.Close()
		body, err := io.ReadAll(io.LimitReader(response.Body, 16))
		if err != nil || response.StatusCode != 200 || string(body) != "ok" {
			os.Exit(1)
		}
		return
	}
	if len(os.Args) > 1 && os.Args[1] == "account" {
		if err := manageAccounts(os.Args[2:]); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	cfg, err := config.Parse(os.Args[1:])
	if err != nil {
		fmt.Fprintln(os.Stderr, "zvonok:", err)
		os.Exit(2)
	}
	srv, err := server.New(cfg)
	if err != nil {
		fmt.Fprintln(os.Stderr, "zvonok:", err)
		os.Exit(1)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if err := srv.Run(ctx); err != nil {
		fmt.Fprintln(os.Stderr, "zvonok:", err)
		os.Exit(1)
	}
}
