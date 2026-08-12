package main

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
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
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

const (
	releaseProtocolVersion    = 2
	adminPrefix               = "/opticon/v1/headscale/"
	artifactPrefix            = "/opticon/artifacts/v1/"
	inviteAdminPrefix         = "/opticon/v1/invitations/"
	inviteInventoryPath       = "/opticon/v1/invitations"
	bundleAdminPrefix         = "/opticon/v1/bundles/"
	releaseAdminPath          = "/opticon/v1/releases/manifest"
	releasePreflightPath      = "/opticon/v1/releases/preflight"
	releaseAcquirePath        = "/opticon/v1/releases/acquire"
	releaseRevokeActivePath   = "/opticon/v1/releases/revoke-active"
	releaseReleasePath        = "/opticon/v1/releases/release"
	releaseFinalizePath       = "/opticon/v1/releases/finalize"
	invitePublicPrefix        = "/opticon/i/"
	maxInviteBody             = 64 << 10
	maxBundleChunk            = 4 << 20
	maxAdminBody              = 1 << 20
	maxReleaseLeaseBytes      = 128 << 10
	maxReleaseLeaseInvites    = 64
	maxParallelKeyRevocations = 4
	releaseLeaseLifetime      = 2 * time.Hour
	// A v2 lease records the irreversible boundary before it calls Headscale or
	// removes a hosted invitation. Keep reading v1 journals conservatively so a
	// gateway upgrade can never mistake an old in-flight cancellation for an
	// untouched lease.
	legacyReleaseLeaseSchemaVersion = 1
	releaseLeaseSchemaVersion       = 2
	maxBootstrapArtifactBytes       = 128 << 20
	pinnedSDKVersion                = "10.*.*"
	pinnedRuntimeVersion            = "10.0.10"
	sourceOnlyManifestSchema        = 2
	sourceInstallProtocol           = "source-v1"
	invitationSigningKeyID          = "FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53"
	// The retired invitation signer is allowed only for this immutable ACL
	// transition package. It is never a general release signing channel.
	legacyMachineStateMigrationBridgeVersion = "1.1.41"
)

var trustedSourceManifestKeyID string
var trustedProductSignerThumbprint string
var trustedSigningProfile string

type gateway struct {
	proxy             *httputil.ReverseProxy
	adminSecret       []byte
	headscaleKey      string
	headscaleAdminURL string
	sourceSigner      sourceDownloadSigner
	now               func() time.Time
	artifactDir       string
	manifestPath      string
	bundleDir         string
	inviteDir         string
	nonceDir          string
	publicOrigin      string
	nonces            map[string]time.Time
	nonceMu           sync.Mutex
	inviteMu          sync.Mutex
	bundleMu          sync.Mutex
	manifestMu        sync.RWMutex
	releaseMu         sync.Mutex
	adminSlots        chan struct{}
	artifactSlots     chan struct{}
	proxySlots        chan struct{}
	streamSlots       chan struct{}
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
	if r.URL.Path == releasePreflightPath {
		g.releasePreflight(w, r)
		return
	}
	if r.URL.Path == releaseAcquirePath {
		g.releaseAcquire(w, r)
		return
	}
	if r.URL.Path == releaseRevokeActivePath {
		g.releaseRevokeActive(w, r)
		return
	}
	if r.URL.Path == releaseReleasePath {
		g.releaseRelease(w, r)
		return
	}
	if r.URL.Path == releaseFinalizePath {
		g.releaseFinalize(w, r)
		return
	}
	if r.URL.Path == inviteInventoryPath {
		g.invitationInventory(w, r)
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
	CreatedAt  time.Time `json:"createdAt,omitempty"`
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
	// TailscaleKeyID is non-secret operational metadata. It lets the gateway
	// revoke the one-use Headscale key before deleting a hosted invitation when
	// a source release must be replaced. The encrypted invitation remains the
	// only place that contains the actual auth key.
	TailscaleKeyID string `json:"tailscaleKeyId"`
	Ciphertext     []byte `json:"ciphertext"`
}

// releasePreflightRequest deliberately contains only the stable source version
// that the command center is considering. The gateway reads its own current
// manifest and hosted invitation records; a client never supplies either.
type releasePreflightRequest struct {
	TargetVersion string `json:"targetVersion"`
	ForceRedeploy bool   `json:"forceRedeploy,omitempty"`
}

type releaseAcquireRequest struct {
	TargetVersion      string `json:"targetVersion"`
	DeploymentRevision string `json:"deploymentRevision"`
	LeaseToken         string `json:"leaseToken"`
	ForceRedeploy      bool   `json:"forceRedeploy,omitempty"`
}

type releaseCancellationRequest struct {
	TargetVersion string `json:"targetVersion"`
	LeaseToken    string `json:"leaseToken"`
}

type releaseReleaseRequest struct {
	LeaseToken       string `json:"leaseToken"`
	AbandonUnstarted bool   `json:"abandonUnstarted,omitempty"`
}

type releaseFinalizeRequest struct {
	TargetVersion string `json:"targetVersion"`
	LeaseToken    string `json:"leaseToken"`
}

// releaseInvitationSummary is safe to return to an authenticated command
// center. In particular it must never gain Ciphertext or any invitation
// secrets: those fields would make an inventory endpoint a credential export.
type releaseInvitationSummary struct {
	IDHash          string    `json:"idHash"`
	DeviceName      string    `json:"deviceName"`
	Role            string    `json:"role"`
	CreatedAt       time.Time `json:"createdAt"`
	ExpiresAt       time.Time `json:"expiresAt"`
	ReleaseVersion  string    `json:"releaseVersion"`
	SourceFile      string    `json:"sourceFile"`
	InstallProtocol string    `json:"installProtocol"`
	CanRevoke       bool      `json:"canRevoke"`
	BlockedReason   string    `json:"blockedReason,omitempty"`
}

type invitationInventoryResponse struct {
	SchemaVersion int                        `json:"schemaVersion"`
	Invitations   []releaseInvitationSummary `json:"invitations"`
}

type releasePreflightResponse struct {
	SchemaVersion           int       `json:"schemaVersion"`
	GatewayReleaseProtocol  int       `json:"gatewayReleaseProtocol"`
	TargetVersion           string    `json:"targetVersion"`
	DeployedVersion         string    `json:"deployedVersion"`
	AlreadyDeployed         bool      `json:"alreadyDeployed"`
	ForceRedeploy           bool      `json:"forceRedeploy,omitempty"`
	TargetIsOlder           bool      `json:"targetIsOlder"`
	DeploymentBlocked       bool      `json:"deploymentBlocked"`
	DeploymentBlockedReason string    `json:"deploymentBlockedReason,omitempty"`
	LeaseExpiresAt          time.Time `json:"leaseExpiresAt,omitempty"`
	// LeaseTokenSHA256 is an authenticated, non-bearer fingerprint. It lets a
	// Command Center prove that its locally protected recovery token belongs to
	// this live lease without ever returning the raw token from the gateway.
	LeaseTokenSHA256          string                     `json:"leaseTokenSha256,omitempty"`
	DeploymentRevision        string                     `json:"deploymentRevision"`
	RequiresInvitationRemoval bool                       `json:"requiresInvitationRemoval"`
	CancellationBlocked       bool                       `json:"cancellationBlocked"`
	Manifest                  artifactManifest           `json:"manifest"`
	BlockingInvitations       []releaseInvitationSummary `json:"blockingInvitations"`
}

type releaseLeaseResponse struct {
	LeaseToken       string    `json:"leaseToken"`
	ExpiresAt        time.Time `json:"expiresAt"`
	RemovedInviteIDs []string  `json:"removedInviteIds"`
}

type releaseCancellationResponse struct {
	RemovedCount     int      `json:"removedCount"`
	RemovedInviteIDs []string `json:"removedInviteIds"`
}

type activeReleaseInvite struct {
	IDHash string
	Path   string
	Invite hostedInvite
	Digest string
}

// releaseLease is an on-volume recovery journal for one deployment. The raw
// bearer token is never persisted: only its SHA-256 digest is stored here.
// CancellationStarted is written before the first network-key revocation or
// hosted-link removal. Once set, the transaction must be resumed rather than
// released, even if the gateway crashed before recording a later outcome.
// The list lets a retry return the full original removal result after a lost
// response or a partial filesystem failure.
type releaseLease struct {
	SchemaVersion        int                  `json:"schemaVersion"`
	TargetVersion        string               `json:"targetVersion"`
	DeploymentRevision   string               `json:"deploymentRevision"`
	TokenSHA256          string               `json:"tokenSha256"`
	ExpiresAt            time.Time            `json:"expiresAt"`
	Invitations          []releaseLeaseInvite `json:"invitations"`
	CancellationStarted  bool                 `json:"cancellationStarted"`
	CancellationComplete bool                 `json:"cancellationComplete"`
	RemovedInviteIDs     []string             `json:"removedInviteIds"`
	ForceRedeploy        bool                 `json:"forceRedeploy,omitempty"`
}

type releaseLeaseInvite struct {
	IDHash         string `json:"idHash"`
	TailscaleKeyID string `json:"tailscaleKeyId"`
}

type artifactManifest struct {
	SchemaVersion int              `json:"schemaVersion"`
	Artifacts     []bundleArtifact `json:"artifacts"`
}

type bundleArtifact struct {
	Product                         string                        `json:"product"`
	Version                         string                        `json:"version"`
	Role                            string                        `json:"role,omitempty"`
	Architecture                    string                        `json:"architecture"`
	File                            string                        `json:"file"`
	Size                            int64                         `json:"size"`
	SHA256                          string                        `json:"sha256"`
	SignerThumbprint                string                        `json:"signerThumbprint,omitempty"`
	DownloadURL                     string                        `json:"downloadUrl,omitempty"`
	SDKVersion                      string                        `json:"sdkVersion,omitempty"`
	RuntimeVersion                  string                        `json:"runtimeVersion,omitempty"`
	SourceManifestSHA256            string                        `json:"sourceManifestSha256,omitempty"`
	SourceManifestKeyID             string                        `json:"sourceManifestKeyId,omitempty"`
	SourceLauncherFile              string                        `json:"sourceLauncherFile,omitempty"`
	SourceLauncherSize              int64                         `json:"sourceLauncherSize,omitempty"`
	SourceLauncherSHA256            string                        `json:"sourceLauncherSha256,omitempty"`
	SigningProfile                  string                        `json:"signingProfile,omitempty"`
	ProductSigner                   string                        `json:"productSignerThumbprint,omitempty"`
	LegacyMigrationSignerThumbprint string                        `json:"legacyMigrationSignerThumbprint,omitempty"`
	TargetRuntime                   string                        `json:"targetRuntime,omitempty"`
	TargetRuntimes                  []string                      `json:"targetRuntimes,omitempty"`
	ClientInstallValidation         clientInstallValidationPolicy `json:"clientInstallValidation,omitempty"`
}

type clientInstallValidationPolicy struct {
	DisableAll    bool     `json:"disableAll,omitempty"`
	DisabledSteps []string `json:"disabledSteps,omitempty"`
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
	// fsyncing the file makes its bytes durable; fsyncing the parent directory
	// makes the name replacement durable too. Release leases record an
	// irreversible cancellation boundary, so returning before the rename is
	// committed would permit a power loss to forget that boundary after a
	// Headscale key was revoked.
	return syncDirectory(filepath.Dir(path))
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
	// Global lock order is release -> manifest -> invitation. Keep the release
	// lease for the entire commit so no invitation mutation can pass its final
	// check between manifest validation and the atomic write.
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
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
	// The lease records whether the operator explicitly requested a same-version
	// emergency replacement. Ordinary deployments retain immutable versions.
	lease, err := g.requireManifestLeaseLocked(r.Header.Get("X-Opticon-Release-Lease"), nextVersion, g.currentTime())
	if err != nil {
		http.Error(w, "release deployment lease is unavailable or does not match this manifest", http.StatusConflict)
		return
	}
	if currentOK {
		comparison, valid := compareSemanticVersions(nextVersion, currentVersion)
		if !valid || comparison < 0 {
			http.Error(w, "release downgrade refused", http.StatusConflict)
			return
		}
		if comparison == 0 && !sameReleaseArtifacts(current, next, currentVersion) && (lease == nil || !lease.ForceRedeploy) {
			http.Error(w, "release version is immutable", http.StatusConflict)
			return
		}
	}
	if !samePinnedDependencies(current, next) {
		http.Error(w, "pinned dependencies changed", http.StatusConflict)
		return
	}
	// A live release lease spans cancellation through this atomic manifest
	// commit. It stops a concurrent invite write from making the just-reviewed
	// snapshot stale during a long source build and upload.
	if err := g.requireActiveInviteArtifacts(next, g.currentTime()); err != nil {
		http.Error(w, "manifest would break an active invitation", http.StatusConflict)
		return
	}
	err = writeFileAtomically(g.artifactManifestPath(), body)
	if err != nil {
		http.Error(w, "storage unavailable", http.StatusInternalServerError)
		return
	}
	if lease != nil {
		if err := g.removeReleaseLeaseLocked(); err != nil {
			// The manifest is already committed. Preserve the successful response;
			// the bounded lease will expire if this best-effort cleanup cannot run.
			log.Printf("manifest %s published but release lease cleanup failed: %v", nextVersion, err)
		}
	}
	writeJSON(w, http.StatusCreated, map[string]any{"published": true, "version": nextVersion})
}

// releasePreflight provides the command center with the exact state that the
// manifest publisher will enforce. It is authenticated because the list of
// pending device names and invitation expiry times is operational metadata.
// It makes no changes, so a decline in the Command Center is side-effect free.
func (g *gateway) releasePreflight(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	body, err := g.readAdminBody(w, r, maxAdminBody)
	if err != nil {
		g.writeAdminBodyError(w, err, "invalid release preflight")
		return
	}
	if !g.authenticate(r, body, g.currentTime()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var request releasePreflightRequest
	if json.Unmarshal(body, &request) != nil {
		http.Error(w, "invalid release preflight", http.StatusBadRequest)
		return
	}
	preflight, err := g.buildReleasePreflight(request.TargetVersion, g.currentTime(), request.ForceRedeploy)
	if err != nil {
		http.Error(w, "release preflight unavailable", http.StatusServiceUnavailable)
		return
	}
	lease, err := g.currentReleaseLease(g.currentTime())
	if err != nil {
		http.Error(w, "release deployment state unavailable", http.StatusServiceUnavailable)
		return
	}
	if lease != nil {
		preflight.DeploymentBlocked = true
		preflight.DeploymentBlockedReason = "Another Opticon release deployment is in progress. Wait for it to commit or expire before starting a new one."
		preflight.LeaseExpiresAt = lease.ExpiresAt
		preflight.LeaseTokenSHA256 = lease.TokenSHA256
	}
	writeJSON(w, http.StatusOK, preflight)
}

// releaseAcquire is the post-confirmation boundary. It binds the UI's
// reviewed preflight snapshot to an opaque server-side lease before any invite
// key is revoked. While the lease is live, all admin invitation mutations are
// rejected so a long source build cannot race the manifest commit.
func (g *gateway) releaseAcquire(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	body, err := g.readAdminBody(w, r, maxAdminBody)
	if err != nil {
		g.writeAdminBodyError(w, err, "invalid release acquisition")
		return
	}
	if !g.authenticate(r, body, g.currentTime()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var request releaseAcquireRequest
	if json.Unmarshal(body, &request) != nil || !validStableReleaseVersion(request.TargetVersion) ||
		!inviteHashPattern.MatchString(strings.ToLower(request.DeploymentRevision)) || !validReleaseLeaseToken(request.LeaseToken) {
		http.Error(w, "invalid release acquisition", http.StatusBadRequest)
		return
	}

	lease, err := g.acquireReleaseLease(request.TargetVersion, strings.ToLower(request.DeploymentRevision), request.LeaseToken, g.currentTime(), request.ForceRedeploy)
	if err != nil {
		http.Error(w, err.Error(), http.StatusConflict)
		return
	}
	writeJSON(w, http.StatusCreated, releaseLeaseResponse{
		LeaseToken:       request.LeaseToken,
		ExpiresAt:        lease.ExpiresAt,
		RemovedInviteIDs: append([]string(nil), lease.RemovedInviteIDs...),
	})
}

// releaseRelease abandons a lease only before invitation cancellation starts.
// After the durable journal crosses that boundary it is intentionally retained
// for retry/recovery until the manifest commit or expiry.
func (g *gateway) releaseRelease(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	body, err := g.readAdminBody(w, r, maxAdminBody)
	if err != nil {
		g.writeAdminBodyError(w, err, "invalid release lease release")
		return
	}
	if !g.authenticate(r, body, g.currentTime()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var request releaseReleaseRequest
	if json.Unmarshal(body, &request) != nil ||
		(request.AbandonUnstarted && request.LeaseToken != "") ||
		(!request.AbandonUnstarted && !validReleaseLeaseToken(request.LeaseToken)) {
		http.Error(w, "invalid release lease release", http.StatusBadRequest)
		return
	}
	if err := g.releaseUncancelledLease(request.LeaseToken, request.AbandonUnstarted, g.currentTime()); err != nil {
		http.Error(w, err.Error(), http.StatusConflict)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// releaseFinalize clears a recovered lease only after independently proving
// that the exact target is live in the gateway manifest. It repairs the rare
// case where the manifest write succeeded but the publisher lost its response
// or post-commit lease-file cleanup failed.
func (g *gateway) releaseFinalize(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	body, err := g.readAdminBody(w, r, maxAdminBody)
	if err != nil {
		g.writeAdminBodyError(w, err, "invalid release finalization")
		return
	}
	if !g.authenticate(r, body, g.currentTime()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var request releaseFinalizeRequest
	if json.Unmarshal(body, &request) != nil || !validStableReleaseVersion(request.TargetVersion) ||
		!validReleaseLeaseToken(request.LeaseToken) {
		http.Error(w, "invalid release finalization", http.StatusBadRequest)
		return
	}
	if err := g.finalizeReleaseLease(request.TargetVersion, request.LeaseToken, g.currentTime()); err != nil {
		http.Error(w, err.Error(), http.StatusConflict)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// releaseRevokeActive continues the durable lease transaction. The lease
// captures the exact invitation snapshot seen by the operator; keys are
// revoked before hosted links are removed. Its journal makes retries return
// the full original result even after a partial delete or lost response.
func (g *gateway) releaseRevokeActive(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	body, err := g.readAdminBody(w, r, maxAdminBody)
	if err != nil {
		g.writeAdminBodyError(w, err, "invalid release cancellation")
		return
	}
	if !g.authenticate(r, body, g.currentTime()) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var request releaseCancellationRequest
	if json.Unmarshal(body, &request) != nil || !validStableReleaseVersion(request.TargetVersion) ||
		!validReleaseLeaseToken(request.LeaseToken) {
		http.Error(w, "invalid release cancellation", http.StatusBadRequest)
		return
	}

	removed, err := g.revokeLeaseInvitations(request.TargetVersion, request.LeaseToken, g.currentTime())
	if err != nil {
		if errors.Is(err, errReleaseLeaseConflict) {
			http.Error(w, err.Error(), http.StatusConflict)
			return
		}
		http.Error(w, "could not revoke and remove every active invitation", http.StatusBadGateway)
		return
	}
	writeJSON(w, http.StatusOK, releaseCancellationResponse{RemovedCount: len(removed), RemovedInviteIDs: removed})
}

var errReleaseLeaseConflict = errors.New("release deployment lease changed or expired")

func (g *gateway) writeAdminBodyError(w http.ResponseWriter, err error, invalidMessage string) {
	if errors.Is(err, errAdminBusy) {
		http.Error(w, err.Error(), http.StatusTooManyRequests)
		return
	}
	http.Error(w, invalidMessage, http.StatusBadRequest)
}

func (g *gateway) currentTime() time.Time {
	if g.now != nil {
		return g.now()
	}
	return time.Now()
}

func validStableReleaseVersion(value string) bool {
	parsed, valid := parseSemanticVersion(value)
	return valid && parsed.core[0] != "0" && !strings.ContainsAny(value, "-+")
}

func (g *gateway) buildReleasePreflight(targetVersion string, now time.Time, forceOptions ...bool) (releasePreflightResponse, error) {
	forceRedeploy := len(forceOptions) != 0 && forceOptions[0]
	if !validStableReleaseVersion(targetVersion) {
		return releasePreflightResponse{}, errors.New("target release version is invalid")
	}
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return releasePreflightResponse{}, err
	}
	deployedVersion, deployed := highestReleaseVersion(manifest)
	if !deployed || !validStableReleaseVersion(deployedVersion) {
		return releasePreflightResponse{}, errors.New("release manifest has no deployable source version")
	}
	comparison, valid := compareSemanticVersions(targetVersion, deployedVersion)
	if !valid {
		return releasePreflightResponse{}, errors.New("release version comparison failed")
	}
	alreadyDeployed := !forceRedeploy && comparison == 0 && completeCloudFrontRelease(manifest, targetVersion)
	targetIsOlder := comparison < 0
	active, err := g.activeReleaseInvites(now)
	if err != nil {
		return releasePreflightResponse{}, err
	}
	requiresRemoval := !alreadyDeployed && !targetIsOlder && len(active) != 0
	response := releasePreflightResponse{
		SchemaVersion:             1,
		GatewayReleaseProtocol:    releaseProtocolVersion,
		TargetVersion:             targetVersion,
		DeployedVersion:           deployedVersion,
		AlreadyDeployed:           alreadyDeployed,
		ForceRedeploy:             forceRedeploy,
		TargetIsOlder:             targetIsOlder,
		DeploymentRevision:        releaseInvitationRevision(targetVersion, active),
		RequiresInvitationRemoval: requiresRemoval,
		Manifest:                  manifest,
		BlockingInvitations:       []releaseInvitationSummary{},
	}
	if requiresRemoval && len(active) > maxReleaseLeaseInvites {
		// Do not ask for confirmation which this version cannot safely fulfill.
		// A bounded transaction guarantees the lease outlives key revocation and
		// lets a lost response remain recoverable as one coherent operation.
		response.RequiresInvitationRemoval = false
		response.DeploymentBlocked = true
		response.DeploymentBlockedReason = fmt.Sprintf(
			"%d active invitations exceed this release transaction's safe limit of %d. Reconcile them in smaller safe batches before publishing a new source release.",
			len(active), maxReleaseLeaseInvites)
		return response, nil
	}
	if !requiresRemoval {
		return response, nil
	}
	response.BlockingInvitations = make([]releaseInvitationSummary, 0, len(active))
	for _, item := range active {
		canRevoke := strings.TrimSpace(item.Invite.TailscaleKeyID) != ""
		summary := releaseInvitationSummary{
			IDHash:          item.IDHash,
			DeviceName:      item.Invite.DeviceName,
			Role:            item.Invite.Role,
			CreatedAt:       item.Invite.CreatedAt,
			ExpiresAt:       item.Invite.ExpiresAt,
			ReleaseVersion:  item.Invite.ReleaseVersion,
			SourceFile:      item.Invite.SourceFile,
			InstallProtocol: item.Invite.InstallProtocol,
			CanRevoke:       canRevoke,
		}
		if !canRevoke {
			// Records written before the key-identity field was introduced can
			// still be abandoned explicitly. Deleting the encrypted hosted record
			// makes the link unusable, but a recipient that already extracted its
			// one-use key may retain access until the recorded expiry. The command
			// center surfaces that distinction in its default-No confirmation.
			summary.BlockedReason = "Its hosted link can be removed, but its network key identity is unavailable and the key may remain usable until the invitation expires."
		}
		response.BlockingInvitations = append(response.BlockingInvitations, summary)
	}
	return response, nil
}

func (g *gateway) activeReleaseInvites(now time.Time) ([]activeReleaseInvite, error) {
	g.inviteMu.Lock()
	defer g.inviteMu.Unlock()
	return g.activeReleaseInvitesLocked(now)
}

func (g *gateway) activeReleaseInvitesLocked(now time.Time) ([]activeReleaseInvite, error) {
	if g.inviteDir == "" {
		return []activeReleaseInvite{}, nil
	}
	entries, err := os.ReadDir(g.inviteDir)
	if err != nil {
		return nil, err
	}
	result := make([]activeReleaseInvite, 0, len(entries))
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".json") {
			continue
		}
		idHash := strings.TrimSuffix(entry.Name(), ".json")
		if !inviteHashPattern.MatchString(idHash) {
			return nil, errors.New("an invitation record has an invalid identity")
		}
		path := filepath.Join(g.inviteDir, entry.Name())
		data, readErr := os.ReadFile(path)
		if readErr != nil || len(data) == 0 || len(data) > maxInviteBody*2 {
			return nil, errors.New("an invitation record could not be read safely")
		}
		var invite hostedInvite
		if json.Unmarshal(data, &invite) != nil {
			return nil, errors.New("an invitation record is corrupt")
		}
		// Match the manifest publisher's definition of a blocking invitation.
		if !invite.ExpiresAt.After(now) || invite.ReleaseVersion == "" {
			continue
		}
		if invite.InstallProtocol != "" && invite.InstallProtocol != sourceInstallProtocol {
			return nil, errors.New("an invitation record has an unsupported install protocol")
		}
		digest := sha256.Sum256(data)
		result = append(result, activeReleaseInvite{
			IDHash: idHash,
			Path:   path,
			Invite: invite,
			Digest: hex.EncodeToString(digest[:]),
		})
	}
	sort.Slice(result, func(left, right int) bool { return result[left].IDHash < result[right].IDHash })
	return result, nil
}

func releaseInvitationRevision(targetVersion string, active []activeReleaseInvite) string {
	hash := sha256.New()
	_, _ = io.WriteString(hash, "target="+targetVersion+"\n")
	for _, item := range active {
		_, _ = io.WriteString(hash, item.IDHash+":"+item.Digest+"\n")
	}
	return hex.EncodeToString(hash.Sum(nil))
}

func (g *gateway) releaseLeasePath() string {
	// The live manifest path is on the durable writable volume in production;
	// artifactDir is image content and intentionally read-only there.
	return filepath.Join(filepath.Dir(g.artifactManifestPath()), "release-deployment.json")
}

func validReleaseLeaseToken(token string) bool {
	if len(token) != 43 || strings.TrimSpace(token) != token {
		return false
	}
	raw, err := base64.RawURLEncoding.DecodeString(token)
	return err == nil && len(raw) == 32
}

func releaseLeaseTokenHash(token string) string {
	digest := sha256.Sum256([]byte(token))
	return hex.EncodeToString(digest[:])
}

func (g *gateway) currentReleaseLease(now time.Time) (*releaseLease, error) {
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
	return g.readReleaseLeaseLocked(now)
}

func (g *gateway) readReleaseLeaseLocked(now time.Time) (*releaseLease, error) {
	path := g.releaseLeasePath()
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil || len(data) == 0 || len(data) > maxReleaseLeaseBytes {
		return nil, errors.New("release deployment journal could not be read safely")
	}
	var lease releaseLease
	if json.Unmarshal(data, &lease) != nil || !validStoredReleaseLease(lease) {
		return nil, errors.New("release deployment journal is corrupt")
	}
	if !lease.ExpiresAt.After(now) {
		if err := g.removeReleaseLeaseLocked(); err != nil {
			return nil, errors.New("expired release deployment journal could not be removed")
		}
		return nil, nil
	}
	return &lease, nil
}

func validStoredReleaseLease(lease releaseLease) bool {
	if (lease.SchemaVersion != legacyReleaseLeaseSchemaVersion && lease.SchemaVersion != releaseLeaseSchemaVersion) ||
		!validStableReleaseVersion(lease.TargetVersion) ||
		!inviteHashPattern.MatchString(strings.ToLower(lease.DeploymentRevision)) ||
		!inviteHashPattern.MatchString(strings.ToLower(lease.TokenSHA256)) || lease.ExpiresAt.IsZero() ||
		len(lease.Invitations) > maxReleaseLeaseInvites || len(lease.RemovedInviteIDs) > maxReleaseLeaseInvites {
		return false
	}
	ids := make(map[string]struct{}, len(lease.Invitations))
	for _, invite := range lease.Invitations {
		if !inviteHashPattern.MatchString(invite.IDHash) || len(invite.TailscaleKeyID) > 512 ||
			strings.ContainsAny(invite.TailscaleKeyID, "\r\n") {
			return false
		}
		if _, exists := ids[invite.IDHash]; exists {
			return false
		}
		ids[invite.IDHash] = struct{}{}
	}
	if len(lease.Invitations) == 0 && (!lease.CancellationComplete || lease.CancellationStarted) {
		return false
	}
	if !lease.CancellationComplete {
		return len(lease.RemovedInviteIDs) == 0
	}
	if len(lease.RemovedInviteIDs) != len(lease.Invitations) {
		return false
	}
	removed := make(map[string]struct{}, len(lease.RemovedInviteIDs))
	for _, idHash := range lease.RemovedInviteIDs {
		if _, exists := ids[idHash]; !exists {
			return false
		}
		if _, exists := removed[idHash]; exists {
			return false
		}
		removed[idHash] = struct{}{}
	}
	// Version 1 predates CancellationStarted. It remains readable so a
	// pre-upgrade journal can be completed, but a newly-written (v2) journal
	// must never claim completed invitation cancellation without first having
	// crossed and durably recorded that boundary.
	if lease.SchemaVersion == releaseLeaseSchemaVersion && len(lease.Invitations) != 0 && !lease.CancellationStarted {
		return false
	}
	return true
}

func (g *gateway) writeReleaseLeaseLocked(lease releaseLease) error {
	if !validStoredReleaseLease(lease) {
		return errors.New("release deployment journal is invalid")
	}
	body, err := json.Marshal(lease)
	if err != nil {
		return err
	}
	return writeFileAtomically(g.releaseLeasePath(), body)
}

func (g *gateway) removeReleaseLeaseLocked() error {
	path := g.releaseLeasePath()
	err := os.Remove(path)
	if err != nil && !os.IsNotExist(err) {
		return err
	}
	if err == nil {
		return syncDirectory(filepath.Dir(path))
	}
	return nil
}

func (g *gateway) acquireReleaseLease(targetVersion, revision, token string, now time.Time, forceOptions ...bool) (releaseLease, error) {
	forceRedeploy := len(forceOptions) != 0 && forceOptions[0]
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
	if existing, err := g.readReleaseLeaseLocked(now); err != nil {
		return releaseLease{}, err
	} else if existing != nil {
		// The client persists this token before POSTing it. If a successful
		// acquire response is lost, the exact same request can safely recover
		// the durable transaction without ever storing the raw token server-side.
		if existing.TargetVersion == targetVersion && existing.DeploymentRevision == revision &&
			existing.ForceRedeploy == forceRedeploy &&
			hmac.Equal([]byte(existing.TokenSHA256), []byte(releaseLeaseTokenHash(token))) {
			return *existing, nil
		}
		return releaseLease{}, fmt.Errorf("%w: an Opticon release deployment remains active until %s", errReleaseLeaseConflict, existing.ExpiresAt.UTC().Format(time.RFC3339))
	}

	preflight, err := g.buildReleasePreflight(targetVersion, now, forceRedeploy)
	if err != nil {
		return releaseLease{}, err
	}
	if preflight.AlreadyDeployed || preflight.TargetIsOlder {
		return releaseLease{}, fmt.Errorf("%w: the requested release is not publishable", errReleaseLeaseConflict)
	}
	if !hmac.Equal([]byte(revision), []byte(preflight.DeploymentRevision)) {
		return releaseLease{}, fmt.Errorf("%w: active invitation state changed; refresh before confirming removal", errReleaseLeaseConflict)
	}
	active, err := g.activeReleaseInvites(now)
	if err != nil {
		return releaseLease{}, err
	}
	if !hmac.Equal([]byte(revision), []byte(releaseInvitationRevision(targetVersion, active))) {
		return releaseLease{}, fmt.Errorf("%w: active invitation state changed; refresh before confirming removal", errReleaseLeaseConflict)
	}
	if len(active) > maxReleaseLeaseInvites {
		return releaseLease{}, fmt.Errorf("%w: too many active invitations for one safe release transaction", errReleaseLeaseConflict)
	}
	items := make([]releaseLeaseInvite, 0, len(active))
	for _, item := range active {
		items = append(items, releaseLeaseInvite{IDHash: item.IDHash, TailscaleKeyID: item.Invite.TailscaleKeyID})
	}
	lease := releaseLease{
		SchemaVersion:        releaseLeaseSchemaVersion,
		TargetVersion:        targetVersion,
		DeploymentRevision:   revision,
		TokenSHA256:          releaseLeaseTokenHash(token),
		ExpiresAt:            now.Add(releaseLeaseLifetime).UTC(),
		Invitations:          items,
		CancellationComplete: len(items) == 0,
		RemovedInviteIDs:     []string{},
		ForceRedeploy:        forceRedeploy,
	}
	if err := g.writeReleaseLeaseLocked(lease); err != nil {
		return releaseLease{}, err
	}
	return lease, nil
}

func (g *gateway) releaseUncancelledLease(token string, abandonUnstarted bool, now time.Time) error {
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
	lease, err := g.readReleaseLeaseLocked(now)
	if err != nil {
		return err
	}
	if lease == nil || (!abandonUnstarted &&
		!hmac.Equal([]byte(lease.TokenSHA256), []byte(releaseLeaseTokenHash(token)))) {
		return errReleaseLeaseConflict
	}
	// A v1 journal did not have a durable cancellation boundary. Treat every
	// non-empty v1 lease as potentially in progress: releasing it could make a
	// partially-revoked invitation mutable again after a gateway upgrade.
	if lease.CancellationStarted || (lease.SchemaVersion == legacyReleaseLeaseSchemaVersion && len(lease.Invitations) != 0) {
		return fmt.Errorf("%w: invitation cancellation may have started; retry the manifest publication instead", errReleaseLeaseConflict)
	}
	return g.removeReleaseLeaseLocked()
}

func (g *gateway) finalizeReleaseLease(targetVersion, token string, now time.Time) error {
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
	lease, err := g.readReleaseLeaseLocked(now)
	if err != nil {
		return err
	}
	if lease != nil && (lease.TargetVersion != targetVersion || !lease.CancellationComplete ||
		!hmac.Equal([]byte(lease.TokenSHA256), []byte(releaseLeaseTokenHash(token)))) {
		return errReleaseLeaseConflict
	}
	// Global lock order remains release -> manifest. A target is final only if
	// its complete immutable source artifact is what the gateway now serves.
	manifest, err := g.readArtifactManifest()
	if err != nil {
		return err
	}
	if !completeCloudFrontRelease(manifest, targetVersion) {
		return fmt.Errorf("%w: the target manifest is not live", errReleaseLeaseConflict)
	}
	deployed, ok := highestReleaseVersion(manifest)
	if !ok || deployed != targetVersion {
		return fmt.Errorf("%w: the target version is not the deployed release", errReleaseLeaseConflict)
	}
	if lease == nil {
		return nil
	}
	return g.removeReleaseLeaseLocked()
}

func (g *gateway) revokeLeaseInvitations(targetVersion, token string, now time.Time) ([]string, error) {
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
	lease, err := g.readReleaseLeaseLocked(now)
	if err != nil {
		return nil, err
	}
	if lease == nil || lease.TargetVersion != targetVersion ||
		!hmac.Equal([]byte(lease.TokenSHA256), []byte(releaseLeaseTokenHash(token))) {
		return nil, errReleaseLeaseConflict
	}
	if lease.CancellationComplete {
		return append([]string(nil), lease.RemovedInviteIDs...), nil
	}
	// Persist the irreversible boundary before issuing a single Headscale call
	// or deleting a hosted record. If the process dies after this write, a retry
	// will safely repeat revocation (404 is accepted) instead of releasing a
	// lease that might already have changed the outside world.
	if lease.SchemaVersion != releaseLeaseSchemaVersion || !lease.CancellationStarted {
		lease.SchemaVersion = releaseLeaseSchemaVersion
		lease.CancellationStarted = true
		if err := g.writeReleaseLeaseLocked(*lease); err != nil {
			return nil, err
		}
	}
	// Bound revocations so even the largest supported transaction completes
	// well inside the two-hour lease while every key still finishes before any
	// hosted link is deleted. A retry is safe because Headscale 404 is accepted.
	revocable := make([]releaseLeaseInvite, 0, len(lease.Invitations))
	for _, invite := range lease.Invitations {
		if strings.TrimSpace(invite.TailscaleKeyID) != "" {
			revocable = append(revocable, invite)
		}
	}
	workers := maxParallelKeyRevocations
	if len(revocable) < workers {
		workers = len(revocable)
	}
	if workers > 0 {
		jobs := make(chan releaseLeaseInvite)
		errs := make(chan error, len(revocable))
		var wait sync.WaitGroup
		for worker := 0; worker < workers; worker++ {
			wait.Add(1)
			go func() {
				defer wait.Done()
				for invite := range jobs {
					if err := g.revokeHostedInviteKey(invite.TailscaleKeyID); err != nil {
						errs <- err
					}
				}
			}()
		}
		for _, invite := range revocable {
			jobs <- invite
		}
		close(jobs)
		wait.Wait()
		close(errs)
		for err := range errs {
			if err != nil {
				return nil, err
			}
		}
	}
	for _, invite := range lease.Invitations {
		path := filepath.Join(g.inviteDir, invite.IDHash+".json")
		if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
			return nil, err
		}
	}
	// The completed journal is authoritative on retries. Do not write it until
	// the directory entries for every removed hosted link are durable too; a
	// power loss must not resurrect a link while the journal says cancellation
	// has already finished.
	if err := syncDirectory(g.inviteDir); err != nil {
		return nil, err
	}
	lease.CancellationComplete = true
	lease.RemovedInviteIDs = make([]string, 0, len(lease.Invitations))
	for _, invite := range lease.Invitations {
		lease.RemovedInviteIDs = append(lease.RemovedInviteIDs, invite.IDHash)
	}
	if err := g.writeReleaseLeaseLocked(*lease); err != nil {
		return nil, err
	}
	return append([]string(nil), lease.RemovedInviteIDs...), nil
}

func (g *gateway) requireManifestLeaseLocked(token, targetVersion string, now time.Time) (*releaseLease, error) {
	lease, err := g.readReleaseLeaseLocked(now)
	if err != nil {
		return nil, err
	}
	if lease == nil {
		if token == "" {
			return nil, nil
		}
		return nil, errReleaseLeaseConflict
	}
	if !validReleaseLeaseToken(token) || lease.TargetVersion != targetVersion || !lease.CancellationComplete ||
		!hmac.Equal([]byte(lease.TokenSHA256), []byte(releaseLeaseTokenHash(token))) {
		return nil, errReleaseLeaseConflict
	}
	return lease, nil
}

func (g *gateway) revokeHostedInviteKey(keyID string) error {
	if strings.TrimSpace(keyID) == "" {
		return errors.New("hosted invitation has no key identity")
	}
	body, err := json.Marshal(struct {
		ID string `json:"id"`
	}{ID: keyID})
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	headscaleURL := g.headscaleAdminURL
	if headscaleURL == "" {
		headscaleURL = "http://127.0.0.1:8081"
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, strings.TrimRight(headscaleURL, "/")+"/api/v1/preauthkey/expire", bytes.NewReader(body))
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", "Bearer "+g.headscaleKey)
	request.Header.Set("Content-Type", "application/json")
	response, err := (&http.Client{Timeout: 15 * time.Second}).Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 8<<10))
	if response.StatusCode == http.StatusNotFound || response.StatusCode == http.StatusGone ||
		(response.StatusCode >= http.StatusOK && response.StatusCode < http.StatusMultipleChoices) {
		return nil
	}
	return errors.New("Headscale pre-authentication key revocation failed")
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
		!inviteHashPattern.MatchString(strings.ToLower(artifact.SourceManifestSHA256)) || !validProductionArtifactTrust(artifact) ||
		!validClientInstallValidationPolicy(artifact.ClientInstallValidation) {
		return false
	}
	version, valid := parseSemanticVersion(artifact.Version)
	if !valid || version.core[0] == "0" || strings.ContainsAny(artifact.Version, "-+") || !validCloudFrontDownloadURL(artifact) {
		return false
	}
	comparison, _ := compareSemanticVersions(artifact.Version, "1.2.1")
	return comparison < 0 || validSourceLauncherMetadata(artifact)
}

func validClientInstallValidationPolicy(policy clientInstallValidationPolicy) bool {
	known := map[string]bool{
		"InvitationAuthenticity": true, "InvitationConstraints": true, "ProtectedPaths": true,
		"DownloadIntegrity": true, "SourceArchiveAuthenticity": true, "LauncherBinding": true,
		"SourceBuildProvenance": true, "SetupPreflight": true, "MachineState": true,
		"PayloadAuthenticity": true, "DependencyIntegrity": true, "ComponentPostconditions": true,
		"NetworkIdentity": true, "FirewallPolicy": true, "EnrollmentConfirmation": true,
	}
	seen := make(map[string]bool, len(policy.DisabledSteps))
	for _, step := range policy.DisabledSteps {
		if !known[step] || seen[step] {
			return false
		}
		seen[step] = true
	}
	return true
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

func (g *gateway) invitationInventory(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
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

	// Keep the same global order used by invitation mutations and release
	// transactions so this is one authoritative point-in-time inventory.
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
	g.inviteMu.Lock()
	defer g.inviteMu.Unlock()

	now := g.currentTime()
	entries, err := os.ReadDir(g.inviteDir)
	if err != nil {
		http.Error(w, "invitation inventory unavailable", http.StatusServiceUnavailable)
		return
	}
	items := make([]releaseInvitationSummary, 0, len(entries))
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".json") {
			continue
		}
		idHash := strings.TrimSuffix(entry.Name(), ".json")
		if !inviteHashPattern.MatchString(idHash) {
			http.Error(w, "invitation inventory contains an invalid identity", http.StatusServiceUnavailable)
			return
		}
		path := filepath.Join(g.inviteDir, entry.Name())
		data, readErr := os.ReadFile(path)
		if readErr != nil || len(data) == 0 || len(data) > maxInviteBody*2 {
			http.Error(w, "invitation inventory could not be read safely", http.StatusServiceUnavailable)
			return
		}
		var invite hostedInvite
		if json.Unmarshal(data, &invite) != nil {
			http.Error(w, "invitation inventory contains a corrupt record", http.StatusServiceUnavailable)
			return
		}
		if !invite.ExpiresAt.After(now) {
			continue
		}
		createdAt := invite.CreatedAt
		if createdAt.IsZero() {
			info, statErr := entry.Info()
			if statErr != nil {
				http.Error(w, "invitation inventory metadata is unavailable", http.StatusServiceUnavailable)
				return
			}
			createdAt = info.ModTime().UTC()
		}
		canRevoke := strings.TrimSpace(invite.TailscaleKeyID) != ""
		summary := releaseInvitationSummary{
			IDHash:          idHash,
			DeviceName:      invite.DeviceName,
			Role:            invite.Role,
			CreatedAt:       createdAt,
			ExpiresAt:       invite.ExpiresAt,
			ReleaseVersion:  invite.ReleaseVersion,
			SourceFile:      invite.SourceFile,
			InstallProtocol: invite.InstallProtocol,
			CanRevoke:       canRevoke,
		}
		if !canRevoke {
			summary.BlockedReason = "Its hosted link can be removed, but its network key identity is unavailable and the key may remain usable until the invitation expires."
		}
		items = append(items, summary)
	}
	sort.Slice(items, func(left, right int) bool {
		if items[left].CreatedAt.Equal(items[right].CreatedAt) {
			return items[left].IDHash < items[right].IDHash
		}
		return items[left].CreatedAt.After(items[right].CreatedAt)
	})
	writeJSON(w, http.StatusOK, invitationInventoryResponse{SchemaVersion: 1, Invitations: items})
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
	// Global lock order is release -> manifest -> invitation. Hold the release
	// lock through this write/delete, not merely through a preliminary check,
	// so an acquire cannot capture a snapshot while this request is still about
	// to mutate the invitation directory.
	g.releaseMu.Lock()
	defer g.releaseMu.Unlock()
	lease, leaseErr := g.readReleaseLeaseLocked(g.currentTime())
	if leaseErr != nil {
		http.Error(w, "release deployment state unavailable", http.StatusServiceUnavailable)
		return
	}
	if lease != nil {
		http.Error(w, "invitation changes are temporarily blocked by an Opticon release deployment", http.StatusConflict)
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
		if g.now != nil {
			now = g.now()
		}
		if invite.CreatedAt.IsZero() {
			invite.CreatedAt = now.UTC()
		}
		sourceOnly := invite.InstallProtocol == sourceInstallProtocol
		legacyBootstrap := invite.InstallProtocol == ""
		if strings.TrimSpace(invite.DeviceName) == "" || (invite.Role != "ManagedOnly" && invite.Role != "ControllerAndManaged") ||
			!invite.ExpiresAt.After(now) || invite.ExpiresAt.After(now.Add(366*24*time.Hour)) || len(invite.Ciphertext) < 64 || len(invite.Ciphertext) > maxInviteBody ||
			!inviteHashPattern.MatchString(strings.ToLower(invite.SourceSHA256)) || !inviteHashPattern.MatchString(strings.ToLower(invite.SourceManifestSHA256)) ||
			invite.SourceSize <= 0 || invite.SDKVersion != pinnedSDKVersion || invite.RuntimeVersion != pinnedRuntimeVersion || !supportedTargetRuntimes(invite.TargetRuntimes) ||
			invite.SigningProfile != trustedSigningProfile || invite.SourceManifestKeyID != trustedSourceManifestKeyID ||
			invite.ProductSigner != trustedProductSignerThumbprint || len(invite.TailscaleKeyID) > 512 || strings.ContainsAny(invite.TailscaleKeyID, "\r\n") || (!sourceOnly && !legacyBootstrap) ||
			invite.CreatedAt.After(now.Add(5*time.Minute)) || invite.CreatedAt.After(invite.ExpiresAt) {
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
		g.inviteMu.Lock()
		defer g.inviteMu.Unlock()
		if existing, readErr := os.ReadFile(path); readErr == nil {
			var stored hostedInvite
			if json.Unmarshal(existing, &stored) != nil {
				http.Error(w, "stored invitation is corrupt", http.StatusInternalServerError)
				return
			}
			if !stored.CreatedAt.IsZero() {
				invite.CreatedAt = stored.CreatedAt
			}
		} else if !os.IsNotExist(readErr) {
			http.Error(w, "storage unavailable", http.StatusInternalServerError)
			return
		}
		encoded, err := json.Marshal(invite)
		if err != nil {
			http.Error(w, "invalid invitation", http.StatusBadRequest)
			return
		}
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
		if err := syncDirectory(g.inviteDir); err != nil {
			http.Error(w, "storage unavailable", http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusCreated, map[string]any{"stored": true, "expiresAt": invite.ExpiresAt})
	case http.MethodDelete:
		g.inviteMu.Lock()
		storedData, readErr := os.ReadFile(path)
		g.inviteMu.Unlock()
		if os.IsNotExist(readErr) {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		if readErr != nil || len(storedData) == 0 || len(storedData) > maxInviteBody*2 {
			http.Error(w, "stored invitation could not be read safely", http.StatusInternalServerError)
			return
		}
		var stored hostedInvite
		if json.Unmarshal(storedData, &stored) != nil {
			http.Error(w, "stored invitation is corrupt", http.StatusInternalServerError)
			return
		}
		if strings.TrimSpace(stored.TailscaleKeyID) != "" {
			if err := g.revokeHostedInviteKey(stored.TailscaleKeyID); err != nil {
				http.Error(w, "network key revocation failed; invitation was retained", http.StatusBadGateway)
				return
			}
		}
		g.inviteMu.Lock()
		err := os.Remove(path)
		if err == nil {
			err = syncDirectory(g.inviteDir)
		}
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
	page := fmt.Sprintf(`<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Opticon invitation</title><style>body{font:18px Segoe UI,sans-serif;background:#111316;color:#edf1f5;max-width:720px;margin:10vh auto;padding:28px}.download{display:inline-block;background:#52d39a;color:#08130e;text-decoration:none;padding:14px 20px;font-weight:700;font-size:17px;border-radius:6px}.disabled{pointer-events:none;opacity:.45}.muted{color:#9da7b1}code{color:#52d39a;overflow-wrap:anywhere;user-select:all}</style></head><body><h1>Install Opticon</h1><p>This private invitation is for <strong>%s</strong>.</p><p>Opticon <code>%s</code> is ready to build and install.</p><p><a id="install" class="download" href="%s">Download signed installer</a></p><p id="status" class="muted">Download the installer, then double-click it. Windows will ask for administrator approval. No ZIP extraction or invitation paste is needed.</p><p class="muted">The signed installer downloads a private source link valid for 30 minutes, then verifies the invitation, source SHA-256, signed manifest, and approved .NET 10 SDK before building.</p><p class="muted">Source SHA-256: <code>%s</code><br>Requires a stable .NET SDK matching <code>%s</code>. Invitation expires <code>%s</code>.</p><script>const key=location.hash.slice(1),install=document.getElementById('install'),status=document.getElementById('status');if(!/^[A-Za-z0-9_-]{43}$/.test(key)){install.removeAttribute('href');install.classList.add('disabled');status.textContent='This invitation link is incomplete. Ask the command center for a new link.'}else{install.download='Install-Opticon-%s--'+key+'--%s.exe'}</script></body></html>`, html.EscapeString(invite.DeviceName), html.EscapeString(source.Version), html.EscapeString(launcherPath), html.EscapeString(strings.ToLower(source.SHA256)), html.EscapeString(source.SDKVersion), invite.ExpiresAt.Local().Format(time.RFC1123), publicID, strings.ToLower(source.SourceLauncherSHA256))
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
	page := fmt.Sprintf(`<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Opticon invitation</title><style>body{font:18px Segoe UI,sans-serif;background:#111316;color:#edf1f5;max-width:720px;margin:10vh auto;padding:28px}button{background:#52d39a;color:#08130e;border:0;padding:14px 20px;font-weight:700;font-size:17px;border-radius:6px;cursor:pointer}button:disabled{cursor:wait;opacity:.65}.muted{color:#9da7b1}code{color:#52d39a;overflow-wrap:anywhere;user-select:all}</style></head><body><h1>Build and install Opticon</h1><p>This private invitation is for <strong>%s</strong>.</p><p id="status">Preparing authenticated Opticon source <code>%s</code>.</p><button id="download">Download source and signed bootstrap</button><p id="diagnostic" class="muted">Allow two downloads when your browser asks. Keep both files in the same folder.</p><p class="muted">Source SHA-256: <code>%s</code><br>Bootstrap SHA-256: <code>%s</code></p><p class="muted">Requires a stable .NET SDK matching <code>%s</code>. Expires <code>%s</code>.</p><script>const key=location.hash.slice(1),status=document.getElementById('status'),diagnostic=document.getElementById('diagnostic'),button=document.getElementById('download');let active=false;function save(blob,name){const u=URL.createObjectURL(blob),a=document.createElement('a');a.href=u;a.download=name;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(u),30000)}async function verified(url,size,hash,label){if(!globalThis.crypto||!crypto.subtle)throw new Error('Use current Microsoft Edge or Chrome; WebCrypto SHA-256 is unavailable.');const r=await fetch(url,{credentials:'omit'});if(!r.ok)throw new Error(label+' returned HTTP '+r.status+'.');const b=await r.blob();if(b.size!==size)throw new Error(label+' size is invalid.');const d=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await b.arrayBuffer()))).map(v=>v.toString(16).padStart(2,'0')).join('');if(d!==hash)throw new Error(label+' SHA-256 is invalid.');return b}async function download(){if(active)return;if(!/^[A-Za-z0-9_-]{32,128}$/.test(key)){status.textContent='This invitation link is incomplete. Ask for a new link.';button.disabled=true;return}active=true;button.disabled=true;status.textContent='Downloading and hashing source...';try{const s=await verified(%q,%d,%q,'Source archive');save(s,%q);status.textContent='Source verified. Downloading and hashing the signed bootstrap...';const b=await verified(%q,%d,%q,'Signed bootstrap');save(b,'Install-Opticon-%s--'+key+'--%s.exe');status.textContent='Both authenticated files are downloaded. Keep them together, then open the Install-Opticon executable.';diagnostic.textContent='Windows will request elevation. The signed bootstrap rechecks itself, the encrypted signed invitation, exact source archive, and RSA-PSS inner manifest before building.'}catch(e){status.textContent='The authenticated installer could not be downloaded.';diagnostic.textContent=String(e&&e.message||e).slice(0,240)+' Retry in current Microsoft Edge or Chrome; no unsigned fallback is offered.';button.disabled=false}finally{active=false}}button.addEventListener('click',download)</script></body></html>`, html.EscapeString(invite.DeviceName), html.EscapeString(source.Version), html.EscapeString(strings.ToLower(source.SHA256)), html.EscapeString(strings.ToLower(bootstrap.SHA256)), source.SDKVersion, invite.ExpiresAt.Local().Format(time.RFC1123), source.DownloadURL, source.Size, strings.ToLower(source.SHA256), source.File, bootstrap.DownloadURL, bootstrap.Size, strings.ToLower(bootstrap.SHA256), publicID, strings.ToLower(bootstrap.SHA256))
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
