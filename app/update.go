package main

// OTA-обновления: проверка последнего релиза на GitHub, скачивание zip,
// замена файлов рядом с программой и перезапуск новой версии.

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

const updateRepo = "JEFFRIPPER/GIHERNET-SQUAD"

type UpdateInfo struct {
	Checking   bool   `json:"checking"`
	CheckedAt  int64  `json:"checkedAt"`
	Latest     string `json:"latest"`
	Available  bool   `json:"available"`
	Notes      string `json:"notes"`
	URL        string `json:"url"`
	Size       int64  `json:"size"`
	Installing bool   `json:"installing"`
	Done       int64  `json:"done"`
	Stage      string `json:"stage"`
	Error      string `json:"error"`

	assetURL string
	digest   string
}

type ghRelease struct {
	TagName    string `json:"tag_name"`
	Body       string `json:"body"`
	HTMLURL    string `json:"html_url"`
	Draft      bool   `json:"draft"`
	Prerelease bool   `json:"prerelease"`
	Assets     []struct {
		Name   string `json:"name"`
		Size   int64  `json:"size"`
		URL    string `json:"browser_download_url"`
		Digest string `json:"digest"`
	} `json:"assets"`
}

func updateAPI() string {
	if v := os.Getenv("GSQUAD_UPDATE_API"); v != "" { // для тестов
		return v
	}
	return "https://api.github.com/repos/" + updateRepo + "/releases/latest"
}

// newerVersion сообщает, новее ли a, чем b ("1.2.0" > "1.1.9").
func newerVersion(a, b string) bool {
	pa, pb := splitVer(a), splitVer(b)
	for i := 0; i < 3; i++ {
		if pa[i] != pb[i] {
			return pa[i] > pb[i]
		}
	}
	return false
}

func splitVer(v string) [3]int {
	var r [3]int
	v = strings.TrimPrefix(strings.TrimSpace(v), "v")
	if i := strings.IndexAny(v, "-+ "); i >= 0 {
		v = v[:i]
	}
	for i, p := range strings.SplitN(v, ".", 3) {
		r[i], _ = strconv.Atoi(p)
	}
	return r
}

var httpClient = &http.Client{Timeout: 10 * time.Minute}

func ghGet(url string) (*http.Response, error) {
	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "GnirehtetSquad/"+appVersion)
	req.Header.Set("Accept", "application/vnd.github+json")
	return httpClient.Do(req)
}

// CheckUpdate запрашивает последний релиз. manual — проверка по кнопке (пишем результат в журнал).
func (a *App) CheckUpdate(manual bool) {
	a.mu.Lock()
	if a.update.Checking || a.update.Installing {
		a.mu.Unlock()
		return
	}
	a.update.Checking = true
	a.update.Error = ""
	a.pushStateLocked()
	a.mu.Unlock()

	rel, err := fetchLatest()

	a.mu.Lock()
	defer a.mu.Unlock()
	u := &a.update
	u.Checking = false
	u.CheckedAt = time.Now().UnixMilli()
	if err != nil {
		u.Error = "Проверка обновлений: " + err.Error()
		if manual {
			a.logLocked("warn", u.Error)
		}
		a.pushStateLocked()
		return
	}
	latest := strings.TrimPrefix(rel.TagName, "v")
	u.Latest, u.Notes, u.URL = latest, strings.TrimSpace(rel.Body), rel.HTMLURL
	u.assetURL, u.digest, u.Size = "", "", 0
	for _, as := range rel.Assets {
		if strings.HasPrefix(as.Name, "GnirehtetSquad-") && strings.HasSuffix(as.Name, "-win64.zip") {
			u.assetURL, u.digest, u.Size = as.URL, as.Digest, as.Size
			break
		}
	}
	wasAvailable := u.Available
	u.Available = newerVersion(latest, appVersion) && u.assetURL != ""
	switch {
	case u.Available && (!wasAvailable || manual):
		a.logLocked("app", fmt.Sprintf("⬆ Доступно обновление %s (у вас %s)", latest, appVersion))
	case !u.Available && manual:
		a.logLocked("app", "✓ Установлена последняя версия "+appVersion)
	}
	a.pushStateLocked()
	if u.Available && a.settings.AutoUpdate && !a.background {
		go func() {
			time.Sleep(3 * time.Second)
			if err := a.InstallUpdate(); err != nil {
				a.log("error", "Автообновление: "+err.Error())
			}
		}()
	}
}

func fetchLatest() (*ghRelease, error) {
	resp, err := ghGet(updateAPI())
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode == 404 {
		return nil, errors.New("релизов пока нет")
	}
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("GitHub ответил %d", resp.StatusCode)
	}
	var rel ghRelease
	if err := json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(&rel); err != nil {
		return nil, err
	}
	return &rel, nil
}

// updateLoop: проверка при запуске и каждые 6 часов.
func (a *App) updateLoop() {
	time.Sleep(5 * time.Second)
	for {
		a.mu.Lock()
		on := a.settings.CheckUpdates
		a.mu.Unlock()
		if on {
			a.CheckUpdate(false)
		}
		time.Sleep(6 * time.Hour)
	}
}

func (a *App) setStage(stage string, done int64) {
	a.mu.Lock()
	a.update.Stage, a.update.Done = stage, done
	a.pushStateLocked()
	a.mu.Unlock()
}

// InstallUpdate скачивает и ставит обновление, затем перезапускает программу.
func (a *App) InstallUpdate() error {
	a.mu.Lock()
	u := &a.update
	if u.Installing {
		a.mu.Unlock()
		return errors.New("обновление уже устанавливается")
	}
	if !u.Available || u.assetURL == "" {
		a.mu.Unlock()
		return errors.New("нет доступного обновления")
	}
	if !writableDir(a.baseDir) {
		a.mu.Unlock()
		return errors.New("нет прав на запись в папку программы — переместите её, например, в Документы")
	}
	u.Installing, u.Error, u.Done = true, "", 0
	assetURL, digest, latest := u.assetURL, u.digest, u.Latest
	a.logLocked("app", "⬇ Скачивание обновления "+latest+"…")
	a.pushStateLocked()
	a.mu.Unlock()

	go func() {
		err := a.doUpdate(assetURL, digest, latest)
		if err != nil {
			a.mu.Lock()
			a.update.Installing = false
			a.update.Stage = ""
			a.update.Error = "Обновление не установлено: " + err.Error()
			a.logLocked("error", a.update.Error)
			a.pushStateLocked()
			a.mu.Unlock()
		}
	}()
	return nil
}

type dlProgress struct {
	a *App
	n int64
	t time.Time
}

func (p *dlProgress) Write(b []byte) (int, error) {
	p.n += int64(len(b))
	if time.Since(p.t) > 200*time.Millisecond {
		p.t = time.Now()
		p.a.setStage("download", p.n)
	}
	return len(b), nil
}

func (a *App) doUpdate(assetURL, digest, latest string) error {
	// 1. скачивание
	resp, err := ghGet(assetURL)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return fmt.Errorf("скачивание: HTTP %d", resp.StatusCode)
	}
	tmp, err := os.CreateTemp(a.cfgDir, "update-*.zip")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	h := sha256.New()
	_, err = io.Copy(io.MultiWriter(tmp, h, &dlProgress{a: a}), resp.Body)
	tmp.Close()
	if err != nil {
		return err
	}

	// 2. проверка контрольной суммы (GitHub отдаёт digest "sha256:…" для ассетов)
	a.setStage("verify", 0)
	if want, ok := strings.CutPrefix(digest, "sha256:"); ok {
		if got := hex.EncodeToString(h.Sum(nil)); !strings.EqualFold(got, want) {
			return errors.New("контрольная сумма не совпала — файл повреждён")
		}
	}
	zr, err := zip.OpenReader(tmp.Name())
	if err != nil {
		return fmt.Errorf("архив повреждён: %w", err)
	}
	defer zr.Close()
	selfName := exeName("GnirehtetSquad")
	files := map[string]*zip.File{}
	for _, f := range zr.File {
		if f.FileInfo().IsDir() {
			continue
		}
		name := filepath.ToSlash(f.Name)
		if i := strings.IndexByte(name, '/'); i >= 0 { // убираем корневую папку GnirehtetSquad/
			name = name[i+1:]
		}
		if name == "" || strings.Contains(name, "..") || strings.Contains(name, "/") {
			continue // берём только файлы верхнего уровня
		}
		files[name] = f
	}
	if files[selfName] == nil {
		return errors.New("в архиве нет " + selfName)
	}

	// 3. остановка relay — gnirehtet.exe тоже заменяется
	a.mu.Lock()
	wasRunning := a.running || a.starting
	a.mu.Unlock()
	a.setStage("install", 0)
	if wasRunning {
		a.Stop(false)
		time.Sleep(800 * time.Millisecond)
	}

	// 4. замена: старый файл → .old (работающий exe Windows позволяет переименовать), новый — на его место
	var replaced []string
	rollback := func() {
		for _, p := range replaced {
			os.Remove(p)
			os.Rename(p+".old", p)
		}
	}
	for name, f := range files {
		dst := filepath.Join(a.baseDir, name)
		if fileExists(dst) {
			os.Remove(dst + ".old")
			if err := os.Rename(dst, dst+".old"); err != nil {
				rollback()
				return fmt.Errorf("не удалось заменить %s: %w", name, err)
			}
		}
		replaced = append(replaced, dst)
		if err := extractFile(f, dst); err != nil {
			rollback()
			return fmt.Errorf("запись %s: %w", name, err)
		}
	}

	// 5. перезапуск новой версии
	a.setStage("restart", 0)
	a.log("app", "✓ Обновление "+latest+" установлено, перезапуск…")
	args := []string{"--after-update"}
	if wasRunning {
		args = append(args, "--start-relay")
	}
	if a.background {
		args = append(args, "--background")
	}
	cmd := exec.Command(filepath.Join(a.baseDir, selfName), args...)
	cmd.Dir = a.baseDir
	if err := cmd.Start(); err != nil {
		rollback()
		return fmt.Errorf("не удалось запустить новую версию: %w", err)
	}
	go func() {
		time.Sleep(300 * time.Millisecond)
		a.exitNow()
	}()
	return nil
}

func extractFile(f *zip.File, dst string) error {
	rc, err := f.Open()
	if err != nil {
		return err
	}
	defer rc.Close()
	out, err := os.OpenFile(dst, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, rc); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

// cleanupOld удаляет *.old, оставшиеся после обновления.
func (a *App) cleanupOld() {
	matches, _ := filepath.Glob(filepath.Join(a.baseDir, "*.old"))
	for _, m := range matches {
		for i := 0; i < 10; i++ { // старый процесс может ещё завершаться
			if os.Remove(m) == nil || !fileExists(m) {
				break
			}
			time.Sleep(500 * time.Millisecond)
		}
	}
}
