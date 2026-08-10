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
	adminPrefix               = "/opticon/v1/headscale/"
	artifactPrefix            = "/opticon/artifacts/v1/"
	inviteAdminPrefix         = "/opticon/v1/invitations/"
	bundleAdminPrefix         = "/opticon/v1/bundles/"
	releaseAdminPath          = "/opticon/v1/releases/manifest"
	invitePublicPrefix        = "/opticon/i/"
	maxInviteBody             = 64 << 10
	maxBundleChunk            = 4 << 20
	maxAdminBody              = 1 << 20
	maxBootstrapArtifactBytes = 128 << 20
	pinnedSDKVersion          = "10.0.302"
	pinnedRuntimeVersion      = "10.0.10"
	sourceOnlyManifestSchema  = 2
	sourceInstallProtocol     = "source-v1"
	invitationSigningKeyID    = "FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53"
	// The retired invitation signer is allowed only for this immutable ACL
	// transition package. It is never a general release signing channel.
	legacyMachineStateMigrationBridgeVersion = "1.1.41"
)

var trustedSourceManifestKeyID string
var trustedProductSignerThumbprint string
var trustedSigningProfile string

type gateway struct {
	proxy         *httputil.ReverseProxy
	adminSecret   []byte
	headscaleKey  string
	sourceSigner  sourceDownloadSigner
	now           func() time.Time
	artifactDir   string
	manifestPath  string
	bundleDir     string
	inviteDir     string
	nonceDir      string
	publicOrigin  string
	nonces        map[string]time.Time
	nonceMu       sync.Mutex
	inviteMu      sync.Mutex
	bundleMu      sync.Mutex
	manifestMu    sync.RWMutex
	adminSlots    chan struct{}
	artifactSlots chan struct{}
	proxySlots    chan struct{}
	streamSlots   chan struct{}
}

func main() {
	if len(os.Args) == 2 && os.Args[1] == "fix-permissions" {
		if err := fixPermissions("/var/lib/headscale", 65532, 65532); err != nil {
			log.Fatal(err)
		}
		return
	}
	if os.Geteuid() == 0 {
		if os.Getenv("OPTICON_MIGRATE_PERMISSIONS") != "1" {
			log.Fatal("refusing to serve as root")
		}
		if err := fixPermissions("/var/lib/headscale", 65532, 65532); err != nil {
			log.Fatal(err)
		}
		if err := setRuntimeIdentity(65532, 65532); err != nil {
			log.Fatal(err)
		}
	}
	if os.Geteuid() != 65532 {
		log.Fatalf("refusing unexpected runtime uid %d", os.Geteuid())
	}
	secret := os.Getenv("OPTICON_ADMIN_HMAC_KEY")
	headscaleKey := os.Getenv("HEADSCALE_API_KEY")
	if len(secret) < 32 || headscaleKey == "" {
		log.Fatal("required gateway secrets are missing")
	}
	if err := configureProductionTrust(); err != nil {
		log.Fatal(err)
	}
	sourceSigner, err := newS3SourceDownloadSignerFromEnvironment()
	if err != nil {
		log.Fatal(err)
	}

	child := exec.Command("/ko-app/headscale", "serve")
	child.Env = make([]string, 0, len(os.Environ()))
	for _, item := range os.Environ() {
		name := strings.SplitN(item, "=", 2)[0]
		if name == "OPTICON_ADMIN_HMAC_KEY" || name == "HEADSCALE_API_KEY" ||
			name == "OPTICON_S3_ACCESS_KEY_ID" || name == "OPTICON_S3_SECRET_ACCESS_KEY" || name == "OPTICON_S3_SESSION_TOKEN" {
			continue
		}
		child.Env = append(child.Env, item)
	}
	child.Stdout, child.Stderr = os.Stdout, os.Stderr
	if err := child.Start(); err != nil {
		log.Fatal(err)
	}
	defer func() { _ = child.Process.Signal(syscall.SIGTERM); _, _ = child.Process.Wait() }()

	upstream, _ := url.Parse("http://127.0.0.1:8081")
	proxy := httputil.NewSingleHostReverseProxy(upstream)
	originalDirector := proxy.Director
	proxy.Director = func(r *http.Request) {
		clientIP := r.Header.Get("Fly-Client-IP")
		originalDirector(r)
		r.Host = upstream.Host
		r.Header.Del("X-Forwarded-For")
		if net.ParseIP(clientIP) != nil {
			r.Header.Set("X-Forwarded-For", clientIP)
		}
		r.Header.Set("X-Forwarded-Proto", "https")
	}
	appName := strings.TrimSpace(os.Getenv("FLY_APP_NAME"))
	if appName == "" {
		log.Fatal("FLY_APP_NAME is missing")
	}
	g := &gateway{proxy: proxy, adminSecret: []byte(secret), headscaleKey: headscaleKey, sourceSigner: sourceSigner, now: time.Now,
		artifactDir: "/opt/opticon/artifacts", bundleDir: "/var/lib/headscale/opticon-artifacts", inviteDir: "/var/lib/headscale/opticon-invites",
		nonceDir:     "/var/lib/headscale/opticon-nonces",
		manifestPath: "/var/lib/headscale/opticon-release/manifest.json",
		publicOrigin: "https://" + appName + ".fly.dev", nonces: make(map[string]time.Time),
		adminSlots: make(chan struct{}, 16), artifactSlots: make(chan struct{}, 8),
		proxySlots: make(chan struct{}, 64), streamSlots: make(chan struct{}, 64)}
	if err := os.MkdirAll(g.inviteDir, 0700); err != nil {
		log.Fatal(err)
	}
	if err := os.MkdirAll(g.bundleDir, 0700); err != nil {
		log.Fatal(err)
	}
	if err := os.MkdirAll(g.nonceDir, 0700); err != nil {
		log.Fatal(err)
	}
	if err := seedDynamicManifest(g.manifestPath, filepath.Join(g.artifactDir, "manifest.json")); err != nil {
		log.Fatal(err)
	}
	if err := migrateBundleUploads("/var/lib/headscale", g.artifactDir, g.bundleDir); err != nil {
		log.Fatal(err)
	}
	// Legacy volume bundles are retained as an emergency migration fallback.
	// New releases are selected through immutable CloudFront URLs instead.

	server := &http.Server{Addr: "0.0.0.0:8080", Handler: g, ReadHeaderTimeout: 10 * time.Second, ReadTimeout: 30 * time.Second,
		IdleTimeout: 2 * time.Minute, MaxHeaderBytes: 64 << 10}
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-stop
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		_ = server.Shutdown(ctx)
	}()
	if err := server.ListenAndServe(); !errors.Is(err, http.ErrServerClosed) {
		log.Fatal(err)
	}
}

func (g *gateway) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'")
	if r.URL.Path == "/health" {
		g.health(w, r)
		return
	}
	if r.URL.Path == "/robots.txt" {
		w.Header().Set("Content-Type", "text/plain")
		_, _ = io.WriteString(w, "User-agent: *\nDisallow: /\n")
		return
	}
	if r.URL.Path == releaseAdminPath {
		g.releaseManifestAdmin(w, r)
		return
	}
	if strings.HasPrefix(r.URL.Path, artifactPrefix) {
		g.artifact(w, r)
		return
	}
	if strings.HasPrefix(r.URL.Path, inviteAdminPrefix) {
		g.invitationAdmin(w, r)
		return
	}
	if strings.HasPrefix(r.URL.Path, bundleAdminPrefix) {
		g.bundleAdmin(w, r)
		return
	}
	if strings.HasPrefix(r.URL.Path, invitePublicPrefix) {
		g.publicInvitation(w, r)
		return
	}
	if strings.HasPrefix(r.URL.Path, adminPrefix) {
		g.admin(w, r)
		return
	}
	if isPublicControlRoute(r.Method, r.URL.Path) {
		g.servePublicControl(w, r)
		return
	}
	http.NotFound(w, r)
}

func (g *gateway) servePublicControl(w http.ResponseWriter, r *http.Request) {
	slots := g.proxySlots
	if strings.HasPrefix(r.URL.Path, "/derp") || strings.HasPrefix(r.URL.Path, "/ts2021") {
		slots = g.streamSlots
	}
	if slots != nil {
		select {
		case slots <- struct{}{}:
			defer func() { <-slots }()
		default:
			w.Header().Set("Retry-After", "5")
			http.Error(w, "control service is busy", http.StatusTooManyRequests)
			return
		}
	}
	if g.proxy == nil {
		http.Error(w, "control service unavailable", http.StatusServiceUnavailable)
		return
	}
	// DERP, ts2021, and machine-map responses can legitimately remain open.
	// Their resource bound is the route-specific slot, not a global writer
	// timeout that would sever healthy Tailscale sessions.
	g.proxy.ServeHTTP(w, r)
}

func (g *gateway) readAdminBody(w http.ResponseWriter, r *http.Request, maximum int64) ([]byte, error) {
	if g.adminSlots != nil {
		select {
		case g.adminSlots <- struct{}{}:
			defer func() { <-g.adminSlots }()
		default:
			return nil, errAdminBusy
		}
	}
	controller := http.NewResponseController(w)
	deadlineSet := controller.SetReadDeadline(time.Now().Add(30*time.Second)) == nil
	if deadlineSet {
		defer func() { _ = controller.SetReadDeadline(time.Time{}) }()
	}
	return io.ReadAll(http.MaxBytesReader(w, r.Body, maximum))
}

func isPublicControlRoute(method, path string) bool {
	if path == "/key" || path == "/verify" || path == "/bootstrap-dns" || path == "/favicon.ico" {
		return true
	}
	if strings.HasPrefix(path, "/ts2021") || strings.HasPrefix(path, "/machine/") || strings.HasPrefix(path, "/derp") {
		return true
	}
	return method == http.MethodHead && path == "/machine/ping-response"
}

func (g *gateway) health(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	client := http.Client{Timeout: 2 * time.Second}
	resp, err := client.Get("http://127.0.0.1:8081/health")
	if err != nil || resp.StatusCode != http.StatusOK {
		http.Error(w, "unhealthy", http.StatusServiceUnavailable)
		return
	}
	defer resp.Body.Close()
	w.Header().Set("Content-Type", "application/json")
	_, _ = io.WriteString(w, `{"service":"opticon-control","status":"ok"}`)
}

func (g *gateway) artifact(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	name := strings.TrimPrefix(r.URL.Path, artifactPrefix)
	if name == "" || filepath.Base(name) != name || strings.ContainsAny(name, `/\\`) {
		http.NotFound(w, r)
		return
	}
	if name == "manifest.json" {
		g.publicArtifactManifest(w, r)
		return
	}
	if g.artifactSlots != nil {
		select {
		case g.artifactSlots <- struct{}{}:
			defer func() { <-g.artifactSlots }()
		default:
			http.Error(w, "artifact service is busy", http.StatusTooManyRequests)
			return
		}
	}
	path := filepath.Join(g.artifactDir, name)
	isBundle := strings.HasPrefix(name, "opticon-bundle-")
	var expected bundleArtifact
	if isBundle {
		if !safeBundleFilePattern.MatchString(name) {
			http.NotFound(w, r)
			return
		}
		var err error
		expected, err = g.bundleByFile(name)
		if err != nil {
			http.NotFound(w, r)
			return
		}
		path = filepath.Join(g.bundleDir, name)
	} else {
		var err error
		expected, err = g.artifactByFile(name)
		if err != nil {
			http.NotFound(w, r)
			return
		}
	}
	file, err := os.Open(path)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() || info.Size() != expected.Size {
		http.NotFound(w, r)
		return
	}
	if isBundle {
		w.Header().Set("Cache-Control", "no-store")
	} else {
		w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
	}
	w.Header().Set("Content-Disposition", fmt.Sprintf("attachment; filename=%q", name))
	deadlineWriter := newIdleDeadlineWriter(w, 30*time.Second)
	defer deadlineWriter.clear()
	http.ServeContent(deadlineWriter, r, name, info.ModTime(), file)
}

type idleDeadlineWriter struct {
	http.ResponseWriter
	controller *http.ResponseController
	idle       time.Duration
}

func newIdleDeadlineWriter(w http.ResponseWriter, idle time.Duration) *idleDeadlineWriter {
	writer := &idleDeadlineWriter{ResponseWriter: w, controller: http.NewResponseController(w), idle: idle}
	writer.refresh()
	return writer
}

func (w *idleDeadlineWriter) Write(p []byte) (int, error) {
	w.refresh()
	return w.ResponseWriter.Write(p)
}

func (w *idleDeadlineWriter) Unwrap() http.ResponseWriter { return w.ResponseWriter }

func (w *idleDeadlineWriter) refresh() {
	_ = w.controller.SetWriteDeadline(time.Now().Add(w.idle))
}

func (w *idleDeadlineWriter) clear() {
	_ = w.controller.SetWriteDeadline(time.Time{})
}

var inviteTokenPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{24,128}$`)
var inviteHashPattern = regexp.MustCompile(`^[a-f0-9]{64}$`)
var safeFilePartPattern = regexp.MustCompile(`[^A-Za-z0-9_-]+`)
var safeBundleFilePattern = regexp.MustCompile(`^opticon-bundle-[A-Za-z0-9][A-Za-z0-9._-]{0,190}\.zip$`)
var safeBootstrapFilePattern = regexp.MustCompile(`^opticon-bootstrap-[0-9]+\.[0-9]+\.[0-9]+\.exe$`)
var safeSourceFilePattern = regexp.MustCompile(`^opticon-source-[0-9]+\.[0-9]+\.[0-9]+\.zip$`)
var publisherThumbprintPattern = regexp.MustCompile(`^[A-Fa-f0-9]{40}$`)
var errAdminBusy = errors.New("too many concurrent administrative requests")

func configureProductionTrust() error {
	sourceKey := strings.ToUpper(strings.TrimSpace(os.Getenv("OPTICON_SOURCE_RELEASE_KEY_ID")))
	productSigner := strings.ToUpper(strings.TrimSpace(os.Getenv("OPTICON_PRODUCT_SIGNER_THUMBPRINT")))
	signingProfile := strings.TrimSpace(os.Getenv("OPTICON_SIGNING_PROFILE"))
	if signingProfile != "Production" && signingProfile != "OwnerManaged" {
		return errors.New("production signing profile must be Production or OwnerManaged")
	}
	if !publisherThumbprintPattern.MatchString(sourceKey) || !publisherThumbprintPattern.MatchString(productSigner) {
		return errors.New("production source-release and product signer trust pins are missing or invalid")
	}
	if sourceKey == invitationSigningKeyID || productSigner == invitationSigningKeyID || sourceKey == productSigner {
		return errors.New("source-release, product-signing, and invitation trust domains must be distinct")
	}
	trustedSourceManifestKeyID = sourceKey
	trustedProductSignerThumbprint = productSigner
	trustedSigningProfile = signingProfile
	return nil
}

func validProductionArtifactTrust(artifact bundleArtifact) bool {
	if artifact.SigningProfile != trustedSigningProfile || !publisherThumbprintPattern.MatchString(artifact.SourceManifestKeyID) ||
		!publisherThumbprintPattern.MatchString(artifact.ProductSigner) || artifact.SourceManifestKeyID == invitationSigningKeyID ||
		artifact.ProductSigner == invitationSigningKeyID || artifact.SourceManifestKeyID == artifact.ProductSigner {
		return false
	}
	if trustedSourceManifestKeyID != "" && artifact.SourceManifestKeyID != trustedSourceManifestKeyID {
		return false
	}
	return trustedProductSignerThumbprint == "" || artifact.ProductSigner == trustedProductSignerThumbprint
}

type hostedInvite struct {
	DeviceName string    `json:"deviceName"`
	Role       string    `json:"role"`
	ExpiresAt  time.Time `json:"expiresAt"`
	// InstallProtocol is explicit so that a source-only invitation cannot be
	// silently interpreted as an older bootstrap-and-binary invitation.
	InstallProtocol      string   `json:"installProtocol"`
	ReleaseVersion       string   `json:"releaseVersion"`
	SourceSHA256         string   `json:"sourceSha256"`
	SourceFile           string   `json:"sourceFile"`
	SourceSize           int64    `json:"sourceSize"`
	SourceManifestSHA256 string   `json:"sourceManifestSha256"`
	SourceManifestKeyID  string   `json:"sourceManifestKeyId"`
	SigningProfile       string   `json:"signingProfile"`
	ProductSigner        string   `json:"productSignerThumbprint"`
	SDKVersion           string   `json:"sdkVersion"`
	RuntimeVersion       string   `json:"runtimeVersion"`
	TargetRuntime        string   `json:"targetRuntime"`
	TargetRuntimes       []string `json:"targetRuntimes"`
	BootstrapVersion     string   `json:"bootstrapVersion"`
	BootstrapFile        string   `json:"bootstrapFile"`
	BootstrapSize        int64    `json:"bootstrapSize"`
	BootstrapSHA256      string   `json:"bootstrapSha256"`
	BootstrapSigner      string   `json:"bootstrapSignerThumbprint"`
	Ciphertext           []byte   `json:"ciphertext"`
}

type artifactManifest struct {
	SchemaVersion int              `json:"schemaVersion"`
	Artifacts     []bundleArtifact `json:"artifacts"`
}

type bundleArtifact struct {
	Product                         string   `json:"product"`
	Version                         string   `json:"version"`
	Role                            string   `json:"role,omitempty"`
	Architecture                    string   `json:"architecture"`
	File                            string   `json:"file"`
	Size                            int64    `json:"size"`
	SHA256                          string   `json:"sha256"`
	SignerThumbprint                string   `json:"signerThumbprint,omitempty"`
	DownloadURL                     string   `json:"downloadUrl,omitempty"`
	SDKVersion                      string   `json:"sdkVersion,omitempty"`
	RuntimeVersion                  string   `json:"runtimeVersion,omitempty"`
	SourceManifestSHA256            string   `json:"sourceManifestSha256,omitempty"`
	SourceManifestKeyID             string   `json:"sourceManifestKeyId,omitempty"`
	SourceLauncherFile              string   `json:"sourceLauncherFile,omitempty"`
	SourceLauncherSize              int64    `json:"sourceLauncherSize,omitempty"`
	SourceLauncherSHA256            string   `json:"sourceLauncherSha256,omitempty"`
	SigningProfile                  string   `json:"signingProfile,omitempty"`
	ProductSigner                   string   `json:"productSignerThumbprint,omitempty"`
	LegacyMigrationSignerThumbprint string   `json:"legacyMigrationSignerThumbprint,omitempty"`
	TargetRuntime                   string   `json:"targetRuntime,omitempty"`
	TargetRuntimes                  []string `json:"targetRuntimes,omitempty"`
}

func (g *gateway) publicArtifactManifest(w http.ResponseWriter, r *http.Request) {
	manifest, err := g.readArtifactManifest()
	if err != nil {
		http.Error(w, "release manifest unavailable", http.StatusServiceUnavailable)
		return
	}
	available := make([]bundleArtifact, 0, len(manifest.Artifacts))
	for _, artifact := range manifest.Artifacts {
		if artifact.Product != "OpticonBundle" || g.bundleIsAvailable(artifact) {
			available = append(available, artifact)
		}
	}
	manifest.Artifacts = available
	encoded, err := json.Marshal(manifest)
	if err != nil {
		http.Error(w, "release manifest unavailable", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Cache-Control", "no-store, max-age=0")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Content-Length", strconv.Itoa(len(encoded)))
	w.Header().Set("Content-Disposition", `attachment; filename="manifest.json"`)
	if r.Method == http.MethodHead {
		w.WriteHeader(http.StatusOK)
		return
	}
	_, _ = w.Write(encoded)
}

func (g *gateway) readArtifactManifest() (artifactManifest, error) {
	g.manifestMu.RLock()
	defer g.manifestMu.RUnlock()
	return g.readArtifactManifestUnlocked()
}

func (g *gateway) readArtifactManifestUnlocked() (artifactManifest, error) {
	manifest, err := g.readStoredArtifactManifestUnlocked()
	if err != nil {
		return artifactManifest{}, err
	}
	if err := validateArtifactManifest(manifest); err != nil {
		return artifactManifest{}, err
	}
	return manifest, nil
}

// readStoredArtifactManifestUnlocked performs only the bounded structural read
// needed to migrate an older, now-untrusted release manifest. Callers must not
// serve or select artifacts from this result without validateArtifactManifest.
func (g *gateway) readStoredArtifactManifestUnlocked() (artifactManifest, error) {
	data, err := os.ReadFile(g.artifactManifestPath())
	if err != nil {
		return artifactManifest{}, err
	}
	if len(data) == 0 || len(data) > maxAdminBody {
		return artifactManifest{}, errors.New("release manifest size is invalid")
	}
	var manifest artifactManifest
	if err := json.Unmarshal(data, &manifest); err != nil {
		return artifactManifest{}, err
	}
	if (manifest.SchemaVersion != 1 && manifest.SchemaVersion != sourceOnlyManifestSchema) || len(manifest.Artifacts) < 1 || len(manifest.Artifacts) > 1024 {
		return artifactManifest{}, errors.New("release manifest structure is invalid")
	}
	return manifest, nil
}

func (g *gateway) artifactManifestPath() string {
	if g.manifestPath != "" {
		return g.manifestPath
	}
	return filepath.Join(g.artifactDir, "manifest.json")
}

func validateArtifactManifest(manifest artifactManifest) error {
	if manifest.SchemaVersion != 1 && manifest.SchemaVersion != sourceOnlyManifestSchema {
		return errors.New("release manifest schema is unsupported")
	}
	if len(manifest.Artifacts) < 1 || len(manifest.Artifacts) > 1024 {
		return errors.New("release manifest artifact count is invalid")
	}
	seenFiles := make(map[string]struct{})
	for _, artifact := range manifest.Artifacts {
		key := strings.ToLower(artifact.File)
		if key == "" {
			return errors.New("release manifest contains an empty filename")
		}
		if _, duplicate := seenFiles[key]; duplicate {
			return errors.New("release manifest contains a duplicate artifact filename")
		}
		seenFiles[key] = struct{}{}
		switch artifact.Product {
		case "OpticonBundle":
			if manifest.SchemaVersion == sourceOnlyManifestSchema ||
				(!validBundleArtifact(artifact) || (artifact.DownloadURL != "" && !validCloudFrontDownloadURL(artifact))) {
				return errors.New("release manifest contains an invalid Opticon bundle")
			}
		case "OpticonBootstrap":
			if manifest.SchemaVersion == sourceOnlyManifestSchema || !validBootstrapArtifact(artifact) {
				return errors.New("release manifest contains an invalid Opticon bootstrap")
			}
		case "OpticonSource":
			if !validSourceArtifact(artifact) || (manifest.SchemaVersion == sourceOnlyManifestSchema && artifact.LegacyMigrationSignerThumbprint != "") {
				return errors.New("release manifest contains an invalid Opticon source archive")
			}
		default:
			if manifest.SchemaVersion == sourceOnlyManifestSchema {
				return errors.New("source-only release manifest contains a non-source artifact")
			}
		}
	}
	if manifest.SchemaVersion == sourceOnlyManifestSchema {
		return validateSourceOnlyArtifactManifest(manifest)
	}
	return nil
}

// validateSourceOnlyArtifactManifest enforces the new release contract: each
// version is represented by exactly one immutable, signed source archive. It
// deliberately refuses installers, payload bundles, and dependency binaries so
// that S3 never becomes a second executable delivery channel.
func validateSourceOnlyArtifactManifest(manifest artifactManifest) error {
	seenVersions := make(map[string]struct{}, len(manifest.Artifacts))
	for _, artifact := range manifest.Artifacts {
		if artifact.Product != "OpticonSource" || !validSourceArtifact(artifact) {
			return errors.New("source-only release manifest contains an invalid source archive")
		}
		if _, duplicate := seenVersions[artifact.Version]; duplicate {
			return errors.New("source-only release manifest contains duplicate source versions")
		}
		seenVersions[artifact.Version] = struct{}{}
	}
	return nil
}

func seedDynamicManifest(target, fallback string) error {
	if _, err := os.Stat(target); err == nil {
		return nil
	} else if !os.IsNotExist(err) {
		return err
	}
	data, err := os.ReadFile(fallback)
	if err != nil {
		return err
	}
	var manifest artifactManifest
	if err := json.Unmarshal(data, &manifest); err != nil {
		return err
	}
	if err := validateArtifactManifest(manifest); err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(target), 0700); err != nil {
		return err
	}
	return writeFileAtomically(target, data)
}

func writeFileAtomically(path string, data []byte) error {
	temporary := path + ".tmp"
	file, err := os.OpenFile(temporary, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	if _, err = file.Write(data); err == nil {
		err = file.Sync()
	}
	if closeErr := file.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		_ = os.Remove(temporary)
		return err
	}
	if err := os.Rename(temporary, path); err != nil {
		_ = os.Remove(temporary)
		return err
	}
	return nil
}

func (g *gateway) releaseManifestAdmin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPut {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	body, err := g.readAdminBody(w, r, maxAdminBody)
	if err != nil {
		if errors.Is(err, errAdminBusy) {
			http.Error(w, err.Error(), http.StatusTooManyRequests)
		} else {
			http.Error(w, "invalid manifest", http.StatusBadRequest)
		}
		return
	}
	if !g.authenticate(r, body, time.Now()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var next artifactManifest
	if err := json.Unmarshal(body, &next); err != nil || validateArtifactManifest(next) != nil {
		http.Error(w, "invalid manifest", http.StatusBadRequest)
		return
	}
	g.manifestMu.Lock()
	defer g.manifestMu.Unlock()
	current, err := g.readArtifactManifestUnlocked()
	if err != nil {
		// A trust-domain rotation can make the last otherwise well-formed
		// manifest unservable. Preserve its version/dependency downgrade guards,
		// but permit only this authenticated endpoint to replace it with a fully
		// validated manifest. Active invitations are still checked below.
		current, err = g.readStoredArtifactManifestUnlocked()
		if err != nil {
			http.Error(w, "release manifest unavailable", http.StatusServiceUnavailable)
			return
		}
	}
	currentVersion, currentOK := highestReleaseVersion(current)
	nextVersion, nextOK := highestReleaseVersion(next)
	if !nextOK {
		http.Error(w, "manifest has no published release", http.StatusBadRequest)
		return
	}
	if !completeCloudFrontRelease(next, nextVersion) {
		http.Error(w, "manifest release is incomplete", http.StatusBadRequest)
		return
	}
	if currentOK {
		comparison, valid := compareSemanticVersions(nextVersion, currentVersion)
		if !valid || comparison < 0 {
			http.Error(w, "release downgrade refused", http.StatusConflict)
			return
		}
		if comparison == 0 && !sameReleaseArtifacts(current, next, currentVersion) {
			http.Error(w, "release version is immutable", http.StatusConflict)
			return
		}
	}
	if !samePinnedDependencies(current, next) {
		http.Error(w, "pinned dependencies changed", http.StatusConflict)
		return
	}
	if err := g.requireActiveInviteArtifacts(next, time.Now()); err != nil {
		http.Error(w, "manifest would break an active invitation", http.StatusConflict)
		return
	}
	err = writeFileAtomically(g.artifactManifestPath(), body)
	if err != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"published": true, "version": nextVersion})
}

func sameReleaseArtifacts(left, right artifactManifest, version string) bool {
	encode := func(manifest artifactManifest) map[string]string {
		result := make(map[string]string)
		for _, artifact := range manifest.Artifacts {
			if artifact.Version != version || (artifact.Product != "OpticonBundle" && artifact.Product != "OpticonBootstrap" && artifact.Product != "OpticonSource") {
				continue
			}
			encoded, _ := json.Marshal(artifact)
			result[artifact.Product+"|"+artifact.Role+"|"+artifact.Architecture] = string(encoded)
		}
		return result
	}
	a, b := encode(left), encode(right)
	if len(a) != len(b) {
		return false
	}
	for key, value := range a {
		if b[key] != value {
			return false
		}
	}
	return true
}

func completeCloudFrontRelease(manifest artifactManifest, version string) bool {
	if manifest.SchemaVersion == sourceOnlyManifestSchema {
		sourceCount := 0
		for _, artifact := range manifest.Artifacts {
			if artifact.Version != version {
				continue
			}
			if artifact.Product != "OpticonSource" || !validSourceArtifact(artifact) || !validCloudFrontDownloadURL(artifact) {
				return false
			}
			sourceCount++
		}
		return sourceCount == 1
	}
	roles := make(map[string]bool)
	bootstrapCount := 0
	sourceCount := 0
	for _, artifact := range manifest.Artifacts {
		if artifact.Version != version {
			continue
		}
		switch artifact.Product {
		case "OpticonBundle":
			if !validBundleArtifact(artifact) || !validCloudFrontDownloadURL(artifact) || roles[artifact.Role] {
				return false
			}
			roles[artifact.Role] = true
		case "OpticonBootstrap":
			if !validBootstrapArtifact(artifact) {
				return false
			}
			bootstrapCount++
		case "OpticonSource":
			if !validSourceArtifact(artifact) {
				return false
			}
			sourceCount++
		}
	}
	return len(roles) == 2 && roles["ManagedOnly"] && roles["ControllerAndManaged"] && bootstrapCount == 1 && sourceCount == 1
}

func highestBundleVersion(manifest artifactManifest) (string, bool) {
	selected := ""
	for _, artifact := range manifest.Artifacts {
		if artifact.Product != "OpticonBundle" {
			continue
		}
		if selected == "" {
			selected = artifact.Version
			continue
		}
		if comparison, valid := compareSemanticVersions(artifact.Version, selected); valid && comparison > 0 {
			selected = artifact.Version
		}
	}
	return selected, selected != ""
}

// highestReleaseVersion selects the authoritative release item for the active
// manifest protocol. Schema 1 remains readable only for the one-way migration;
// schema 2 has no binary bundle, so the signed source archive is authoritative.
func highestReleaseVersion(manifest artifactManifest) (string, bool) {
	if manifest.SchemaVersion != sourceOnlyManifestSchema {
		return highestBundleVersion(manifest)
	}
	selected := ""
	for _, artifact := range manifest.Artifacts {
		if artifact.Product != "OpticonSource" {
			continue
		}
		if selected == "" {
			selected = artifact.Version
			continue
		}
		if comparison, valid := compareSemanticVersions(artifact.Version, selected); valid && comparison > 0 {
			selected = artifact.Version
		}
	}
	return selected, selected != ""
}

func samePinnedDependencies(left, right artifactManifest) bool {
	// Source-only releases carry their restore inputs in the signed archive and
	// do not use gateway/S3 dependency objects. Moving from schema 1 is safe
	// only in this direction; active legacy invitations are checked separately
	// before the manifest is committed.
	if right.SchemaVersion == sourceOnlyManifestSchema {
		return true
	}
	if left.SchemaVersion == sourceOnlyManifestSchema {
		return false
	}
	encode := func(manifest artifactManifest) map[string]string {
		result := make(map[string]string)
		for _, artifact := range manifest.Artifacts {
			if artifact.Product == "OpticonBundle" || artifact.Product == "OpticonBootstrap" || artifact.Product == "OpticonSource" {
				continue
			}
			encoded, _ := json.Marshal(artifact)
			result[artifact.Product+"|"+artifact.Architecture+"|"+artifact.File] = string(encoded)
		}
		return result
	}
	a, b := encode(left), encode(right)
	if len(a) != len(b) {
		return false
	}
	for key, value := range a {
		if b[key] != value {
			return false
		}
	}
	return true
}

func validBundleArtifact(artifact bundleArtifact) bool {
	if artifact.Product != "OpticonBundle" || !safeBundleFilePattern.MatchString(artifact.File) ||
		artifact.Size <= 0 || !inviteHashPattern.MatchString(strings.ToLower(artifact.SHA256)) ||
		(artifact.Role != "ManagedOnly" && artifact.Role != "ControllerAndManaged") ||
		(artifact.Architecture != "x64" && artifact.Architecture != "arm64") {
		return false
	}
	version, valid := parseSemanticVersion(artifact.Version)
	if !valid || version.core[0] == "0" || strings.ContainsAny(artifact.Version, "-+") {
		return false
	}
	return artifact.LegacyMigrationSignerThumbprint == "" || validLegacyMachineStateMigrationBridgeArtifact(artifact)
}

// The gateway preserves the marker for the Admin selection logic, but it does
// not make the marker itself a signing authority. In addition to this exact
// outer trust record, the target Agent independently verifies the legacy-signed
// inner manifest and payload before it can stage the bridge.
func validLegacyMachineStateMigrationBridgeArtifact(artifact bundleArtifact) bool {
	return artifact.LegacyMigrationSignerThumbprint == invitationSigningKeyID &&
		artifact.Version == legacyMachineStateMigrationBridgeVersion &&
		artifact.SigningProfile == "OwnerManaged" &&
		validProductionArtifactTrust(artifact)
}

func validCloudFrontDownloadURL(artifact bundleArtifact) bool {
	if artifact.DownloadURL == "" {
		return false
	}
	u, err := url.Parse(artifact.DownloadURL)
	if err != nil || u.Scheme != "https" || u.User != nil || u.Fragment != "" || u.RawQuery != "" || u.Port() != "" {
		return false
	}
	host := strings.ToLower(u.Hostname())
	if !regexp.MustCompile(`^[a-z0-9-]+\.cloudfront\.net$`).MatchString(host) {
		return false
	}
	return u.EscapedPath() == "/opticon/releases/"+url.PathEscape(artifact.Version)+"/"+url.PathEscape(artifact.File)
}

func validBootstrapArtifact(artifact bundleArtifact) bool {
	if artifact.Product != "OpticonBootstrap" || artifact.Architecture != "x64" || artifact.Size <= 0 || artifact.Size > maxBootstrapArtifactBytes ||
		!safeBootstrapFilePattern.MatchString(artifact.File) || !inviteHashPattern.MatchString(strings.ToLower(artifact.SHA256)) ||
		!publisherThumbprintPattern.MatchString(artifact.SignerThumbprint) || !validProductionArtifactTrust(artifact) ||
		artifact.SignerThumbprint != artifact.ProductSigner {
		return false
	}
	version, valid := parseSemanticVersion(artifact.Version)
	if !valid || version.core[0] == "0" || strings.ContainsAny(artifact.Version, "-+") {
		return false
	}
	u, err := url.Parse(artifact.DownloadURL)
	if err != nil || u.Scheme != "https" || u.User != nil || u.Fragment != "" || u.RawQuery != "" || u.Port() != "" {
		return false
	}
	if !regexp.MustCompile(`^[a-z0-9-]+\.cloudfront\.net$`).MatchString(strings.ToLower(u.Hostname())) {
		return false
	}
	return u.EscapedPath() == "/opticon/releases/"+url.PathEscape(artifact.Version)+"/"+url.PathEscape(artifact.File)
}

func validSourceArtifact(artifact bundleArtifact) bool {
	if artifact.Product != "OpticonSource" || artifact.Architecture != "source" || artifact.Size <= 0 ||
		!safeSourceFilePattern.MatchString(artifact.File) || !inviteHashPattern.MatchString(strings.ToLower(artifact.SHA256)) ||
		artifact.SDKVersion != pinnedSDKVersion || artifact.RuntimeVersion != pinnedRuntimeVersion || !supportedTargetRuntimes(artifact.TargetRuntimes) ||
		!inviteHashPattern.MatchString(strings.ToLower(artifact.SourceManifestSHA256)) || !validProductionArtifactTrust(artifact) {
		return false
	}
	version, valid := parseSemanticVersion(artifact.Version)
	if !valid || version.core[0] == "0" || strings.ContainsAny(artifact.Version, "-+") || !validCloudFrontDownloadURL(artifact) {
		return false
	}
	comparison, _ := compareSemanticVersions(artifact.Version, "1.2.1")
	return comparison < 0 || validSourceLauncherMetadata(artifact)
}

func validSourceLauncherMetadata(artifact bundleArtifact) bool {
	return artifact.SourceLauncherFile == "opticon-source-launcher-"+artifact.Version+".exe" &&
		artifact.SourceLauncherSize > 0 && artifact.SourceLauncherSize <= maxBootstrapArtifactBytes &&
		inviteHashPattern.MatchString(strings.ToLower(artifact.SourceLauncherSHA256))
}

func supportedTargetRuntimes(values []string) bool {
	return len(values) == 2 && values[0] == "win-x64" && values[1] == "win-arm64"
}

func (g *gateway) bundleIsAvailable(artifact bundleArtifact) bool {
	return validBundleArtifact(artifact) && (validCloudFrontDownloadURL(artifact) || g.bundleIsFinalized(artifact))
}

func (g *gateway) bundleIsFinalized(artifact bundleArtifact) bool {
	if !validBundleArtifact(artifact) {
		return false
	}
	info, err := os.Lstat(filepath.Join(g.bundleDir, artifact.File))
	return err == nil && info.Mode().IsRegular() && info.Size() == artifact.Size
}

func (g *gateway) invitationAdmin(w http.ResponseWriter, r *http.Request) {
	idHash := strings.ToLower(strings.TrimPrefix(r.URL.Path, inviteAdminPrefix))
	if !inviteHashPattern.MatchString(idHash) {
		http.NotFound(w, r)
		return
	}
	body, err := g.readAdminBody(w, r, maxInviteBody)
	if err != nil {
		if errors.Is(err, errAdminBusy) {
			http.Error(w, err.Error(), http.StatusTooManyRequests)
		} else {
			http.Error(w, "invalid body", http.StatusBadRequest)
		}
		return
	}
	if !g.authenticate(r, body, time.Now()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	path := filepath.Join(g.inviteDir, idHash+".json")
	switch r.Method {
	case http.MethodPut:
		var invite hostedInvite
		if err := json.Unmarshal(body, &invite); err != nil {
			http.Error(w, "invalid invitation", http.StatusBadRequest)
			return
		}
		now := time.Now()
		sourceOnly := invite.InstallProtocol == sourceInstallProtocol
		legacyBootstrap := invite.InstallProtocol == ""
		if strings.TrimSpace(invite.DeviceName) == "" || (invite.Role != "ManagedOnly" && invite.Role != "ControllerAndManaged") ||
			!invite.ExpiresAt.After(now) || invite.ExpiresAt.After(now.Add(366*24*time.Hour)) || len(invite.Ciphertext) < 64 || len(invite.Ciphertext) > maxInviteBody ||
			!inviteHashPattern.MatchString(strings.ToLower(invite.SourceSHA256)) || !inviteHashPattern.MatchString(strings.ToLower(invite.SourceManifestSHA256)) ||
			invite.SourceSize <= 0 || invite.SDKVersion != pinnedSDKVersion || invite.RuntimeVersion != pinnedRuntimeVersion || !supportedTargetRuntimes(invite.TargetRuntimes) ||
			invite.SigningProfile != trustedSigningProfile || invite.SourceManifestKeyID != trustedSourceManifestKeyID ||
			invite.ProductSigner != trustedProductSignerThumbprint || (!sourceOnly && !legacyBootstrap) {
			http.Error(w, "invalid invitation", http.StatusBadRequest)
			return
		}
		if sourceOnly {
			if invite.BootstrapVersion != "" || invite.BootstrapFile != "" || invite.BootstrapSize != 0 ||
				invite.BootstrapSHA256 != "" || invite.BootstrapSigner != "" {
				http.Error(w, "source-only invitation carries a release bootstrap", http.StatusBadRequest)
				return
			}
		} else if invite.BootstrapSigner != invite.ProductSigner || invite.BootstrapVersion != invite.ReleaseVersion ||
			invite.BootstrapFile != "opticon-bootstrap-"+invite.ReleaseVersion+".exe" || invite.BootstrapSize <= 0 || invite.BootstrapSize > maxBootstrapArtifactBytes ||
			!inviteHashPattern.MatchString(strings.ToLower(invite.BootstrapSHA256)) || !publisherThumbprintPattern.MatchString(invite.BootstrapSigner) {
			http.Error(w, "invalid invitation", http.StatusBadRequest)
			return
		}
		if _, err := g.sourceForInvite(invite); err != nil {
			http.Error(w, "invitation source release is unavailable", http.StatusConflict)
			return
		}
		if !sourceOnly {
			if _, err := g.bootstrapForInvite(invite); err != nil {
				http.Error(w, "invitation bootstrap release is unavailable", http.StatusConflict)
				return
			}
		}
		encoded, err := json.Marshal(invite)
		if err != nil {
			http.Error(w, "invalid invitation", http.StatusBadRequest)
			return
		}
		g.inviteMu.Lock()
		defer g.inviteMu.Unlock()
		temporary := path + ".tmp"
		if err := os.WriteFile(temporary, encoded, 0600); err != nil {
			http.Error(w, "storage unavailable", http.StatusInternalServerError)
			return
		}
		if err := os.Rename(temporary, path); err != nil {
			_ = os.Remove(temporary)
			http.Error(w, "storage unavailable", http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusCreated, map[string]any{"stored": true, "expiresAt": invite.ExpiresAt})
	case http.MethodDelete:
		g.inviteMu.Lock()
		err := os.Remove(path)
		g.inviteMu.Unlock()
		if err != nil && !os.IsNotExist(err) {
			http.Error(w, "storage unavailable", http.StatusInternalServerError)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func (g *gateway) bundleAdmin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPut && r.Method != http.MethodDelete {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	name := strings.TrimPrefix(r.URL.Path, bundleAdminPrefix)
	if filepath.Base(name) != name || !safeBundleFilePattern.MatchString(name) {
		http.NotFound(w, r)
		return
	}
	body, err := g.readAdminBody(w, r, maxBundleChunk)
	if err != nil {
		if errors.Is(err, errAdminBusy) {
			http.Error(w, err.Error(), http.StatusTooManyRequests)
		} else {
			http.Error(w, "invalid chunk", http.StatusBadRequest)
		}
		return
	}
	if !g.authenticate(r, body, time.Now()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
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
		if uploadError := removeIfExists(filepath.Join(g.bundleDir, name+".upload")); deletionError == nil {
			deletionError = uploadError
		}
		g.bundleMu.Unlock()
		if deletionError != nil {
			http.Error(w, "storage unavailable", http.StatusInternalServerError)
			return
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}
	expected, err := g.bundleByFile(name)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	offset, offsetErr := strconv.ParseInt(r.URL.Query().Get("offset"), 10, 64)
	total, totalErr := strconv.ParseInt(r.URL.Query().Get("total"), 10, 64)
	claimedHash := strings.ToLower(r.URL.Query().Get("sha256"))
	if offsetErr != nil || totalErr != nil || offset < 0 || total != expected.Size || claimedHash != strings.ToLower(expected.SHA256) || len(body) == 0 || offset+int64(len(body)) > total || (offset+int64(len(body)) < total && len(body) != maxBundleChunk) {
		http.Error(w, "invalid chunk metadata", http.StatusBadRequest)
		return
	}
	g.bundleMu.Lock()
	defer g.bundleMu.Unlock()
	temporary := filepath.Join(g.bundleDir, name+".upload")
	flags := os.O_CREATE | os.O_WRONLY
	if offset == 0 {
		flags |= os.O_TRUNC
	}
	file, err := os.OpenFile(temporary, flags, 0600)
	if err != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	info, statErr := file.Stat()
	if statErr != nil || info.Size() != offset {
		_ = file.Close()
		http.Error(w, "unexpected chunk offset", http.StatusConflict)
		return
	}
	if _, err = file.WriteAt(body, offset); err == nil {
		err = file.Sync()
	}
	if closeErr := file.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	if offset+int64(len(body)) < total {
		writeJSON(w, http.StatusAccepted, map[string]any{"nextOffset": offset + int64(len(body))})
		return
	}
	file, err = os.Open(temporary)
	if err != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	hasher := sha256.New()
	_, hashErr := io.Copy(hasher, file)
	closeErr := file.Close()
	actualHash := hex.EncodeToString(hasher.Sum(nil))
	if hashErr != nil || closeErr != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	if actualHash != claimedHash {
		http.Error(w, "bundle hash verification failed", http.StatusConflict)
		return
	}
	finalPath := filepath.Join(g.bundleDir, name)
	if err := os.Rename(temporary, finalPath); err != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	if err := os.Chmod(finalPath, 0444); err != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"stored": true, "sha256": actualHash})
}

func removeIfExists(path string) error {
	err := os.Remove(path)
	if os.IsNotExist(err) {
		return nil
	}
	return err
}
func (g *gateway) publicInvitation(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	suffix := strings.Trim(strings.TrimPrefix(r.URL.Path, invitePublicPrefix), "/")
	parts := strings.Split(suffix, "/")
	if len(parts) < 1 || len(parts) > 2 || !inviteTokenPattern.MatchString(parts[0]) {
		http.NotFound(w, r)
		return
	}
	invite, path, err := g.readHostedInvite(parts[0])
	if err != nil {
		http.NotFound(w, r)
		return
	}
	now := time.Now()
	if g.now != nil {
		now = g.now()
	}
	if !invite.ExpiresAt.After(now) {
		_ = os.Remove(path)
		http.Error(w, "This invitation has expired. Ask for a new Opticon link.", http.StatusGone)
		return
	}
	w.Header().Set("Cache-Control", "no-store")
	if len(parts) == 2 {
		if parts[1] == "source" {
			g.redirectInvitationSource(w, r, invite, now)
			return
		}
		if parts[1] == "launcher" {
			g.serveInvitationSourceLauncher(w, r, invite)
			return
		}
		if parts[1] != "invite.tdinvite" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Disposition", `attachment; filename="invite.tdinvite"`)
		w.Header().Set("Content-Length", strconv.Itoa(len(invite.Ciphertext)))
		if r.Method == http.MethodGet {
			_, _ = w.Write(invite.Ciphertext)
		}
		return
	}
	if invite.ReleaseVersion == "" {
		http.Error(w, "This legacy invitation cannot install software. Ask the command center for a new source-build invitation.", http.StatusGone)
		return
	}
	source, err := g.sourceForInvite(invite)
	if err != nil {
		http.Error(w, "This invitation's exact Opticon source release is unavailable.", http.StatusServiceUnavailable)
		return
	}
	if invite.InstallProtocol == sourceInstallProtocol {
		g.sourceOnlyInvitationLandingSecure(w, r, parts[0], invite, source)
		return
	}
	g.sourceInvitationLandingSecure(w, r, parts[0], invite, source)
}

func (g *gateway) redirectInvitationSource(w http.ResponseWriter, r *http.Request, invite hostedInvite, now time.Time) {
	if invite.InstallProtocol != sourceInstallProtocol || g.sourceSigner == nil {
		http.Error(w, "This invitation cannot issue a source download.", http.StatusServiceUnavailable)
		return
	}
	source, err := g.sourceForInvite(invite)
	if err != nil {
		http.Error(w, "This invitation's exact Opticon source release is unavailable.", http.StatusServiceUnavailable)
		return
	}
	location, err := g.sourceSigner.Presign(source, now)
	if err != nil {
		http.Error(w, "The private source download could not be authorized.", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Disposition", fmt.Sprintf("attachment; filename=%q", source.File))
	http.Redirect(w, r, location, http.StatusTemporaryRedirect)
}

func (g *gateway) serveInvitationSourceLauncher(w http.ResponseWriter, r *http.Request, invite hostedInvite) {
	if invite.InstallProtocol != sourceInstallProtocol || (r.Method != http.MethodGet && r.Method != http.MethodHead) {
		http.NotFound(w, r)
		return
	}
	source, err := g.sourceForInvite(invite)
	if err != nil || !validSourceLauncherMetadata(source) {
		http.Error(w, "This invitation's signed Opticon launcher is unavailable.", http.StatusServiceUnavailable)
		return
	}
	file, err := os.Open(filepath.Join(g.artifactDir, source.SourceLauncherFile))
	if err != nil {
		http.Error(w, "This invitation's signed Opticon launcher is unavailable.", http.StatusServiceUnavailable)
		return
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() || info.Size() != source.SourceLauncherSize {
		http.Error(w, "This invitation's signed Opticon launcher is unavailable.", http.StatusServiceUnavailable)
		return
	}
	hasher := sha256.New()
	if _, err := io.Copy(hasher, file); err != nil || !hmac.Equal(
		[]byte(hex.EncodeToString(hasher.Sum(nil))), []byte(strings.ToLower(source.SourceLauncherSHA256))) {
		http.Error(w, "This invitation's signed Opticon launcher failed integrity validation.", http.StatusServiceUnavailable)
		return
	}
	if _, err := file.Seek(0, io.SeekStart); err != nil {
		http.Error(w, "This invitation's signed Opticon launcher is unavailable.", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Type", "application/vnd.microsoft.portable-executable")
	w.Header().Set("Content-Length", strconv.FormatInt(info.Size(), 10))
	http.ServeContent(w, r, source.SourceLauncherFile, info.ModTime(), file)
}

func (g *gateway) readHostedInvite(publicID string) (hostedInvite, string, error) {
	hash := sha256.Sum256([]byte(publicID))
	path := filepath.Join(g.inviteDir, hex.EncodeToString(hash[:])+".json")
	g.inviteMu.Lock()
	data, err := os.ReadFile(path)
	g.inviteMu.Unlock()
	if err != nil {
		return hostedInvite{}, path, err
	}
	var invite hostedInvite
	if err := json.Unmarshal(data, &invite); err != nil {
		return hostedInvite{}, path, err
	}
	return invite, path, nil
}

func (g *gateway) bundleForRole(role string) (bundleArtifact, error) {
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return bundleArtifact{}, err
	}
	var selected bundleArtifact
	found := false
	for _, artifact := range manifest.Artifacts {
		if validBundleArtifact(artifact) && artifact.LegacyMigrationSignerThumbprint == "" &&
			artifact.Role == role && artifact.Architecture == "x64" && g.bundleIsAvailable(artifact) {
			if !found {
				selected = artifact
				found = true
				continue
			}
			comparison, _ := compareSemanticVersions(artifact.Version, selected.Version)
			if comparison > 0 {
				selected = artifact
			} else if comparison == 0 {
				return bundleArtifact{}, errors.New("role bundle manifest contains ambiguous precedence-equivalent releases")
			}
		}
	}
	if found {
		return selected, nil
	}
	return bundleArtifact{}, errors.New("role bundle is not published")
}

func (g *gateway) sourceForInvite(invite hostedInvite) (bundleArtifact, error) {
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return bundleArtifact{}, err
	}
	return sourceForInviteInManifest(invite, manifest)
}

func sourceForInviteInManifest(invite hostedInvite, manifest artifactManifest) (bundleArtifact, error) {
	if _, valid := parseSemanticVersion(invite.ReleaseVersion); !valid || strings.ContainsAny(invite.ReleaseVersion, "-+") || !inviteHashPattern.MatchString(strings.ToLower(invite.SourceSHA256)) {
		return bundleArtifact{}, errors.New("invitation source metadata is invalid")
	}
	for _, artifact := range manifest.Artifacts {
		if validSourceArtifact(artifact) && artifact.Version == invite.ReleaseVersion && artifact.File == invite.SourceFile && artifact.Size == invite.SourceSize &&
			artifact.SDKVersion == invite.SDKVersion && artifact.RuntimeVersion == invite.RuntimeVersion && equalStrings(artifact.TargetRuntimes, invite.TargetRuntimes) && artifact.SourceManifestKeyID == invite.SourceManifestKeyID &&
			artifact.SigningProfile == invite.SigningProfile && artifact.ProductSigner == invite.ProductSigner &&
			hmac.Equal([]byte(strings.ToLower(artifact.SHA256)), []byte(strings.ToLower(invite.SourceSHA256))) &&
			hmac.Equal([]byte(strings.ToLower(artifact.SourceManifestSHA256)), []byte(strings.ToLower(invite.SourceManifestSHA256))) {
			return artifact, nil
		}
	}
	return bundleArtifact{}, errors.New("invitation source release is not published")
}

func (g *gateway) requireActiveInviteArtifacts(next artifactManifest, now time.Time) error {
	if g.inviteDir == "" {
		return nil
	}
	g.inviteMu.Lock()
	defer g.inviteMu.Unlock()
	entries, err := os.ReadDir(g.inviteDir)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".json") {
			continue
		}
		data, readErr := os.ReadFile(filepath.Join(g.inviteDir, entry.Name()))
		if readErr != nil || len(data) > maxInviteBody*2 {
			return errors.New("an invitation record could not be read safely")
		}
		var invite hostedInvite
		if json.Unmarshal(data, &invite) != nil {
			return errors.New("an invitation record is corrupt")
		}
		if !invite.ExpiresAt.After(now) || invite.ReleaseVersion == "" {
			continue
		}
		if invite.InstallProtocol != "" && invite.InstallProtocol != sourceInstallProtocol {
			return errors.New("an invitation record has an unsupported install protocol")
		}
		if _, err := sourceForInviteInManifest(invite, next); err != nil {
			return err
		}
		if invite.InstallProtocol != sourceInstallProtocol {
			if _, err := bootstrapForInviteInManifest(invite, next); err != nil {
				return err
			}
		}
	}
	return nil
}

func (g *gateway) bootstrapForInvite(invite hostedInvite) (bundleArtifact, error) {
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return bundleArtifact{}, err
	}
	return bootstrapForInviteInManifest(invite, manifest)
}

func bootstrapForInviteInManifest(invite hostedInvite, manifest artifactManifest) (bundleArtifact, error) {
	for _, artifact := range manifest.Artifacts {
		if validBootstrapArtifact(artifact) && artifact.Version == invite.BootstrapVersion && artifact.File == invite.BootstrapFile &&
			artifact.Size == invite.BootstrapSize && strings.EqualFold(artifact.SignerThumbprint, invite.BootstrapSigner) &&
			artifact.SigningProfile == invite.SigningProfile && artifact.SourceManifestKeyID == invite.SourceManifestKeyID && artifact.ProductSigner == invite.ProductSigner &&
			hmac.Equal([]byte(strings.ToLower(artifact.SHA256)), []byte(strings.ToLower(invite.BootstrapSHA256))) {
			return artifact, nil
		}
	}
	return bundleArtifact{}, errors.New("invitation bootstrap release is not published")
}

func equalStrings(left, right []string) bool {
	if len(left) != len(right) {
		return false
	}
	for index := range left {
		if left[index] != right[index] {
			return false
		}
	}
	return true
}

func (g *gateway) bundleByFile(name string) (bundleArtifact, error) {
	if !safeBundleFilePattern.MatchString(name) {
		return bundleArtifact{}, errors.New("bundle filename is invalid")
	}
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return bundleArtifact{}, err
	}
	for _, artifact := range manifest.Artifacts {
		if validBundleArtifact(artifact) && artifact.File == name {
			return artifact, nil
		}
	}
	return bundleArtifact{}, errors.New("bundle is not declared")
}

func (g *gateway) artifactByFile(name string) (bundleArtifact, error) {
	if filepath.Base(name) != name || strings.ContainsAny(name, `/\\`) {
		return bundleArtifact{}, errors.New("artifact filename is invalid")
	}
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return bundleArtifact{}, err
	}
	for _, artifact := range manifest.Artifacts {
		if artifact.File == name && artifact.Size > 0 {
			return artifact, nil
		}
	}
	return bundleArtifact{}, errors.New("artifact is not declared")
}

func (g *gateway) pruneUndeclaredBundles() error {
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return err
	}
	declared := make(map[string]struct{})
	for _, artifact := range manifest.Artifacts {
		if artifact.Product == "OpticonBundle" && filepath.Base(artifact.File) == artifact.File {
			declared[artifact.File] = struct{}{}
		}
	}
	entries, err := os.ReadDir(g.bundleDir)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		name := entry.Name()
		if !strings.HasPrefix(name, "opticon-bundle-") {
			continue
		}
		base := strings.TrimSuffix(name, ".upload")
		if !strings.HasSuffix(base, ".zip") {
			continue
		}
		if _, current := declared[base]; current {
			continue
		}
		if err := os.Remove(filepath.Join(g.bundleDir, name)); err != nil && !os.IsNotExist(err) {
			return err
		}
	}
	return nil
}

// sourceOnlyInvitationLandingSecure binds the URL fragment into the local
// download filename without sending it to the gateway. The browser downloads
// only the signed launcher. It decrypts the invitation and fetches the source.
func (g *gateway) sourceOnlyInvitationLandingSecure(w http.ResponseWriter, r *http.Request, publicID string, invite hostedInvite, source bundleArtifact) {
	if !validSourceLauncherMetadata(source) {
		http.Error(w, "This invitation's one-click signed launcher is unavailable.", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'")
	if r.Method == http.MethodHead {
		return
	}
	launcherPath := invitePublicPrefix + url.PathEscape(publicID) + "/launcher"
	page := fmt.Sprintf(`<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Opticon invitation</title><style>body{font:18px Segoe UI,sans-serif;background:#111316;color:#edf1f5;max-width:720px;margin:10vh auto;padding:28px}.download{display:inline-block;background:#52d39a;color:#08130e;text-decoration:none;padding:14px 20px;font-weight:700;font-size:17px;border-radius:6px}.disabled{pointer-events:none;opacity:.45}.muted{color:#9da7b1}code{color:#52d39a;overflow-wrap:anywhere;user-select:all}</style></head><body><h1>Install Opticon</h1><p>This private invitation is for <strong>%s</strong>.</p><p>Opticon <code>%s</code> is ready to build and install.</p><p><a id="install" class="download" href="%s">Download signed installer</a></p><p id="status" class="muted">Download the installer, then double-click it. Windows will ask for administrator approval. No ZIP extraction or invitation paste is needed.</p><p class="muted">The signed installer downloads a private source link valid for 30 minutes, then verifies the invitation, source SHA-256, signed manifest, and exact .NET SDK before building.</p><p class="muted">Source SHA-256: <code>%s</code><br>Requires exact .NET SDK <code>%s</code>. Invitation expires <code>%s</code>.</p><script>const key=location.hash.slice(1),install=document.getElementById('install'),status=document.getElementById('status');if(!/^[A-Za-z0-9_-]{43}$/.test(key)){install.removeAttribute('href');install.classList.add('disabled');status.textContent='This invitation link is incomplete. Ask the command center for a new link.'}else{install.download='Install-Opticon-%s--'+key+'--%s.exe'}</script></body></html>`, html.EscapeString(invite.DeviceName), html.EscapeString(source.Version), html.EscapeString(launcherPath), html.EscapeString(strings.ToLower(source.SHA256)), html.EscapeString(source.SDKVersion), invite.ExpiresAt.Local().Format(time.RFC1123), publicID, strings.ToLower(source.SourceLauncherSHA256))
	_, _ = io.WriteString(w, page)
}

func (g *gateway) sourceInvitationLandingSecure(w http.ResponseWriter, r *http.Request, publicID string, invite hostedInvite, source bundleArtifact) {
	bootstrap, err := g.bootstrapForInvite(invite)
	if err != nil {
		http.Error(w, "This invitation's signed source bootstrap is unavailable.", http.StatusServiceUnavailable)
		return
	}
	origins := map[string]bool{}
	for _, raw := range []string{source.DownloadURL, bootstrap.DownloadURL} {
		if parsed, parseErr := url.Parse(raw); parseErr == nil && parsed.IsAbs() {
			origins[parsed.Scheme+"://"+parsed.Host] = true
		}
	}
	connect := make([]string, 0, len(origins))
	for origin := range origins {
		connect = append(connect, origin)
	}
	if len(connect) == 0 {
		connect = append(connect, "'self'")
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; connect-src "+strings.Join(connect, " ")+"; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'")
	if r.Method == http.MethodHead {
		return
	}
	page := fmt.Sprintf(`<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Opticon invitation</title><style>body{font:18px Segoe UI,sans-serif;background:#111316;color:#edf1f5;max-width:720px;margin:10vh auto;padding:28px}button{background:#52d39a;color:#08130e;border:0;padding:14px 20px;font-weight:700;font-size:17px;border-radius:6px;cursor:pointer}button:disabled{cursor:wait;opacity:.65}.muted{color:#9da7b1}code{color:#52d39a;overflow-wrap:anywhere;user-select:all}</style></head><body><h1>Build and install Opticon</h1><p>This private invitation is for <strong>%s</strong>.</p><p id="status">Preparing authenticated Opticon source <code>%s</code>.</p><button id="download">Download source and signed bootstrap</button><p id="diagnostic" class="muted">Allow two downloads when your browser asks. Keep both files in the same folder.</p><p class="muted">Source SHA-256: <code>%s</code><br>Bootstrap SHA-256: <code>%s</code></p><p class="muted">Requires exact .NET SDK <code>%s</code>. Expires <code>%s</code>.</p><script>const key=location.hash.slice(1),status=document.getElementById('status'),diagnostic=document.getElementById('diagnostic'),button=document.getElementById('download');let active=false;function save(blob,name){const u=URL.createObjectURL(blob),a=document.createElement('a');a.href=u;a.download=name;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(u),30000)}async function verified(url,size,hash,label){if(!globalThis.crypto||!crypto.subtle)throw new Error('Use current Microsoft Edge or Chrome; WebCrypto SHA-256 is unavailable.');const r=await fetch(url,{credentials:'omit'});if(!r.ok)throw new Error(label+' returned HTTP '+r.status+'.');const b=await r.blob();if(b.size!==size)throw new Error(label+' size is invalid.');const d=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await b.arrayBuffer()))).map(v=>v.toString(16).padStart(2,'0')).join('');if(d!==hash)throw new Error(label+' SHA-256 is invalid.');return b}async function download(){if(active)return;if(!/^[A-Za-z0-9_-]{32,128}$/.test(key)){status.textContent='This invitation link is incomplete. Ask for a new link.';button.disabled=true;return}active=true;button.disabled=true;status.textContent='Downloading and hashing source...';try{const s=await verified(%q,%d,%q,'Source archive');save(s,%q);status.textContent='Source verified. Downloading and hashing the signed bootstrap...';const b=await verified(%q,%d,%q,'Signed bootstrap');save(b,'Install-Opticon-%s--'+key+'--%s.exe');status.textContent='Both authenticated files are downloaded. Keep them together, then open the Install-Opticon executable.';diagnostic.textContent='Windows will request elevation. The signed bootstrap rechecks itself, the encrypted signed invitation, exact source archive, and RSA-PSS inner manifest before building.'}catch(e){status.textContent='The authenticated installer could not be downloaded.';diagnostic.textContent=String(e&&e.message||e).slice(0,240)+' Retry in current Microsoft Edge or Chrome; no unsigned fallback is offered.';button.disabled=false}finally{active=false}}button.addEventListener('click',download)</script></body></html>`, html.EscapeString(invite.DeviceName), html.EscapeString(source.Version), html.EscapeString(strings.ToLower(source.SHA256)), html.EscapeString(strings.ToLower(bootstrap.SHA256)), source.SDKVersion, invite.ExpiresAt.Local().Format(time.RFC1123), source.DownloadURL, source.Size, strings.ToLower(source.SHA256), source.File, bootstrap.DownloadURL, bootstrap.Size, strings.ToLower(bootstrap.SHA256), publicID, strings.ToLower(bootstrap.SHA256))
	_, _ = io.WriteString(w, page)
}

func (g *gateway) bootstrapForVersion(version string) (bundleArtifact, error) {
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return bundleArtifact{}, err
	}
	for _, artifact := range manifest.Artifacts {
		if artifact.Product == "OpticonBootstrap" && artifact.Version == version && validBootstrapArtifact(artifact) {
			return artifact, nil
		}
	}
	return bundleArtifact{}, errors.New("release bootstrap is not published")
}

func (g *gateway) admin(w http.ResponseWriter, r *http.Request) {
	if !isAllowedAdminRoute(r.Method, strings.TrimPrefix(r.URL.Path, adminPrefix)) {
		http.NotFound(w, r)
		return
	}
	body, err := g.readAdminBody(w, r, maxAdminBody)
	if err != nil {
		if errors.Is(err, errAdminBusy) {
			http.Error(w, err.Error(), http.StatusTooManyRequests)
		} else {
			http.Error(w, "invalid body", http.StatusBadRequest)
		}
		return
	}
	if !g.authenticate(r, body, time.Now()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	r.URL.Path = "/" + strings.TrimPrefix(r.URL.Path, adminPrefix)
	r.URL.RawPath = ""
	r.Body = io.NopCloser(bytes.NewReader(body))
	r.ContentLength = int64(len(body))
	r.Header.Set("Authorization", "Bearer "+g.headscaleKey)
	for _, name := range []string{"X-Opticon-Key-Id", "X-Opticon-Timestamp", "X-Opticon-Nonce", "X-Opticon-Content-SHA256", "X-Opticon-Signature"} {
		r.Header.Del(name)
	}
	g.proxy.ServeHTTP(w, r)
}

func isAllowedAdminRoute(method, path string) bool {
	if method == http.MethodGet && path == "api/v1/node" {
		return true
	}
	if method == http.MethodPost && (path == "api/v1/preauthkey" || path == "api/v1/preauthkey/expire") {
		return true
	}
	parts := strings.Split(path, "/")
	if len(parts) == 5 && parts[0] == "api" && parts[1] == "v1" && parts[2] == "node" && parts[3] != "" {
		return method == http.MethodPost && (parts[4] == "tags" || parts[4] == "approve_routes")
	}
	return len(parts) == 4 && parts[0] == "api" && parts[1] == "v1" && parts[2] == "node" && parts[3] != "" && method == http.MethodDelete
}

func (g *gateway) authenticate(r *http.Request, body []byte, now time.Time) bool {
	if r.Header.Get("X-Opticon-Key-Id") != "primary" {
		return false
	}
	timestampText, nonce := r.Header.Get("X-Opticon-Timestamp"), r.Header.Get("X-Opticon-Nonce")
	timestamp, err := strconv.ParseInt(timestampText, 10, 64)
	if err != nil || len(nonce) < 20 || abs(now.Unix()-timestamp) > 300 {
		return false
	}
	hash := sha256.Sum256(body)
	hashText := hex.EncodeToString(hash[:])
	if !hmac.Equal([]byte(hashText), []byte(strings.ToLower(r.Header.Get("X-Opticon-Content-SHA256")))) {
		return false
	}
	canonical := strings.Join([]string{r.Method, r.URL.RequestURI(), timestampText, nonce, hashText}, "\n")
	expected := hmac.New(sha256.New, g.adminSecret)
	_, _ = expected.Write([]byte(canonical))
	provided, err := hex.DecodeString(r.Header.Get("X-Opticon-Signature"))
	if err != nil || !hmac.Equal(provided, expected.Sum(nil)) {
		return false
	}
	if g.nonceDir != "" {
		return g.consumePersistentNonce(nonce, now)
	}
	g.nonceMu.Lock()
	defer g.nonceMu.Unlock()
	for key, expiry := range g.nonces {
		if now.After(expiry) {
			delete(g.nonces, key)
		}
	}
	if _, exists := g.nonces[nonce]; exists {
		return false
	}
	g.nonces[nonce] = now.Add(10 * time.Minute)
	return true
}

func (g *gateway) consumePersistentNonce(nonce string, now time.Time) bool {
	g.nonceMu.Lock()
	defer g.nonceMu.Unlock()
	hash := sha256.Sum256([]byte(nonce))
	path := filepath.Join(g.nonceDir, hex.EncodeToString(hash[:])+".nonce")
	file, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return false
	}
	_, writeErr := io.WriteString(file, strconv.FormatInt(now.Add(10*time.Minute).Unix(), 10))
	if writeErr == nil {
		writeErr = file.Sync()
	}
	if closeErr := file.Close(); writeErr == nil {
		writeErr = closeErr
	}
	if writeErr != nil {
		_ = os.Remove(path)
		return false
	}
	if err := syncDirectory(g.nonceDir); err != nil {
		return false
	}
	// Pruning is deliberately after the O_EXCL commit. Another gateway may be
	// between creating and writing a nonce, so recent or target files are never
	// parsed or removed. Old residue is only a bounded availability concern.
	entries, readErr := os.ReadDir(g.nonceDir)
	if readErr == nil {
		pruneBefore := now.Add(-20 * time.Minute)
		for _, entry := range entries {
			if entry.IsDir() || entry.Name() == filepath.Base(path) || !strings.HasSuffix(entry.Name(), ".nonce") {
				continue
			}
			info, infoErr := entry.Info()
			if infoErr == nil && info.ModTime().Before(pruneBefore) {
				_ = os.Remove(filepath.Join(g.nonceDir, entry.Name()))
			}
		}
	}
	return true
}

func abs(value int64) int64 {
	if value < 0 {
		return -value
	}
	return value
}

func migrateBundleUploads(stagingDir, artifactDir, bundleDir string) error {
	g := &gateway{artifactDir: artifactDir, bundleDir: bundleDir}
	if _, err := g.readArtifactManifest(); err != nil {
		// Legacy uploads are optional migration residue. An obsolete release
		// manifest must make artifacts unavailable, not crash-loop the control
		// plane before an authenticated replacement can be published.
		log.Printf("legacy bundle migration skipped: %v", err)
		return nil
	}
	entries, err := os.ReadDir(stagingDir)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasPrefix(entry.Name(), "opticon-bundle-") || !strings.HasSuffix(entry.Name(), ".zip.upload") {
			continue
		}
		finalName := strings.TrimSuffix(entry.Name(), ".upload")
		source := filepath.Join(stagingDir, entry.Name())
		expected, declarationErr := g.bundleByFile(finalName)
		if declarationErr != nil {
			if err := removeIfExists(source); err != nil {
				return err
			}
			continue
		}
		matches, verifyErr := bundleFileMatches(source, expected)
		if verifyErr != nil {
			return verifyErr
		}
		if !matches {
			if err := removeIfExists(source); err != nil {
				return err
			}
			continue
		}
		if err := os.Chmod(source, 0444); err != nil {
			return err
		}
		if err := os.Rename(source, filepath.Join(bundleDir, finalName)); err != nil {
			return err
		}
	}
	return nil
}

func bundleFileMatches(path string, expected bundleArtifact) (bool, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return false, err
	}
	if !info.Mode().IsRegular() || info.Size() != expected.Size {
		return false, nil
	}
	file, err := os.Open(path)
	if err != nil {
		return false, err
	}
	hasher := sha256.New()
	_, hashErr := io.Copy(hasher, file)
	closeErr := file.Close()
	if hashErr != nil {
		return false, hashErr
	}
	if closeErr != nil {
		return false, closeErr
	}
	return strings.EqualFold(hex.EncodeToString(hasher.Sum(nil)), expected.SHA256), nil
}
func fixPermissions(root string, uid, gid int) error {
	if os.Geteuid() != 0 {
		return errors.New("fix-permissions must run as root")
	}
	return filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		return os.Chown(path, uid, gid)
	})
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
