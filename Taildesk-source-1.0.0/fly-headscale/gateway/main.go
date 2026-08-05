package main

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"html"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

const (
	adminPrefix = "/opticon/v1/headscale/"
	artifactPrefix = "/opticon/artifacts/v1/"
	inviteAdminPrefix = "/opticon/v1/invitations/"
	bundleAdminPrefix = "/opticon/v1/bundles/"
	invitePublicPrefix = "/opticon/i/"
	maxInviteBody = 64 << 10
	maxBundleChunk = 4 << 20
	maxAdminBody = 1 << 20
)

type gateway struct {
	proxy *httputil.ReverseProxy
	adminSecret []byte
	headscaleKey string
	artifactDir string
	bundleDir string
	inviteDir string
	publicOrigin string
	nonces map[string]time.Time
	nonceMu sync.Mutex
	inviteMu sync.Mutex
	bundleMu sync.Mutex
}

func main() {
	if len(os.Args) == 2 && os.Args[1] == "fix-permissions" {
		if err := fixPermissions("/var/lib/headscale", 65532, 65532); err != nil { log.Fatal(err) }
		return
	}
	if os.Geteuid() == 0 {
		if os.Getenv("OPTICON_MIGRATE_PERMISSIONS") != "1" { log.Fatal("refusing to serve as root") }
		if err := fixPermissions("/var/lib/headscale", 65532, 65532); err != nil { log.Fatal(err) }
		if err := syscall.Setgroups([]int{65532}); err != nil { log.Fatal(err) }
		if err := syscall.Setgid(65532); err != nil { log.Fatal(err) }
		if err := syscall.Setuid(65532); err != nil { log.Fatal(err) }
	}
	if os.Geteuid() != 65532 { log.Fatalf("refusing unexpected runtime uid %d", os.Geteuid()) }
	secret := os.Getenv("OPTICON_ADMIN_HMAC_KEY")
	headscaleKey := os.Getenv("HEADSCALE_API_KEY")
	if len(secret) < 32 || headscaleKey == "" { log.Fatal("required gateway secrets are missing") }

	child := exec.Command("/ko-app/headscale", "serve")
	child.Stdout, child.Stderr = os.Stdout, os.Stderr
	if err := child.Start(); err != nil { log.Fatal(err) }
	defer func() { _ = child.Process.Signal(syscall.SIGTERM); _, _ = child.Process.Wait() }()

	upstream, _ := url.Parse("http://127.0.0.1:8081")
	proxy := httputil.NewSingleHostReverseProxy(upstream)
	originalDirector := proxy.Director
	proxy.Director = func(r *http.Request) {
		clientIP := r.Header.Get("Fly-Client-IP")
		originalDirector(r)
		r.Host = upstream.Host
		r.Header.Del("X-Forwarded-For")
		if net.ParseIP(clientIP) != nil { r.Header.Set("X-Forwarded-For", clientIP) }
		r.Header.Set("X-Forwarded-Proto", "https")
	}
	appName := strings.TrimSpace(os.Getenv("FLY_APP_NAME"))
	if appName == "" { log.Fatal("FLY_APP_NAME is missing") }
	g := &gateway{proxy: proxy, adminSecret: []byte(secret), headscaleKey: headscaleKey,
		artifactDir: "/opt/opticon/artifacts", bundleDir: "/var/lib/headscale/opticon-artifacts", inviteDir: "/var/lib/headscale/opticon-invites",
		publicOrigin: "https://" + appName + ".fly.dev", nonces: make(map[string]time.Time)}
	if err := os.MkdirAll(g.inviteDir, 0700); err != nil { log.Fatal(err) }
	if err := os.MkdirAll(g.bundleDir, 0700); err != nil { log.Fatal(err) }
	if err := migrateBundleUploads("/var/lib/headscale", g.bundleDir); err != nil { log.Fatal(err) }

	server := &http.Server{Addr: "0.0.0.0:8080", Handler: g, ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout: 2 * time.Minute, MaxHeaderBytes: 64 << 10}
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	go func() { <-stop; ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second); defer cancel(); _ = server.Shutdown(ctx) }()
	if err := server.ListenAndServe(); !errors.Is(err, http.ErrServerClosed) { log.Fatal(err) }
}

func (g *gateway) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'")
	if r.URL.Path == "/health" { g.health(w, r); return }
	if r.URL.Path == "/robots.txt" { w.Header().Set("Content-Type", "text/plain"); _, _ = io.WriteString(w, "User-agent: *\nDisallow: /\n"); return }
	if strings.HasPrefix(r.URL.Path, artifactPrefix) { g.artifact(w, r); return }
	if strings.HasPrefix(r.URL.Path, inviteAdminPrefix) { g.invitationAdmin(w, r); return }
	if strings.HasPrefix(r.URL.Path, bundleAdminPrefix) { g.bundleAdmin(w, r); return }
	if strings.HasPrefix(r.URL.Path, invitePublicPrefix) { g.publicInvitation(w, r); return }
	if strings.HasPrefix(r.URL.Path, adminPrefix) { g.admin(w, r); return }
	if isPublicControlRoute(r.Method, r.URL.Path) { g.proxy.ServeHTTP(w, r); return }
	http.NotFound(w, r)
}

func isPublicControlRoute(method, path string) bool {
	if path == "/key" || path == "/verify" || path == "/bootstrap-dns" || path == "/favicon.ico" { return true }
	if strings.HasPrefix(path, "/ts2021") || strings.HasPrefix(path, "/machine/") || strings.HasPrefix(path, "/derp") { return true }
	return method == http.MethodHead && path == "/machine/ping-response"
}

func (g *gateway) health(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
	client := http.Client{Timeout: 2 * time.Second}
	resp, err := client.Get("http://127.0.0.1:8081/health")
	if err != nil || resp.StatusCode != http.StatusOK { http.Error(w, "unhealthy", http.StatusServiceUnavailable); return }
	defer resp.Body.Close()
	w.Header().Set("Content-Type", "application/json")
	_, _ = io.WriteString(w, `{"service":"opticon-control","status":"ok"}`)
}

func (g *gateway) artifact(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
	name := strings.TrimPrefix(r.URL.Path, artifactPrefix)
	if name == "" || filepath.Base(name) != name || strings.ContainsAny(name, `/\\`) { http.NotFound(w, r); return }
	path := filepath.Join(g.artifactDir, name)
	isBundle := strings.HasPrefix(name, "opticon-bundle-")
	var expected bundleArtifact
	if isBundle {
		var err error
		expected, err = g.bundleByFile(name)
		if err != nil { http.NotFound(w, r); return }
		path = filepath.Join(g.bundleDir, name)
	}
	file, err := os.Open(path)
	if err != nil { http.NotFound(w, r); return }
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() || (isBundle && info.Size() != expected.Size) { http.NotFound(w, r); return }
	if isBundle {
		w.Header().Set("Cache-Control", "no-store")
	} else {
		w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
	}
	w.Header().Set("Content-Disposition", fmt.Sprintf("attachment; filename=%q", name))
	http.ServeContent(w, r, name, info.ModTime(), file)
}

var inviteTokenPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{24,128}$`)
var inviteHashPattern = regexp.MustCompile(`^[a-f0-9]{64}$`)
var safeFilePartPattern = regexp.MustCompile(`[^A-Za-z0-9_-]+`)

type hostedInvite struct {
	DeviceName string `json:"deviceName"`
	Role string `json:"role"`
	ExpiresAt time.Time `json:"expiresAt"`
	Ciphertext []byte `json:"ciphertext"`
}

type artifactManifest struct {
	Artifacts []bundleArtifact `json:"artifacts"`
}

type bundleArtifact struct {
	Product string `json:"product"`
	Role string `json:"role"`
	Architecture string `json:"architecture"`
	File string `json:"file"`
	Size int64 `json:"size"`
	SHA256 string `json:"sha256"`
}

func (g *gateway) invitationAdmin(w http.ResponseWriter, r *http.Request) {
	idHash := strings.ToLower(strings.TrimPrefix(r.URL.Path, inviteAdminPrefix))
	if !inviteHashPattern.MatchString(idHash) { http.NotFound(w, r); return }
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxInviteBody))
	if err != nil { http.Error(w, "invalid body", http.StatusBadRequest); return }
	if !g.authenticate(r, body, time.Now()) { http.Error(w, "unauthorized", http.StatusUnauthorized); return }
	path := filepath.Join(g.inviteDir, idHash+".json")
	switch r.Method {
	case http.MethodPut:
		var invite hostedInvite
		if err := json.Unmarshal(body, &invite); err != nil { http.Error(w, "invalid invitation", http.StatusBadRequest); return }
		now := time.Now()
		if strings.TrimSpace(invite.DeviceName) == "" || (invite.Role != "ManagedOnly" && invite.Role != "ControllerAndManaged") ||
			!invite.ExpiresAt.After(now) || invite.ExpiresAt.After(now.Add(366*24*time.Hour)) || len(invite.Ciphertext) < 64 || len(invite.Ciphertext) > maxInviteBody {
			http.Error(w, "invalid invitation", http.StatusBadRequest); return
		}
		encoded, err := json.Marshal(invite)
		if err != nil { http.Error(w, "invalid invitation", http.StatusBadRequest); return }
		g.inviteMu.Lock()
		defer g.inviteMu.Unlock()
		temporary := path + ".tmp"
		if err := os.WriteFile(temporary, encoded, 0600); err != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
		if err := os.Rename(temporary, path); err != nil { _ = os.Remove(temporary); http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
		writeJSON(w, http.StatusCreated, map[string]any{"stored": true, "expiresAt": invite.ExpiresAt})
	case http.MethodDelete:
		g.inviteMu.Lock()
		err := os.Remove(path)
		g.inviteMu.Unlock()
		if err != nil && !os.IsNotExist(err) { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
		w.WriteHeader(http.StatusNoContent)
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func (g *gateway) bundleAdmin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPut && r.Method != http.MethodDelete { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
	name := strings.TrimPrefix(r.URL.Path, bundleAdminPrefix)
	if filepath.Base(name) != name || !strings.HasPrefix(name, "opticon-bundle-") || !strings.HasSuffix(name, ".zip") { http.NotFound(w, r); return }
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxBundleChunk))
	if err != nil { http.Error(w, "invalid chunk", http.StatusBadRequest); return }
	if !g.authenticate(r, body, time.Now()) { http.Error(w, "unauthorized", http.StatusUnauthorized); return }
	if r.Method == http.MethodDelete {
		if _, err := g.bundleByFile(name); err == nil {
			if r.URL.Query().Get("upload") != "true" {
				http.Error(w, "declared bundle cannot be deleted", http.StatusConflict)
				return
			}
			g.bundleMu.Lock()
			deletionError := removeIfExists(filepath.Join(g.bundleDir, name+".upload"))
			g.bundleMu.Unlock()
			if deletionError != nil {
				http.Error(w, "storage unavailable", http.StatusInternalServerError)
				return
			}
			w.WriteHeader(http.StatusNoContent)
			return
		}
		g.bundleMu.Lock()
		deletionError := removeIfExists(filepath.Join(g.bundleDir, name))
		if uploadError := removeIfExists(filepath.Join(g.bundleDir, name+".upload")); deletionError == nil { deletionError = uploadError }
		g.bundleMu.Unlock()
		if deletionError != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
		w.WriteHeader(http.StatusNoContent)
		return
	}
	expected, err := g.bundleByFile(name)
	if err != nil { http.NotFound(w, r); return }
	offset, offsetErr := strconv.ParseInt(r.URL.Query().Get("offset"), 10, 64)
	total, totalErr := strconv.ParseInt(r.URL.Query().Get("total"), 10, 64)
	claimedHash := strings.ToLower(r.URL.Query().Get("sha256"))
	if offsetErr != nil || totalErr != nil || offset < 0 || total != expected.Size || claimedHash != strings.ToLower(expected.SHA256) || len(body) == 0 || offset+int64(len(body)) > total || (offset+int64(len(body)) < total && len(body) != maxBundleChunk) {
		http.Error(w, "invalid chunk metadata", http.StatusBadRequest); return
	}
	g.bundleMu.Lock(); defer g.bundleMu.Unlock()
	temporary := filepath.Join(g.bundleDir, name+".upload")
	flags := os.O_CREATE | os.O_WRONLY
	if offset == 0 { flags |= os.O_TRUNC }
	file, err := os.OpenFile(temporary, flags, 0600)
	if err != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
	info, statErr := file.Stat()
	if statErr != nil || info.Size() != offset { _ = file.Close(); http.Error(w, "unexpected chunk offset", http.StatusConflict); return }
	if _, err = file.WriteAt(body, offset); err == nil { err = file.Sync() }
	if closeErr := file.Close(); err == nil { err = closeErr }
	if err != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
	if offset+int64(len(body)) < total { writeJSON(w, http.StatusAccepted, map[string]any{"nextOffset": offset + int64(len(body))}); return }
	file, err = os.Open(temporary)
	if err != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
	hasher := sha256.New(); _, hashErr := io.Copy(hasher, file); closeErr := file.Close()
	actualHash := hex.EncodeToString(hasher.Sum(nil))
	if hashErr != nil || closeErr != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
	if actualHash != claimedHash { http.Error(w, "bundle hash verification failed", http.StatusConflict); return }
	finalPath := filepath.Join(g.bundleDir, name)
	if err := os.Rename(temporary, finalPath); err != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
	if err := os.Chmod(finalPath, 0444); err != nil { http.Error(w, "storage unavailable", http.StatusInternalServerError); return }
	writeJSON(w, http.StatusCreated, map[string]any{"stored": true, "sha256": actualHash})
}

func removeIfExists(path string) error {
	err := os.Remove(path)
	if os.IsNotExist(err) { return nil }
	return err
}
func (g *gateway) publicInvitation(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
	suffix := strings.Trim(strings.TrimPrefix(r.URL.Path, invitePublicPrefix), "/")
	parts := strings.Split(suffix, "/")
	if len(parts) < 1 || len(parts) > 2 || !inviteTokenPattern.MatchString(parts[0]) { http.NotFound(w, r); return }
	invite, path, err := g.readHostedInvite(parts[0])
	if err != nil { http.NotFound(w, r); return }
	if !invite.ExpiresAt.After(time.Now()) { _ = os.Remove(path); http.Error(w, "This invitation has expired. Ask for a new Opticon link.", http.StatusGone); return }
	w.Header().Set("Cache-Control", "no-store")
	if len(parts) == 2 {
		if parts[1] != "invite.tdinvite" { http.NotFound(w, r); return }
		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Disposition", `attachment; filename="invite.tdinvite"`)
		w.Header().Set("Content-Length", strconv.Itoa(len(invite.Ciphertext)))
		if r.Method == http.MethodGet { _, _ = w.Write(invite.Ciphertext) }
		return
	}
	bundle, err := g.bundleForRole(invite.Role)
	if err != nil { http.Error(w, "Opticon installer payload is temporarily unavailable.", http.StatusServiceUnavailable); return }
	g.invitationLanding(w, r, parts[0], invite, bundle)
}

func (g *gateway) readHostedInvite(publicID string) (hostedInvite, string, error) {
	hash := sha256.Sum256([]byte(publicID))
	path := filepath.Join(g.inviteDir, hex.EncodeToString(hash[:])+".json")
	g.inviteMu.Lock()
	data, err := os.ReadFile(path)
	g.inviteMu.Unlock()
	if err != nil { return hostedInvite{}, path, err }
	var invite hostedInvite
	if err := json.Unmarshal(data, &invite); err != nil { return hostedInvite{}, path, err }
	return invite, path, nil
}

func (g *gateway) bundleForRole(role string) (bundleArtifact, error) {
	data, err := os.ReadFile(filepath.Join(g.artifactDir, "manifest.json"))
	if err != nil { return bundleArtifact{}, err }
	var manifest artifactManifest
	if err := json.Unmarshal(data, &manifest); err != nil { return bundleArtifact{}, err }
	for _, artifact := range manifest.Artifacts {
		if artifact.Product == "OpticonBundle" && artifact.Role == role && artifact.Architecture == "x64" &&
			filepath.Base(artifact.File) == artifact.File && artifact.Size > 0 && inviteHashPattern.MatchString(strings.ToLower(artifact.SHA256)) {
			return artifact, nil
		}
	}
	return bundleArtifact{}, errors.New("role bundle is not published")
}

func (g *gateway) bundleByFile(name string) (bundleArtifact, error) {
	data, err := os.ReadFile(filepath.Join(g.artifactDir, "manifest.json"))
	if err != nil { return bundleArtifact{}, err }
	var manifest artifactManifest
	if err := json.Unmarshal(data, &manifest); err != nil { return bundleArtifact{}, err }
	for _, artifact := range manifest.Artifacts {
		if artifact.Product == "OpticonBundle" && artifact.File == name && artifact.Size > 0 && inviteHashPattern.MatchString(strings.ToLower(artifact.SHA256)) { return artifact, nil }
	}
	return bundleArtifact{}, errors.New("bundle is not declared")
}
func (g *gateway) invitationLanding(w http.ResponseWriter, r *http.Request, publicID string, invite hostedInvite, bundle bundleArtifact) {
	command := buildInstallerCommand(g.publicOrigin, publicID, bundle)
	commandJSON, _ := json.Marshal(command)
	filePart := safeFilePartPattern.ReplaceAllString(invite.DeviceName, "-")
	if filePart == "" { filePart = "device" }
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'")
	if r.Method == http.MethodHead { return }
	page := fmt.Sprintf(`<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Opticon invitation</title><style>body{font:18px Segoe UI,sans-serif;background:#111316;color:#edf1f5;max-width:720px;margin:10vh auto;padding:28px}button{background:#52d39a;color:#08130e;border:0;padding:14px 20px;font-weight:700;font-size:17px;border-radius:6px;cursor:pointer}.muted{color:#9da7b1}code{color:#52d39a}</style></head><body><h1>Install Opticon</h1><p>This private invitation is for <strong>%s</strong>.</p><p id="status">Your tiny one-click starter will download automatically. Open it, then approve the Windows administrator prompt.</p><button id="download">Download starter</button><p class="muted">The link expires at <code>%s</code>. It can enroll only one machine. No router changes are required.</p><script>const key=location.hash.slice(1);const status=document.getElementById('status');const button=document.getElementById('download');const template=%s;function download(){if(!/^[A-Za-z0-9_-]{32,128}$/.test(key)){status.textContent='This invitation link is incomplete. Ask for a new link.';button.disabled=true;return;}const blob=new Blob([template.replace('__OPTICON_FRAGMENT_KEY__',key)],{type:'application/octet-stream'});const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='Install-Opticon-%s.cmd';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),5000);status.textContent='Downloaded. Open Install-Opticon-%s.cmd to continue.';}button.addEventListener('click',download);setTimeout(download,350);</script></body></html>`, html.EscapeString(invite.DeviceName), invite.ExpiresAt.Local().Format(time.RFC1123), string(commandJSON), filePart, filePart)
	_, _ = io.WriteString(w, page)
}

func buildInstallerCommand(origin, publicID string, bundle bundleArtifact) string {
	template := `@echo off
setlocal
set "OPTICON_DIR=%TEMP%\Opticon-__PUBLIC_ID__"
if exist "%OPTICON_DIR%" rmdir /s /q "%OPTICON_DIR%"
mkdir "%OPTICON_DIR%" || exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';function Get-OpticonFile([string]$url,[string]$path){$curl=Get-Command curl.exe -ErrorAction SilentlyContinue;if($curl){& $curl.Source --fail --location --silent --show-error --retry 3 --retry-delay 1 --connect-timeout 20 --output $path $url;if($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $path)){return};Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue};Invoke-WebRequest $url -OutFile $path -UseBasicParsing};$dir=$env:OPTICON_DIR;$invite=Join-Path $dir 'invite.tdinvite';$bundle=Join-Path $dir 'opticon-bundle.zip';Get-OpticonFile '__INVITE_URL__' $invite;Get-OpticonFile '__BUNDLE_URL__' $bundle;$info=Get-Item -LiteralPath $bundle;if($info.Length -ne __BUNDLE_SIZE__){throw 'Opticon bundle size verification failed'};$hash=(Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant();if($hash -ne '__BUNDLE_HASH__'){throw 'Opticon bundle hash verification failed'};Expand-Archive -LiteralPath $bundle -DestinationPath $dir -Force;$setup=Join-Path $dir 'Taildesk.Setup.exe';$sig=Get-AuthenticodeSignature -LiteralPath $setup;if(-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or $sig.Status -in @('HashMismatch','NotSigned')){throw 'Opticon Setup signature verification failed'};$q=[char]34;$args='--hosted-invite='+$q+$invite+$q+' --invite-key=__OPTICON_FRAGMENT_KEY__';$p=Start-Process -FilePath $setup -ArgumentList $args -Verb RunAs -Wait -PassThru;if($p.ExitCode -ne 0){throw ('Opticon Setup returned '+$p.ExitCode)}"
if errorlevel 1 (echo. & echo Opticon could not start. Ask for a new invitation link. & pause & exit /b 1)
rmdir /s /q "%OPTICON_DIR%" 2>nul
del "%~f0"
`
	replacer := strings.NewReplacer(
		"__PUBLIC_ID__", publicID[:12],
		"__INVITE_URL__", origin+invitePublicPrefix+publicID+"/invite.tdinvite",
		"__BUNDLE_URL__", origin+artifactPrefix+bundle.File,
		"__BUNDLE_SIZE__", strconv.FormatInt(bundle.Size, 10),
		"__BUNDLE_HASH__", strings.ToLower(bundle.SHA256),
	)
	return replacer.Replace(template)
}
func (g *gateway) admin(w http.ResponseWriter, r *http.Request) {
	if !isAllowedAdminRoute(r.Method, strings.TrimPrefix(r.URL.Path, adminPrefix)) { http.NotFound(w, r); return }
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxAdminBody))
	if err != nil { http.Error(w, "invalid body", http.StatusBadRequest); return }
	if !g.authenticate(r, body, time.Now()) { http.Error(w, "unauthorized", http.StatusUnauthorized); return }
	r.URL.Path = "/" + strings.TrimPrefix(r.URL.Path, adminPrefix)
	r.URL.RawPath = ""
	r.Body = io.NopCloser(bytes.NewReader(body))
	r.ContentLength = int64(len(body))
	r.Header.Set("Authorization", "Bearer "+g.headscaleKey)
	for _, name := range []string{"X-Opticon-Key-Id", "X-Opticon-Timestamp", "X-Opticon-Nonce", "X-Opticon-Content-SHA256", "X-Opticon-Signature"} { r.Header.Del(name) }
	g.proxy.ServeHTTP(w, r)
}

func isAllowedAdminRoute(method, path string) bool {
	if method == http.MethodGet && path == "api/v1/node" { return true }
	if method == http.MethodPost && (path == "api/v1/preauthkey" || path == "api/v1/preauthkey/expire") { return true }
	parts := strings.Split(path, "/")
	if len(parts) == 5 && parts[0] == "api" && parts[1] == "v1" && parts[2] == "node" && parts[3] != "" {
		return method == http.MethodPost && (parts[4] == "tags" || parts[4] == "approve_routes")
	}
	return len(parts) == 4 && parts[0] == "api" && parts[1] == "v1" && parts[2] == "node" && parts[3] != "" && method == http.MethodDelete
}

func (g *gateway) authenticate(r *http.Request, body []byte, now time.Time) bool {
	if r.Header.Get("X-Opticon-Key-Id") != "primary" { return false }
	timestampText, nonce := r.Header.Get("X-Opticon-Timestamp"), r.Header.Get("X-Opticon-Nonce")
	timestamp, err := strconv.ParseInt(timestampText, 10, 64)
	if err != nil || len(nonce) < 20 || abs(now.Unix()-timestamp) > 300 { return false }
	hash := sha256.Sum256(body)
	hashText := hex.EncodeToString(hash[:])
	if !hmac.Equal([]byte(hashText), []byte(strings.ToLower(r.Header.Get("X-Opticon-Content-SHA256")))) { return false }
	canonical := strings.Join([]string{r.Method, r.URL.RequestURI(), timestampText, nonce, hashText}, "\n")
	expected := hmac.New(sha256.New, g.adminSecret); _, _ = expected.Write([]byte(canonical))
	provided, err := hex.DecodeString(r.Header.Get("X-Opticon-Signature"))
	if err != nil || !hmac.Equal(provided, expected.Sum(nil)) { return false }
	g.nonceMu.Lock(); defer g.nonceMu.Unlock()
	for key, expiry := range g.nonces { if now.After(expiry) { delete(g.nonces, key) } }
	if _, exists := g.nonces[nonce]; exists { return false }
	g.nonces[nonce] = now.Add(10 * time.Minute)
	return true
}

func abs(value int64) int64 { if value < 0 { return -value }; return value }

func migrateBundleUploads(stagingDir, bundleDir string) error {
	entries, err := os.ReadDir(stagingDir)
	if err != nil { return err }
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasPrefix(entry.Name(), "opticon-bundle-") || !strings.HasSuffix(entry.Name(), ".zip.upload") { continue }
		finalName := strings.TrimSuffix(entry.Name(), ".upload")
		if filepath.Base(finalName) != finalName { continue }
		if err := os.Rename(filepath.Join(stagingDir, entry.Name()), filepath.Join(bundleDir, finalName)); err != nil { return err }
	}
	return nil
}
func fixPermissions(root string, uid, gid int) error {
	if os.Geteuid() != 0 { return errors.New("fix-permissions must run as root") }
	return filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err != nil { return err }
		return os.Chown(path, uid, gid)
	})
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
