//go:build windows

package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"syscall"
)

const createNoWindow = 0x08000000

func hideWindow(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: createNoWindow}
}

func runHidden(name string, args ...string) error {
	cmd := exec.Command(name, args...)
	hideWindow(cmd)
	return cmd.Run()
}

func killTree(pid int) {
	if runHidden("taskkill", "/T", "/F", "/PID", strconv.Itoa(pid)) != nil {
		if p, err := os.FindProcess(pid); err == nil {
			_ = p.Kill()
		}
	}
}

func killByName(name string) { _ = runHidden("taskkill", "/F", "/T", "/IM", name) }

func openFolder(dir string) error { return exec.Command("explorer", dir).Start() }

func browserCandidates() []string {
	var c []string
	for _, env := range []string{"ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"} {
		base := os.Getenv(env)
		if base == "" {
			continue
		}
		c = append(c,
			filepath.Join(base, `Microsoft\Edge\Application\msedge.exe`),
			filepath.Join(base, `Google\Chrome\Application\chrome.exe`),
			filepath.Join(base, `BraveSoftware\Brave-Browser\Application\brave.exe`),
			filepath.Join(base, `Chromium\Application\chrome.exe`),
		)
	}
	return c
}

// openWindow открывает интерфейс отдельным окном (режим приложения Edge/Chrome).
func openWindow(url, profileDir string) {
	for _, b := range browserCandidates() {
		if !fileExists(b) {
			continue
		}
		args := []string{"--app=" + url, "--window-size=1200,820", "--no-first-run", "--no-default-browser-check", "--disable-features=Translate"}
		if profileDir != "" {
			args = append(args, "--user-data-dir="+profileDir)
		}
		if exec.Command(b, args...).Start() == nil {
			return
		}
	}
	_ = exec.Command("rundll32", "url.dll,FileProtocolHandler", url).Start()
}

const runKey = `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

func setStartup(on bool) error {
	if !on {
		_ = runHidden("reg", "delete", runKey, "/v", "GnirehtetSquad", "/f")
		return nil
	}
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	return runHidden("reg", "add", runKey, "/v", "GnirehtetSquad", "/t", "REG_SZ", "/d", `"`+exe+`" --background`, "/f")
}
