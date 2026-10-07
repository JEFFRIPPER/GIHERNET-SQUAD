//go:build !windows

package main

import (
	"errors"
	"os"
	"os/exec"
	"syscall"
)

func hideWindow(cmd *exec.Cmd) {}

func killTree(pid int) {
	_ = syscall.Kill(-pid, syscall.SIGKILL)
	if p, err := os.FindProcess(pid); err == nil {
		_ = p.Kill()
	}
}

func killByName(name string) { _ = exec.Command("pkill", "-x", name).Run() }

func openFolder(dir string) error { return exec.Command("xdg-open", dir).Start() }

func openWindow(url, profileDir string) { _ = exec.Command("xdg-open", url).Start() }

func setStartup(on bool) error {
	if on {
		return errors.New("автозапуск поддерживается только в Windows")
	}
	return nil
}
