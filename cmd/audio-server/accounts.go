package main

import (
	"flag"
	"fmt"
	"github.com/aes-gcm/audio.lgbt/internal/accounts"
	"io"
	"os"
	"sort"
	"strings"
)

func manageAccounts(args []string) error {
	fs := flag.NewFlagSet("account", flag.ContinueOnError)
	path := fs.String("file", "data/accounts.json", "local account file")
	remove := fs.Bool("delete", false, "delete this account")
	list := fs.Bool("list", false, "list account names")
	stdin := fs.Bool("password-stdin", false, "read password from standard input (never from arguments)")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if *list {
		data, err := accounts.Read(*path)
		if err != nil {
			return err
		}
		names := make([]string, 0, len(data.Users))
		for name := range data.Users {
			names = append(names, name)
		}
		sort.Strings(names)
		for _, name := range names {
			fmt.Println(name)
		}
		return nil
	}
	if fs.NArg() != 1 {
		return fmt.Errorf("usage: account [-file data/accounts.json] -password-stdin USERNAME; or -delete USERNAME; or -list")
	}
	var password string
	if !*remove {
		if !*stdin {
			return fmt.Errorf("use -password-stdin; passwords are not accepted in command-line arguments")
		}
		raw, err := io.ReadAll(io.LimitReader(os.Stdin, 1024))
		if err != nil {
			return err
		}
		password = strings.TrimRight(string(raw), "\r\n")
	}
	if err := accounts.SetUser(*path, fs.Arg(0), password, *remove); err != nil {
		return err
	}
	fmt.Println("Account updated.")
	return nil
}
