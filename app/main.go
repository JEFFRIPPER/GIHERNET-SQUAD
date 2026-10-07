// Gnirehtet Squad — графическая оболочка для gnirehtet (reverse tethering через adb).
// Один исполняемый файл: локальный HTTP-сервер + окно Edge/Chrome в режиме приложения.
package main

import (
	"archive/zip"
	"bufio"
	"context"
	"embed"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

//go:embed ui
var uiFS embed.FS

const (
	appName         = "Gnirehtet Squad"
	appVersion      = "1.0.0"
	uiPort          = 47316
	platformToolURL = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
	apkPackage      = "com.genymobile.gnirehtet"
	maxLogLines     = 3000
)

// ---------- модели ----------

type Settings struct {
	Mode             string `json:"mode"`   // "autorun" — все устройства, "run" — одно выбранное
	Serial           string `json:"serial"` // для режима run
	DNS              string `json:"dns"`
	Routes           string `json:"routes"`
	Port             int    `json:"port"`
	AdbPath          string `json:"adbPath"`
	AutoStart        bool   `json:"autoStart"`
	AutoRestart      bool   `json:"autoRestart"`
	StopDevicesOnOff bool   `json:"stopDevicesOnOff"`
	KeepBackground   bool   `json:"keepBackground"`
	StartWithWindows bool   `json:"startWithWindows"`
}

func defaultSettings() Settings {
	return Settings{
		Mode:             "autorun",
		DNS:              "1.1.1.1,8.8.8.8",
		Port:             31416,
		AutoRestart:      true,
		StopDevicesOnOff: true,
	}
}

type Device struct {
	Serial    string `json:"serial"`
	State     string `json:"state"`
	Model     string `json:"model"`
	Product   string `json:"product"`
	Wireless  bool   `json:"wireless"`
	Installed *bool  `json:"installed"`
	Busy      string `json:"busy"`
}

type LogLine struct {
	ID    int64  `json:"id"`
	Time  string `json:"t"`
	Level string `json:"level"` // info | warn | error | debug | app
	Text  string `json:"text"`
}

type State struct {
	Version     string    `json:"version"`
	Running     bool      `json:"running"`
	Starting    bool      `json:"starting"`
	Mode        string    `json:"mode"`
	Serial      string    `json:"serial"`
	StartedAt   int64     `json:"startedAt"`
	Clients     int       `json:"clients"`
	Restarts    int       `json:"restarts"`
	AdbPath     string    `json:"adbPath"`
	AdbOK       bool      `json:"adbOk"`
	AdbError    string    `json:"adbError"`
	Gnirehtet   string    `json:"gnirehtet"`
	GnirehtetOK bool      `json:"gnirehtetOk"`
	ApkOK       bool      `json:"apkOk"`
	Devices     []Device  `json:"devices"`
	Settings    Settings  `json:"settings"`
	Download    *Download `json:"download"`
	BaseDir     string    `json:"baseDir"`
	OS          string    `json:"os"`
}

type Download struct {
	Done  int64  `json:"done"`
	Total int64  `json:"total"`
	Error string `json:"error"`
}

type event struct {
	name string
	data []byte
}

// ---------- приложение ----------

type App struct {
	mu sync.Mutex

	baseDir   string
	cfgDir    string
	settings  Settings
	background bool

	proc        *exec.Cmd
	running     bool
	starting    bool
	userStopped bool
	startedAt   time.Time
	runMode     string
	runSerial   string
	clients     map[int]bool
	restarts    int
	restartGen  int

	devices      map[string]*Device
	adbPath      string
	adbErr       string
	download     *Download
	installCheck map[string]bool

	logs   []LogLine
	logSeq int64

	subs        map[chan event]struct{}
	everSubbed  bool
	lastSubSeen time.Time

	quit chan struct{}
}

func exeName(n string) string {
	if runtime.GOOS == "windows" {
		return n + ".exe"
	}
	return n
}

func newApp(background bool) *App {
	exe, _ := os.Executable()
	exe, _ = filepath.EvalSymlinks(exe)
	base := filepath.Dir(exe)
	if d := os.Getenv("GSQUAD_BASEDIR"); d != "" {
		base = d
	}
	cfg, err := os.UserConfigDir()
	if err != nil {
		cfg = base
	}
	cfg = filepath.Join(cfg, "GnirehtetSquad")
	_ = os.MkdirAll(cfg, 0o755)

	a := &App{
		baseDir:      base,
		cfgDir:       cfg,
		settings:     defaultSettings(),
		background:   background,
		clients:      map[int]bool{},
		devices:      map[string]*Device{},
		installCheck: map[string]bool{},
		subs:         map[chan event]struct{}{},
		lastSubSeen:  time.Now(),
		quit:         make(chan struct{}),
	}
	a.loadSettings()
	return a
}

func (a *App) settingsPath() string { return filepath.Join(a.cfgDir, "settings.json") }

func (a *App) loadSettings() {
	b, err := os.ReadFile(a.settingsPath())
	if err != nil {
		return
	}
	s := defaultSettings()
	if json.Unmarshal(b, &s) == nil {
		if s.Port <= 0 || s.Port > 65535 {
			s.Port = 31416
		}
		if s.Mode != "run" {
			s.Mode = "autorun"
		}
		a.settings = s
	}
}

func (a *App) saveSettingsLocked() {
	b, _ := json.MarshalIndent(a.settings, "", "  ")
	if err := os.WriteFile(a.settingsPath(), b, 0o644); err != nil {
		a.logLocked("error", "Не удалось сохранить настройки: "+err.Error())
	}
}

func (a *App) gnirehtetPath() string { return filepath.Join(a.baseDir, exeName("gnirehtet")) }
func (a *App) apkPath() string       { return filepath.Join(a.baseDir, "gnirehtet.apk") }

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

// findAdb ищет adb: путь из настроек → рядом с программой → скачанный → PATH → Android SDK.
func (a *App) findAdb() string {
	a.mu.Lock()
	custom := a.settings.AdbPath
	a.mu.Unlock()
	adb := exeName("adb")
	var cand []string
	if custom != "" {
		if st, err := os.Stat(custom); err == nil && st.IsDir() {
			cand = append(cand, filepath.Join(custom, adb))
		} else {
			cand = append(cand, custom)
		}
	}
	cand = append(cand,
		filepath.Join(a.baseDir, "platform-tools", adb),
		filepath.Join(a.baseDir, adb),
		filepath.Join(a.cfgDir, "platform-tools", adb),
	)
	if p, err := exec.LookPath(adb); err == nil {
		cand = append(cand, p)
	}
	for _, env := range []string{"ANDROID_HOME", "ANDROID_SDK_ROOT"} {
		if v := os.Getenv(env); v != "" {
			cand = append(cand, filepath.Join(v, "platform-tools", adb))
		}
	}
	if v := os.Getenv("LOCALAPPDATA"); v != "" {
		cand = append(cand, filepath.Join(v, "Android", "Sdk", "platform-tools", adb))
	}
	for _, c := range cand {
		if fileExists(c) {
			if abs, err := filepath.Abs(c); err == nil {
				return abs
			}
			return c
		}
	}
	return ""
}

// ---------- логи и события ----------

var levelRe = regexp.MustCompile(`^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d+)? (ERROR|WARN|INFO|DEBUG|TRACE) (.*)$`)
var clientRe = regexp.MustCompile(`Client #(\d+) (connected|disconnected)`)

func (a *App) logLocked(level, text string) {
	a.logSeq++
	l := LogLine{ID: a.logSeq, Time: time.Now().Format("15:04:05"), Level: level, Text: text}
	a.logs = append(a.logs, l)
	if len(a.logs) > maxLogLines {
		a.logs = append([]LogLine(nil), a.logs[len(a.logs)-maxLogLines:]...)
	}
	b, _ := json.Marshal(l)
	a.broadcastLocked(event{"log", b})
}

func (a *App) log(level, text string) {
	a.mu.Lock()
	a.logLocked(level, text)
	a.mu.Unlock()
}

// relayLine разбирает строку вывода gnirehtet: уровень, клиенты.
func (a *App) relayLine(line string, stderr bool) {
	line = strings.TrimRight(line, "\r\n")
	if strings.TrimSpace(line) == "" {
		return
	}
	level := "info"
	if stderr {
		level = "error"
	}
	text := line
	if m := levelRe.FindStringSubmatch(line); m != nil {
		level = strings.ToLower(m[1])
		if level == "trace" {
			level = "debug"
		}
		text = m[2]
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if m := clientRe.FindStringSubmatch(text); m != nil {
		id, _ := strconv.Atoi(m[1])
		if m[2] == "connected" {
			a.clients[id] = true
		} else {
			delete(a.clients, id)
		}
		defer a.pushStateLocked()
	}
	a.logLocked(level, text)
}

func (a *App) broadcastLocked(ev event) {
	for ch := range a.subs {
		select {
		case ch <- ev:
		default: // медленный клиент — пропускаем
		}
	}
}

func (a *App) stateLocked() State {
	devs := make([]Device, 0, len(a.devices))
	for _, d := range a.devices {
		devs = append(devs, *d)
	}
	sort.Slice(devs, func(i, j int) bool { return devs[i].Serial < devs[j].Serial })
	var started int64
	if a.running {
		started = a.startedAt.UnixMilli()
	}
	var dl *Download
	if a.download != nil {
		c := *a.download
		dl = &c
	}
	return State{
		Version: appVersion, Running: a.running, Starting: a.starting,
		Mode: a.runMode, Serial: a.runSerial, StartedAt: started,
		Clients: len(a.clients), Restarts: a.restarts,
		AdbPath: a.adbPath, AdbOK: a.adbPath != "" && a.adbErr == "", AdbError: a.adbErr,
		Gnirehtet: a.gnirehtetPath(), GnirehtetOK: fileExists(a.gnirehtetPath()), ApkOK: fileExists(a.apkPath()),
		Devices: devs, Settings: a.settings, Download: dl, BaseDir: a.baseDir, OS: runtime.GOOS,
	}
}

func (a *App) pushStateLocked() {
	b, _ := json.Marshal(a.stateLocked())
	a.broadcastLocked(event{"state", b})
}

func (a *App) pushState() {
	a.mu.Lock()
	a.pushStateLocked()
	a.mu.Unlock()
}

// ---------- запуск внешних команд ----------

func (a *App) env() []string {
	env := os.Environ()
	if a.adbPath != "" {
		env = append(env, "ADB="+a.adbPath)
	}
	return env
}

// runTool выполняет короткую команду и пишет её вывод в лог.
func (a *App) runTool(timeout time.Duration, logOutput bool, name string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = a.baseDir
	a.mu.Lock()
	cmd.Env = a.env()
	a.mu.Unlock()
	hideWindow(cmd)
	out, err := cmd.CombinedOutput()
	if logOutput {
		for _, l := range strings.Split(string(out), "\n") {
			a.relayLine(l, false)
		}
	}
	if ctx.Err() == context.DeadlineExceeded {
		return string(out), fmt.Errorf("превышено время ожидания (%s)", timeout)
	}
	return string(out), err
}

func (a *App) gnirehtet(timeout time.Duration, args ...string) error {
	_, err := a.runTool(timeout, true, a.gnirehtetPath(), args...)
	return err
}

func (a *App) adb(timeout time.Duration, args ...string) (string, error) {
	a.mu.Lock()
	p := a.adbPath
	a.mu.Unlock()
	if p == "" {
		return "", errors.New("adb не найден")
	}
	return a.runTool(timeout, false, p, args...)
}

// ---------- relay ----------

func (a *App) relayArgs(mode, serial string) []string {
	s := a.settings
	var args []string
	if mode == "run" {
		args = append(args, "run")
		if serial != "" {
			args = append(args, serial)
		}
	} else {
		args = append(args, "autorun")
	}
	if d := strings.ReplaceAll(strings.TrimSpace(s.DNS), " ", ""); d != "" {
		args = append(args, "-d", d)
	}
	if r := strings.ReplaceAll(strings.TrimSpace(s.Routes), " ", ""); r != "" {
		args = append(args, "-r", r)
	}
	if s.Port != 0 && s.Port != 31416 {
		args = append(args, "-p", strconv.Itoa(s.Port))
	}
	return args
}

func portFree(port int) bool {
	l, err := net.Listen("tcp", "127.0.0.1:"+strconv.Itoa(port))
	if err != nil {
		return false
	}
	l.Close()
	return true
}

func (a *App) Start(mode, serial string) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.running || a.starting {
		return errors.New("relay уже запущен")
	}
	if mode == "" {
		mode = a.settings.Mode
	}
	if mode == "run" && serial == "" {
		serial = a.settings.Serial
	}
	if !fileExists(a.gnirehtetPath()) {
		return fmt.Errorf("не найден %s рядом с программой", exeName("gnirehtet"))
	}
	if a.adbPath == "" {
		return errors.New("adb не найден — скачайте platform-tools или укажите путь в настройках")
	}
	if !portFree(a.settings.Port) {
		return fmt.Errorf("порт %d занят — вероятно, уже работает другой gnirehtet. Нажмите «Завершить зависшие процессы»", a.settings.Port)
	}
	a.userStopped = false
	a.restarts = 0
	a.restartGen++
	return a.spawnLocked(mode, serial, a.restartGen)
}

func (a *App) spawnLocked(mode, serial string, gen int) error {
	args := a.relayArgs(mode, serial)
	cmd := exec.Command(a.gnirehtetPath(), args...)
	cmd.Dir = a.baseDir
	cmd.Env = a.env()
	hideWindow(cmd)
	stdout, _ := cmd.StdoutPipe()
	stderr, _ := cmd.StderrPipe()
	if err := cmd.Start(); err != nil {
		return err
	}
	a.proc = cmd
	a.running = true
	a.starting = false
	a.runMode = mode
	a.runSerial = serial
	a.startedAt = time.Now()
	a.clients = map[int]bool{}
	a.logLocked("app", "▶ gnirehtet "+strings.Join(args, " "))
	a.pushStateLocked()

	var wg sync.WaitGroup
	pipe := func(r io.Reader, isErr bool) {
		defer wg.Done()
		sc := bufio.NewScanner(r)
		sc.Buffer(make([]byte, 64*1024), 1024*1024)
		for sc.Scan() {
			a.relayLine(sc.Text(), isErr)
		}
	}
	wg.Add(2)
	go pipe(stdout, false)
	go pipe(stderr, true)

	go func() {
		wg.Wait()
		err := cmd.Wait()
		a.mu.Lock()
		defer a.mu.Unlock()
		if a.proc != cmd {
			return
		}
		a.proc = nil
		a.running = false
		a.clients = map[int]bool{}
		code := 0
		if cmd.ProcessState != nil {
			code = cmd.ProcessState.ExitCode()
		}
		if a.userStopped {
			a.logLocked("app", "■ Relay остановлен")
			a.pushStateLocked()
			return
		}
		msg := fmt.Sprintf("Relay завершился (код %d)", code)
		if err != nil && code == 0 {
			msg += ": " + err.Error()
		}
		a.logLocked("warn", msg)
		if a.settings.AutoRestart && gen == a.restartGen && a.restarts < 50 {
			a.restarts++
			delay := time.Duration(min(a.restarts, 10)) * 2 * time.Second
			a.starting = true
			a.logLocked("app", fmt.Sprintf("↻ Автоперезапуск через %d с (попытка %d)", int(delay.Seconds()), a.restarts))
			go func() {
				time.Sleep(delay)
				a.mu.Lock()
				defer a.mu.Unlock()
				if a.userStopped || gen != a.restartGen || a.running {
					a.starting = false
					a.pushStateLocked()
					return
				}
				if err := a.spawnLocked(mode, serial, gen); err != nil {
					a.starting = false
					a.logLocked("error", "Перезапуск не удался: "+err.Error())
					a.pushStateLocked()
				}
			}()
		}
		a.pushStateLocked()
	}()
	return nil
}

func (a *App) Stop(stopDevices bool) {
	a.mu.Lock()
	a.userStopped = true
	a.starting = false
	a.restartGen++
	cmd := a.proc
	var serials []string
	if stopDevices && a.settings.StopDevicesOnOff {
		for _, d := range a.devices {
			if d.State == "device" && (a.runMode != "run" || a.runSerial == "" || a.runSerial == d.Serial) {
				serials = append(serials, d.Serial)
			}
		}
	}
	a.pushStateLocked()
	a.mu.Unlock()
	if cmd != nil && cmd.Process != nil {
		killTree(cmd.Process.Pid)
	}
	var wg sync.WaitGroup
	for _, s := range serials {
		wg.Add(1)
		go func(s string) {
			defer wg.Done()
			_ = a.gnirehtet(15*time.Second, "stop", s)
		}(s)
	}
	wg.Wait()
}

// ---------- устройства ----------

func parseDevices(out string) []Device {
	var res []Device
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "List of devices") || strings.HasPrefix(line, "*") {
			continue
		}
		f := strings.Fields(line)
		if len(f) < 2 {
			continue
		}
		d := Device{Serial: f[0], State: f[1]}
		for _, kv := range f[2:] {
			k, v, ok := strings.Cut(kv, ":")
			if !ok {
				continue
			}
			switch k {
			case "model":
				d.Model = strings.ReplaceAll(v, "_", " ")
			case "product":
				d.Product = v
			}
		}
		d.Wireless = strings.Contains(d.Serial, ":") || strings.HasPrefix(d.Serial, "adb-")
		res = append(res, d)
	}
	return res
}

func (a *App) pollDevices() {
	adbPath := a.findAdb()
	a.mu.Lock()
	changedAdb := adbPath != a.adbPath
	a.adbPath = adbPath
	a.mu.Unlock()
	if changedAdb {
		if adbPath != "" {
			a.log("app", "adb: "+adbPath)
		} else {
			a.log("warn", "adb не найден. Нажмите «Скачать platform-tools» или укажите путь в настройках.")
		}
	}

	var list []Device
	adbErr := ""
	if adbPath != "" {
		out, err := a.adb(8*time.Second, "devices", "-l")
		if err != nil {
			adbErr = strings.TrimSpace(err.Error() + " " + firstLine(out))
		} else {
			list = parseDevices(out)
		}
	} else {
		adbErr = "adb не найден"
	}

	a.mu.Lock()
	changed := adbErr != a.adbErr || changedAdb
	a.adbErr = adbErr
	seen := map[string]bool{}
	for _, d := range list {
		seen[d.Serial] = true
		old, ok := a.devices[d.Serial]
		if !ok {
			nd := d
			a.devices[d.Serial] = &nd
			a.logLocked("app", fmt.Sprintf("＋ Устройство %s (%s) — %s", d.Serial, nz(d.Model, "?"), stateRu(d.State)))
			changed = true
			continue
		}
		if old.State != d.State || old.Model != d.Model {
			if old.State != d.State {
				a.logLocked("app", fmt.Sprintf("● %s: %s", d.Serial, stateRu(d.State)))
			}
			old.State, old.Model, old.Product, old.Wireless = d.State, d.Model, d.Product, d.Wireless
			if d.State != "device" {
				old.Installed = nil
				delete(a.installCheck, d.Serial)
			}
			changed = true
		}
	}
	for s := range a.devices {
		if !seen[s] {
			a.logLocked("app", "－ Устройство отключено: "+s)
			delete(a.devices, s)
			delete(a.installCheck, s)
			changed = true
		}
	}
	var toCheck []string
	for s, d := range a.devices {
		if d.State == "device" && !a.installCheck[s] {
			a.installCheck[s] = true
			toCheck = append(toCheck, s)
		}
	}
	if changed {
		a.pushStateLocked()
	}
	a.mu.Unlock()

	for _, s := range toCheck {
		go a.checkInstalled(s)
	}
}

func (a *App) checkInstalled(serial string) {
	out, err := a.adb(15*time.Second, "-s", serial, "shell", "pm", "list", "packages", apkPackage)
	a.mu.Lock()
	defer a.mu.Unlock()
	d, ok := a.devices[serial]
	if !ok {
		return
	}
	if err != nil {
		delete(a.installCheck, serial)
		return
	}
	v := strings.Contains(out, "package:"+apkPackage)
	d.Installed = &v
	a.pushStateLocked()
}

func (a *App) deviceAction(serial, action string) error {
	valid := map[string]bool{"start": true, "stop": true, "install": true, "reinstall": true, "uninstall": true, "tunnel": true}
	if !valid[action] {
		return errors.New("неизвестное действие")
	}
	a.mu.Lock()
	d, ok := a.devices[serial]
	if !ok {
		a.mu.Unlock()
		return errors.New("устройство не найдено")
	}
	if d.Busy != "" {
		a.mu.Unlock()
		return errors.New("устройство занято: " + d.Busy)
	}
	if action == "start" && !a.running {
		a.mu.Unlock()
		return errors.New("сначала запустите relay")
	}
	d.Busy = action
	a.pushStateLocked()
	a.mu.Unlock()

	go func() {
		err := a.gnirehtet(90*time.Second, action, serial)
		a.mu.Lock()
		if d, ok := a.devices[serial]; ok {
			d.Busy = ""
			if action == "install" || action == "reinstall" || action == "uninstall" {
				delete(a.installCheck, serial)
				d.Installed = nil
			}
		}
		if err != nil {
			a.logLocked("error", fmt.Sprintf("%s %s: %v", action, serial, err))
		} else {
			a.logLocked("app", fmt.Sprintf("✓ %s %s — готово", action, serial))
		}
		a.pushStateLocked()
		a.mu.Unlock()
	}()
	return nil
}

// ---------- platform-tools ----------

type progressWriter struct {
	a *App
	n int64
	t time.Time
}

func (p *progressWriter) Write(b []byte) (int, error) {
	p.n += int64(len(b))
	if time.Since(p.t) > 200*time.Millisecond {
		p.t = time.Now()
		p.a.mu.Lock()
		if p.a.download != nil {
			p.a.download.Done = p.n
		}
		p.a.pushStateLocked()
		p.a.mu.Unlock()
	}
	return len(b), nil
}

func writableDir(dir string) bool {
	f, err := os.CreateTemp(dir, ".wtest*")
	if err != nil {
		return false
	}
	name := f.Name()
	f.Close()
	os.Remove(name)
	return true
}

func (a *App) DownloadPlatformTools() error {
	a.mu.Lock()
	if a.download != nil && a.download.Error == "" {
		a.mu.Unlock()
		return errors.New("загрузка уже идёт")
	}
	a.download = &Download{}
	a.logLocked("app", "⇣ Скачивание platform-tools с dl.google.com…")
	a.pushStateLocked()
	a.mu.Unlock()

	go func() {
		err := a.downloadPT()
		a.mu.Lock()
		if err != nil {
			a.download.Error = err.Error()
			a.logLocked("error", "Не удалось скачать platform-tools: "+err.Error())
		} else {
			a.download = nil
			a.logLocked("app", "✓ platform-tools установлены")
		}
		a.pushStateLocked()
		a.mu.Unlock()
		a.pollDevices()
	}()
	return nil
}

func (a *App) downloadPT() error {
	target := a.baseDir
	if !writableDir(target) {
		target = a.cfgDir
	}
	resp, err := http.Get(platformToolURL)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	a.mu.Lock()
	a.download.Total = resp.ContentLength
	a.mu.Unlock()
	tmp, err := os.CreateTemp(a.cfgDir, "pt-*.zip")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if _, err := io.Copy(io.MultiWriter(tmp, &progressWriter{a: a}), resp.Body); err != nil {
		tmp.Close()
		return err
	}
	tmp.Close()
	return unzipTo(tmp.Name(), target)
}

func unzipTo(zipPath, dest string) error {
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		return err
	}
	defer zr.Close()
	destAbs, _ := filepath.Abs(dest)
	for _, f := range zr.File {
		p := filepath.Join(destAbs, filepath.FromSlash(f.Name))
		if !strings.HasPrefix(p, destAbs+string(os.PathSeparator)) {
			return fmt.Errorf("недопустимый путь в архиве: %s", f.Name)
		}
		if f.FileInfo().IsDir() {
			os.MkdirAll(p, 0o755)
			continue
		}
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			return err
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		out, err := os.OpenFile(p, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, f.Mode()|0o600)
		if err != nil {
			rc.Close()
			return err
		}
		_, err = io.Copy(out, rc)
		rc.Close()
		out.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

// ---------- HTTP ----------

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func okOrErr(w http.ResponseWriter, err error) {
	if err != nil {
		writeJSON(w, 400, map[string]string{"error": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]bool{"ok": true})
}

func (a *App) routes() http.Handler {
	mux := http.NewServeMux()
	sub, _ := fs.Sub(uiFS, "ui")
	mux.Handle("/", http.FileServer(http.FS(sub)))

	mux.HandleFunc("/api/ping", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, 200, map[string]string{"app": appName})
	})
	mux.HandleFunc("/api/state", func(w http.ResponseWriter, r *http.Request) {
		a.mu.Lock()
		s := a.stateLocked()
		a.mu.Unlock()
		writeJSON(w, 200, s)
	})
	mux.HandleFunc("/api/logs", func(w http.ResponseWriter, r *http.Request) {
		a.mu.Lock()
		l := append([]LogLine(nil), a.logs...)
		a.mu.Unlock()
		writeJSON(w, 200, l)
	})
	mux.HandleFunc("/api/events", a.handleEvents)

	post := func(path string, h func(r *http.Request) error) {
		mux.HandleFunc(path, func(w http.ResponseWriter, r *http.Request) {
			if r.Method != http.MethodPost {
				w.WriteHeader(405)
				return
			}
			// защита от запросов со сторонних сайтов
			if o := r.Header.Get("Origin"); o != "" && !strings.HasPrefix(o, "http://127.0.0.1:") && !strings.HasPrefix(o, "http://localhost:") {
				w.WriteHeader(403)
				return
			}
			okOrErr(w, h(r))
		})
	}
	type req struct {
		Mode   string `json:"mode"`
		Serial string `json:"serial"`
		Action string `json:"action"`
	}
	decode := func(r *http.Request, v any) error {
		if r.ContentLength == 0 {
			return nil
		}
		return json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(v)
	}
	post("/api/start", func(r *http.Request) error {
		var q req
		if err := decode(r, &q); err != nil {
			return err
		}
		return a.Start(q.Mode, q.Serial)
	})
	post("/api/stop", func(r *http.Request) error { go a.Stop(true); return nil })
	post("/api/restart", func(r *http.Request) error {
		a.mu.Lock()
		mode, serial := a.runMode, a.runSerial
		a.mu.Unlock()
		a.Stop(false)
		time.Sleep(700 * time.Millisecond)
		return a.Start(mode, serial)
	})
	post("/api/device", func(r *http.Request) error {
		var q req
		if err := decode(r, &q); err != nil {
			return err
		}
		return a.deviceAction(q.Serial, q.Action)
	})
	post("/api/settings", func(r *http.Request) error {
		a.mu.Lock()
		s := a.settings
		a.mu.Unlock()
		if err := decode(r, &s); err != nil {
			return err
		}
		if s.Port <= 0 || s.Port > 65535 {
			return errors.New("порт должен быть 1–65535")
		}
		if s.Mode != "run" {
			s.Mode = "autorun"
		}
		a.mu.Lock()
		prevStartup := a.settings.StartWithWindows
		a.settings = s
		a.saveSettingsLocked()
		a.pushStateLocked()
		a.mu.Unlock()
		if prevStartup != s.StartWithWindows {
			if err := setStartup(s.StartWithWindows); err != nil {
				a.log("error", "Автозапуск Windows: "+err.Error())
			} else if s.StartWithWindows {
				a.log("app", "✓ Добавлено в автозагрузку Windows")
			} else {
				a.log("app", "Удалено из автозагрузки Windows")
			}
		}
		go a.pollDevices()
		return nil
	})
	post("/api/adb/download", func(r *http.Request) error { return a.DownloadPlatformTools() })
	post("/api/adb/restart", func(r *http.Request) error {
		go func() {
			a.log("app", "↻ Перезапуск adb-сервера…")
			_, _ = a.adb(10*time.Second, "kill-server")
			out, err := a.adb(20*time.Second, "start-server")
			if err != nil {
				a.log("error", "adb start-server: "+err.Error()+" "+out)
			} else {
				a.log("app", "✓ adb-сервер запущен")
			}
			a.pollDevices()
		}()
		return nil
	})
	post("/api/adb/connect", func(r *http.Request) error {
		var q struct {
			Addr string `json:"addr"`
		}
		if err := decode(r, &q); err != nil {
			return err
		}
		addr := strings.TrimSpace(q.Addr)
		if addr == "" || strings.ContainsAny(addr, " \t\"'&|;") {
			return errors.New("укажите адрес вида 192.168.1.10:5555")
		}
		go func() {
			out, err := a.adb(15*time.Second, "connect", addr)
			if err != nil {
				a.log("error", "adb connect: "+err.Error())
			}
			a.log("app", "adb connect "+addr+": "+strings.TrimSpace(out))
			a.pollDevices()
		}()
		return nil
	})
	post("/api/killstale", func(r *http.Request) error {
		a.Stop(false)
		killByName(exeName("gnirehtet"))
		a.log("app", "✓ Все процессы gnirehtet завершены")
		return nil
	})
	post("/api/logs/clear", func(r *http.Request) error {
		a.mu.Lock()
		a.logs = nil
		a.mu.Unlock()
		return nil
	})
	post("/api/openfolder", func(r *http.Request) error { return openFolder(a.baseDir) })
	post("/api/quit", func(r *http.Request) error {
		go func() { time.Sleep(200 * time.Millisecond); a.shutdown() }()
		return nil
	})
	return mux
}

func (a *App) handleEvents(w http.ResponseWriter, r *http.Request) {
	fl, ok := w.(http.Flusher)
	if !ok {
		w.WriteHeader(500)
		return
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	ch := make(chan event, 256)
	a.mu.Lock()
	a.subs[ch] = struct{}{}
	a.everSubbed = true
	b, _ := json.Marshal(a.stateLocked())
	a.mu.Unlock()
	defer func() {
		a.mu.Lock()
		delete(a.subs, ch)
		a.lastSubSeen = time.Now()
		a.mu.Unlock()
	}()
	fmt.Fprintf(w, "event: state\ndata: %s\n\n", b)
	fl.Flush()
	ping := time.NewTicker(15 * time.Second)
	defer ping.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case <-a.quit:
			return
		case ev := <-ch:
			fmt.Fprintf(w, "event: %s\ndata: %s\n\n", ev.name, ev.data)
			fl.Flush()
		case <-ping.C:
			fmt.Fprint(w, ": ping\n\n")
			fl.Flush()
		}
	}
}

var shutdownOnce sync.Once

func (a *App) shutdown() {
	shutdownOnce.Do(func() {
		a.log("app", "Выход…")
		a.Stop(true)
		close(a.quit)
		time.Sleep(300 * time.Millisecond)
		os.Exit(0)
	})
}

// watchWindow завершает программу, когда окно закрыто (нет подключённых окон),
// если не включена работа в фоне.
func (a *App) watchWindow() {
	t := time.NewTicker(2 * time.Second)
	for range t.C {
		a.mu.Lock()
		n := len(a.subs)
		bg := a.background || a.settings.KeepBackground
		idle := time.Since(a.lastSubSeen)
		ever := a.everSubbed
		a.mu.Unlock()
		if n > 0 || bg {
			continue
		}
		if (ever && idle > 8*time.Second) || (!ever && idle > 60*time.Second) {
			a.shutdown()
		}
	}
}

// ---------- main ----------

func main() {
	background := flag.Bool("background", false, "запуск без окна (для автозагрузки), relay стартует сразу")
	noWindow := flag.Bool("no-window", false, "не открывать окно (только сервер)")
	flag.Parse()

	url := fmt.Sprintf("http://127.0.0.1:%d/", uiPort)
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", uiPort))
	if err != nil {
		// уже запущен — просто открываем окно существующего экземпляра
		if resp, e := http.Get(url + "api/ping"); e == nil {
			resp.Body.Close()
			if !*background {
				openWindow(url, "")
			}
			return
		}
		ln, err = net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			log.Fatal(err)
		}
		url = fmt.Sprintf("http://%s/", ln.Addr().String())
	}

	a := newApp(*background)
	a.log("app", fmt.Sprintf("%s %s · папка: %s", appName, appVersion, a.baseDir))
	if !fileExists(a.gnirehtetPath()) {
		a.log("error", "Не найден "+a.gnirehtetPath()+" — положите программу рядом с gnirehtet.exe")
	}
	a.pollDevices()
	go func() {
		for {
			time.Sleep(2 * time.Second)
			a.pollDevices()
		}
	}()

	a.mu.Lock()
	auto := a.settings.AutoStart || *background
	a.mu.Unlock()
	if auto {
		if err := a.Start("", ""); err != nil {
			a.log("error", "Автозапуск relay: "+err.Error())
		}
	}

	go a.watchWindow()
	if !*background && !*noWindow {
		go openWindow(url, filepath.Join(a.cfgDir, "window"))
	}
	srv := &http.Server{Handler: a.routes()}
	log.Fatal(srv.Serve(ln))
}

func firstLine(s string) string {
	s = strings.TrimSpace(s)
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		return s[:i]
	}
	return s
}

func nz(s, d string) string {
	if s == "" {
		return d
	}
	return s
}

func stateRu(s string) string {
	switch s {
	case "device":
		return "готово"
	case "unauthorized":
		return "нужно разрешить отладку на телефоне"
	case "offline":
		return "offline"
	case "authorizing":
		return "авторизация…"
	}
	return s
}
