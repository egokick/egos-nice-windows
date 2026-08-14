package main

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"testing"
	"time"
)

var testProductionSourceKeyID = strings.Repeat("A", 40)
var testProductionProductSigner = strings.Repeat("C", 40)

type roundTripFunc func(*http.Request) (*http.Response, error)

func (fn roundTripFunc) RoundTrip(request *http.Request) (*http.Response, error) {
	return fn(request)
}

func init() {
	trustedSourceManifestKeyID = testProductionSourceKeyID
	trustedProductSignerThumbprint = testProductionProductSigner
	trustedSigningProfile = "Production"
}

func productionArtifact(artifact bundleArtifact) bundleArtifact {
	artifact.SigningProfile = "Production"
	artifact.SourceManifestKeyID = testProductionSourceKeyID
	artifact.ProductSigner = testProductionProductSigner
	return artifact
}

func productionInvite(invite hostedInvite) hostedInvite {
	invite.SigningProfile = "Production"
	invite.SourceManifestKeyID = testProductionSourceKeyID
	invite.ProductSigner = testProductionProductSigner
	return invite
}

func TestHostedInvitationLifecycle(t *testing.T) {
	root := t.TempDir()
	inviteDir := filepath.Join(root, "invites")
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(inviteDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	bundleBytes := []byte("signed reusable bundle fixture")
	bundleHash := sha256.Sum256(bundleBytes)
	bundle := productionArtifact(bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.0.0-managed-win-x64.zip", Size: int64(len(bundleBytes)), SHA256: hex.EncodeToString(bundleHash[:]), DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.0.0/opticon-bundle-1.0.0-managed-win-x64.zip"})
	bootstrapBytes := bytes.Repeat([]byte{0x7a}, 20)
	bootstrapHash := sha256.Sum256(bootstrapBytes)
	bootstrap := productionArtifact(bundleArtifact{Product: "OpticonBootstrap", Version: "1.0.0", Architecture: "x64", File: "opticon-bootstrap-1.0.0.exe", Size: int64(len(bootstrapBytes)), SHA256: hex.EncodeToString(bootstrapHash[:]), SignerThumbprint: strings.Repeat("C", 40), DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.0.0/opticon-bootstrap-1.0.0.exe"})
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{bundle, bootstrap}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(bundleDir, bundle.File), bundleBytes, 0444); err != nil {
		t.Fatal(err)
	}

	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, inviteDir: inviteDir, artifactDir: artifactDir, bundleDir: bundleDir, publicOrigin: "https://opticon.example.test", nonces: make(map[string]time.Time)}
	publicID := strings.Repeat("A", 32)
	idHash := sha256.Sum256([]byte(publicID))
	ciphertext := bytes.Repeat([]byte{0x5a}, 96)
	upload, _ := json.Marshal(productionInvite(hostedInvite{DeviceName: "Mom & Dad PC", Role: "ManagedOnly", ExpiresAt: time.Now().Add(14 * 24 * time.Hour), InstallProtocol: binaryInstallProtocol, ReleaseVersion: bundle.Version, BundleFile: bundle.File, BundleSize: bundle.Size, BundleSHA256: bundle.SHA256, BundleArchitecture: bundle.Architecture, BundleDownloadURL: bundle.DownloadURL, BootstrapVersion: bootstrap.Version, BootstrapFile: bootstrap.File, BootstrapSize: bootstrap.Size, BootstrapSHA256: bootstrap.SHA256, BootstrapSigner: bootstrap.SignerThumbprint, Ciphertext: ciphertext}))
	adminPath := inviteAdminPrefix + hex.EncodeToString(idHash[:])
	put := signedRouteRequest(secret, http.MethodPut, adminPath, "put-nonce-012345678901234", upload)
	putResult := httptest.NewRecorder()
	g.ServeHTTP(putResult, put)
	if putResult.Code != http.StatusCreated {
		t.Fatalf("PUT returned %d: %s", putResult.Code, putResult.Body.String())
	}

	landingResult := httptest.NewRecorder()
	g.ServeHTTP(landingResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID, nil))
	if landingResult.Code != http.StatusOK {
		t.Fatalf("landing returned %d", landingResult.Code)
	}
	landing := landingResult.Body.String()
	if !strings.Contains(landing, "Mom &amp; Dad PC") || strings.Contains(landing, bundle.DownloadURL) ||
		strings.Contains(landing, bootstrap.DownloadURL) || strings.Contains(landing, "fetch(") ||
		strings.Contains(landing, "URL.createObjectURL") || strings.Contains(landing, "crypto.subtle") ||
		!strings.Contains(landing, `href="/opticon/i/`+publicID+`/bootstrap"`) ||
		!strings.Contains(landing, "download.download='Install-Opticon-"+publicID+"--'+key+'--"+bootstrap.SHA256+".exe'") ||
		strings.Contains(landing, "ExecutionPolicy") {
		t.Fatal("landing page did not offer exactly one pinned signed installer safely")
	}
	if !strings.Contains(landingResult.Header().Get("Content-Security-Policy"), "connect-src 'self'") ||
		!strings.Contains(landing, "/opticon/i/"+publicID+"/bootstrap") {
		t.Fatal("landing page did not use the same-origin signed-bootstrap relay")
	}
	if strings.Contains(landing, "private-fragment-test") {
		t.Fatal("landing page leaked a fragment key")
	}

	bundleResult := httptest.NewRecorder()
	g.ServeHTTP(bundleResult, httptest.NewRequest(http.MethodGet, artifactPrefix+bundle.File, nil))
	if bundleResult.Code != http.StatusOK || !bytes.Equal(bundleResult.Body.Bytes(), bundleBytes) {
		t.Fatal("landing page bundle was not downloadable from finalized storage")
	}
	downloadResult := httptest.NewRecorder()
	g.ServeHTTP(downloadResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID+"/invite.tdinvite", nil))
	if downloadResult.Code != http.StatusOK || !bytes.Equal(downloadResult.Body.Bytes(), ciphertext) {
		t.Fatal("encrypted invitation download changed")
	}
	g.downloadClient = &http.Client{Transport: roundTripFunc(func(request *http.Request) (*http.Response, error) {
		if request.URL.String() != bootstrap.DownloadURL {
			t.Fatalf("bootstrap relay requested the wrong artifact: %s", request.URL)
		}
		return &http.Response{
			StatusCode:    http.StatusOK,
			ContentLength: bootstrap.Size,
			Header:        http.Header{"X-Amz-Meta-Sha256": []string{bootstrap.SHA256}},
			Body:          io.NopCloser(bytes.NewReader(bootstrapBytes)),
		}, nil
	})}
	bootstrapResult := httptest.NewRecorder()
	g.ServeHTTP(bootstrapResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID+"/bootstrap", nil))
	if bootstrapResult.Code != http.StatusOK || !bytes.Equal(bootstrapResult.Body.Bytes(), bootstrapBytes) ||
		bootstrapResult.Header().Get("Content-Type") != "application/vnd.microsoft.portable-executable" ||
		bootstrapResult.Header().Get("Content-Length") != strconv.FormatInt(bootstrap.Size, 10) ||
		bootstrapResult.Header().Get("Access-Control-Allow-Origin") != "" ||
		bootstrapResult.Header().Get("Content-Disposition") != "" {
		t.Fatal("same-origin bootstrap relay did not return the exact declared installer")
	}

	deleteRequest := signedRouteRequest(secret, http.MethodDelete, adminPath, "delete-nonce-0123456789012", nil)
	deleteResult := httptest.NewRecorder()
	g.ServeHTTP(deleteResult, deleteRequest)
	if deleteResult.Code != http.StatusNoContent {
		t.Fatalf("DELETE returned %d", deleteResult.Code)
	}
	missingResult := httptest.NewRecorder()
	g.ServeHTTP(missingResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID, nil))
	if missingResult.Code != http.StatusNotFound {
		t.Fatal("deleted invitation remained public")
	}
}

func TestPinnedGatewayDependenciesRemainAvailableForSourceOnlyManifest(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}

	hash := strings.Repeat("b", 64)
	source := productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.0", Architecture: "source",
		File: "opticon-source-1.2.0.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.0/opticon-source-1.2.0.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
	})
	manifest, err := json.Marshal(artifactManifest{SchemaVersion: sourceOnlyManifestSchema, Artifacts: []bundleArtifact{source}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}

	for name, size := range pinnedGatewayDependencies {
		path := filepath.Join(artifactDir, name)
		file, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY, 0600)
		if err != nil {
			t.Fatal(err)
		}
		if err := file.Truncate(size); err != nil {
			file.Close()
			t.Fatal(err)
		}
		if err := file.Close(); err != nil {
			t.Fatal(err)
		}
	}

	g := &gateway{artifactDir: artifactDir}
	for name, size := range pinnedGatewayDependencies {
		result := httptest.NewRecorder()
		g.ServeHTTP(result, httptest.NewRequest(http.MethodHead, artifactPrefix+name, nil))
		if result.Code != http.StatusOK || result.Header().Get("Content-Length") != strconv.FormatInt(size, 10) {
			t.Fatalf("pinned dependency %s was unavailable or had the wrong length: status=%d length=%q", name, result.Code, result.Header().Get("Content-Length"))
		}
	}

	unknown := httptest.NewRecorder()
	g.ServeHTTP(unknown, httptest.NewRequest(http.MethodGet, artifactPrefix+"unexpected.msi", nil))
	if unknown.Code != http.StatusNotFound {
		t.Fatalf("undeclared dependency was served with status %d", unknown.Code)
	}
}

func TestBundleForRoleSelectsHighestSemanticVersion(t *testing.T) {
	root := t.TempDir()
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("a", 64)
	prior := bundleArtifact{Product: "OpticonBundle", Version: "1.9.9", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.9.9-managed-win-x64.zip", Size: 10, SHA256: hash}
	current := bundleArtifact{Product: "OpticonBundle", Version: "1.10.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.10.0-managed-win-x64.zip", Size: 10, SHA256: hash}
	pending := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-2.0.0-managed-win-x64.zip", Size: 10, SHA256: hash}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		prior, current, pending,
		{Product: "OpticonBundle", Version: "99.0.0", Role: "ManagedOnly", Architecture: "arm64", File: "opticon-bundle-99.0.0-managed-win-arm64.zip", Size: 10, SHA256: hash},
	}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	for _, artifact := range []bundleArtifact{prior, current} {
		if err := os.WriteFile(filepath.Join(bundleDir, artifact.File), bytes.Repeat([]byte{'x'}, 10), 0444); err != nil {
			t.Fatal(err)
		}
	}
	selected, err := (&gateway{artifactDir: root, bundleDir: bundleDir}).bundleForRole("ManagedOnly")
	if err != nil {
		t.Fatal(err)
	}
	if selected.File != current.File || selected.Version != current.Version {
		t.Fatalf("selected %s %s instead of highest finalized stable semantic release", selected.File, selected.Version)
	}
}

func TestValidBundleArtifactRequiresStableSupportedVersion(t *testing.T) {
	base := bundleArtifact{
		Product: "OpticonBundle", Role: "ManagedOnly", Architecture: "x64",
		File: "opticon-bundle-test-managed-win-x64.zip", Size: 1024,
		SHA256: strings.Repeat("a", 64),
	}
	cases := []struct {
		version string
		valid   bool
	}{
		{version: "1.0.0", valid: true},
		{version: "10.200.300", valid: true},
		{version: "0.9.0"},
		{version: "1.0.0-rc.1"},
		{version: "1.0.0+build.1"},
		{version: "1.0"},
		{version: "01.0.0"},
	}
	for _, item := range cases {
		artifact := base
		artifact.Version = item.version
		if actual := validBundleArtifact(artifact); actual != item.valid {
			t.Fatalf("stable release validation for %q returned %v", item.version, actual)
		}
	}
}

func TestLegacyMachineStateMigrationBridgeMarkerIsPreservedButNeverNormalSelection(t *testing.T) {
	previousProfile := trustedSigningProfile
	trustedSigningProfile = "OwnerManaged"
	defer func() { trustedSigningProfile = previousProfile }()

	bridge := productionArtifact(bundleArtifact{
		Product: "OpticonBundle", Version: legacyMachineStateMigrationBridgeVersion,
		Role: "ManagedOnly", Architecture: "x64",
		File: "opticon-bundle-1.1.41-legacy-bridge-managed-win-x64.zip", Size: 1024,
		SHA256:      strings.Repeat("a", 64),
		DownloadURL: "https://d111111abcdef8.cloudfront.net/opticon/releases/1.1.41/opticon-bundle-1.1.41-legacy-bridge-managed-win-x64.zip",
	})
	bridge.SigningProfile = "OwnerManaged"
	bridge.LegacyMigrationSignerThumbprint = invitationSigningKeyID
	if !validBundleArtifact(bridge) {
		t.Fatal("the exact trusted legacy bridge was rejected")
	}

	root := t.TempDir()
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{bridge}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	g := &gateway{artifactDir: root, bundleDir: t.TempDir()}
	result := httptest.NewRecorder()
	g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if result.Code != http.StatusOK {
		t.Fatalf("bridge manifest returned %d: %s", result.Code, result.Body.String())
	}
	var public artifactManifest
	if err := json.Unmarshal(result.Body.Bytes(), &public); err != nil {
		t.Fatal(err)
	}
	if len(public.Artifacts) != 1 || public.Artifacts[0].LegacyMigrationSignerThumbprint != invitationSigningKeyID {
		t.Fatal("public manifest did not preserve the exact legacy migration marker")
	}
	if _, err := g.bundleForRole("ManagedOnly"); err == nil {
		t.Fatal("normal source-build selection accepted a legacy migration bridge")
	}
}

func TestLegacyMachineStateMigrationBridgeRejectsForgedOuterMarker(t *testing.T) {
	previousProfile := trustedSigningProfile
	trustedSigningProfile = "OwnerManaged"
	defer func() { trustedSigningProfile = previousProfile }()

	bridge := productionArtifact(bundleArtifact{
		Product: "OpticonBundle", Version: legacyMachineStateMigrationBridgeVersion,
		Role: "ManagedOnly", Architecture: "x64",
		File: "opticon-bundle-1.1.41-legacy-bridge-managed-win-x64.zip", Size: 1024,
		SHA256: strings.Repeat("a", 64),
	})
	bridge.SigningProfile = "OwnerManaged"
	bridge.LegacyMigrationSignerThumbprint = invitationSigningKeyID

	for name, mutate := range map[string]func(*bundleArtifact){
		"wrong signer": func(value *bundleArtifact) {
			value.LegacyMigrationSignerThumbprint = strings.ToLower(invitationSigningKeyID)
		},
		"wrong target":            func(value *bundleArtifact) { value.Version = "1.1.40" },
		"wrong profile":           func(value *bundleArtifact) { value.SigningProfile = "Production" },
		"confused source key":     func(value *bundleArtifact) { value.SourceManifestKeyID = invitationSigningKeyID },
		"confused product signer": func(value *bundleArtifact) { value.ProductSigner = invitationSigningKeyID },
	} {
		t.Run(name, func(t *testing.T) {
			candidate := bridge
			mutate(&candidate)
			if validBundleArtifact(candidate) {
				t.Fatal("forged legacy migration metadata was accepted")
			}
			if err := validateArtifactManifest(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{candidate}}); err == nil {
				t.Fatal("forged legacy migration manifest was accepted")
			}
		})
	}
}

func TestCloudFrontBundleURLIsStrictAndDoesNotNeedFlyVolume(t *testing.T) {
	artifact := bundleArtifact{
		Product: "OpticonBundle", Version: "1.2.3", Role: "ManagedOnly", Architecture: "x64",
		File: "opticon-bundle-1.2.3-managed-win-x64.zip", Size: 1024, SHA256: strings.Repeat("a", 64),
		DownloadURL: "https://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
	}
	if !validCloudFrontDownloadURL(artifact) {
		t.Fatal("valid immutable CloudFront URL was rejected")
	}
	g := &gateway{bundleDir: t.TempDir()}
	if !g.bundleIsAvailable(artifact) {
		t.Fatal("CloudFront artifact incorrectly required a Fly-volume copy")
	}
	for _, unsafe := range []string{
		"http://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
		"https://user:secret@d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
		"https://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/other.zip",
		"https://evil.example.test/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
		"https://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip#fragment",
	} {
		candidate := artifact
		candidate.DownloadURL = unsafe
		if validCloudFrontDownloadURL(candidate) {
			t.Fatalf("unsafe URL accepted: %s", unsafe)
		}
	}
}

func TestBundleForRoleRejectsAmbiguousEquivalentRelease(t *testing.T) {
	root := t.TempDir()
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("b", 64)
	first := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-2.0.0-first-managed-win-x64.zip", Size: 10, SHA256: hash}
	second := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-2.0.0-second-managed-win-x64.zip", Size: 10, SHA256: hash}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{first, second}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	for _, artifact := range []bundleArtifact{first, second} {
		if err := os.WriteFile(filepath.Join(bundleDir, artifact.File), bytes.Repeat([]byte{'x'}, 10), 0444); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := (&gateway{artifactDir: root, bundleDir: bundleDir}).bundleForRole("ManagedOnly"); err == nil {
		t.Fatal("precedence-equivalent conflicting releases were accepted")
	}
}

func TestDuplicateBundleFilenameFailsBeforeServingSelectionOrUpload(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	payload := []byte("duplicate bundle fixture")
	digest := sha256.Sum256(payload)
	fileName := "opticon-bundle-duplicate-win-x64.zip"
	first := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: fileName, Size: int64(len(payload)), SHA256: hex.EncodeToString(digest[:])}
	second := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ControllerAndManaged", Architecture: "x64", File: fileName, Size: int64(len(payload)), SHA256: hex.EncodeToString(digest[:])}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{first, second}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(bundleDir, fileName), payload, 0444); err != nil {
		t.Fatal(err)
	}
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, artifactDir: artifactDir, bundleDir: bundleDir, nonces: make(map[string]time.Time)}
	if _, err := g.readArtifactManifest(); err == nil {
		t.Fatal("duplicate bundle filename passed central manifest validation")
	}
	if _, err := g.bundleForRole("ManagedOnly"); err == nil {
		t.Fatal("duplicate bundle filename reached role selection")
	}
	if _, err := g.bundleByFile(fileName); err == nil {
		t.Fatal("duplicate bundle filename reached file selection")
	}

	public := httptest.NewRecorder()
	g.ServeHTTP(public, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if public.Code != http.StatusServiceUnavailable {
		t.Fatalf("duplicate manifest was served with status %d", public.Code)
	}
	uploadPath := bundleAdminPrefix + fileName + "?offset=0&total=" + strconv.FormatInt(first.Size, 10) + "&sha256=" + first.SHA256
	upload := signedRouteRequest(secret, http.MethodPut, uploadPath, "duplicate-upload-nonce-0123", payload)
	uploadResult := httptest.NewRecorder()
	g.ServeHTTP(uploadResult, upload)
	if uploadResult.Code != http.StatusNotFound {
		t.Fatalf("duplicate manifest authorized upload with status %d", uploadResult.Code)
	}
	if _, err := os.Stat(filepath.Join(bundleDir, fileName+".upload")); !os.IsNotExist(err) {
		t.Fatal("duplicate manifest created an upload staging file")
	}
}

func TestBundleUploadRequiresHMACAndManifestPins(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	payload := []byte("a signed reusable Opticon bundle")
	digest := sha256.Sum256(payload)
	artifact := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-test.zip", Size: int64(len(payload)), SHA256: hex.EncodeToString(digest[:])}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{artifact}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, artifactDir: artifactDir, bundleDir: bundleDir, nonces: make(map[string]time.Time)}
	path := bundleAdminPrefix + artifact.File + "?offset=0&total=" + strconv.FormatInt(artifact.Size, 10) + "&sha256=" + artifact.SHA256
	unsignedResult := httptest.NewRecorder()
	g.ServeHTTP(unsignedResult, httptest.NewRequest(http.MethodPut, path, bytes.NewReader(payload)))
	if unsignedResult.Code != http.StatusUnauthorized {
		t.Fatal("unsigned bundle upload was accepted")
	}
	signed := signedRouteRequest(secret, http.MethodPut, path, "bundle-nonce-0123456789012", payload)
	result := httptest.NewRecorder()
	g.ServeHTTP(result, signed)
	if result.Code != http.StatusCreated {
		t.Fatalf("bundle upload returned %d: %s", result.Code, result.Body.String())
	}
	stored, err := os.ReadFile(filepath.Join(bundleDir, artifact.File))
	if err != nil || !bytes.Equal(stored, payload) {
		t.Fatal("stored bundle differs from hash-pinned upload")
	}
	declaredDelete := signedRouteRequest(secret, http.MethodDelete, bundleAdminPrefix+artifact.File, "declared-delete-nonce-012345", nil)
	declaredResult := httptest.NewRecorder()
	g.ServeHTTP(declaredResult, declaredDelete)
	if declaredResult.Code != http.StatusConflict {
		t.Fatal("declared bundle deletion was accepted")
	}
	partialPath := filepath.Join(bundleDir, artifact.File+".upload")
	if err := os.WriteFile(partialPath, []byte("partial"), 0600); err != nil {
		t.Fatal(err)
	}
	resetUpload := signedRouteRequest(secret, http.MethodDelete, bundleAdminPrefix+artifact.File+"?upload=true", "upload-reset-nonce-012345", nil)
	resetResult := httptest.NewRecorder()
	g.ServeHTTP(resetResult, resetUpload)
	if resetResult.Code != http.StatusNoContent {
		t.Fatalf("partial upload reset returned %d", resetResult.Code)
	}
	if _, err := os.Stat(partialPath); !os.IsNotExist(err) {
		t.Fatal("partial upload remained on disk")
	}
	if stored, err := os.ReadFile(filepath.Join(bundleDir, artifact.File)); err != nil || !bytes.Equal(stored, payload) {
		t.Fatal("partial upload reset damaged final bundle")
	}
	manifest, _ = json.Marshal(artifactManifest{})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	obsoleteDelete := signedRouteRequest(secret, http.MethodDelete, bundleAdminPrefix+artifact.File, "obsolete-delete-nonce-01234", nil)
	obsoleteResult := httptest.NewRecorder()
	g.ServeHTTP(obsoleteResult, obsoleteDelete)
	if obsoleteResult.Code != http.StatusNoContent {
		t.Fatalf("obsolete bundle deletion returned %d", obsoleteResult.Code)
	}
	if _, err := os.Stat(filepath.Join(bundleDir, artifact.File)); !os.IsNotExist(err) {
		t.Fatal("obsolete bundle remained on disk")
	}
}

func TestPruneUndeclaredBundlesPreservesCurrentArtifacts(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	current := "opticon-bundle-1.0.0-managed-win-x64.zip"
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: current, Size: int64(len(current)), SHA256: strings.Repeat("a", 64)}}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{current, current + ".upload", "opticon-bundle-old-managed-win-x64.zip", "opticon-bundle-old-controller-win-x64.zip.upload", "unrelated.data"} {
		if err := os.WriteFile(filepath.Join(bundleDir, name), []byte(name), 0600); err != nil {
			t.Fatal(err)
		}
	}
	g := &gateway{artifactDir: artifactDir, bundleDir: bundleDir}
	if err := g.pruneUndeclaredBundles(); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{current, current + ".upload", "unrelated.data"} {
		if _, err := os.Stat(filepath.Join(bundleDir, name)); err != nil {
			t.Fatalf("current or unrelated file %q was removed: %v", name, err)
		}
	}
	for _, name := range []string{"opticon-bundle-old-managed-win-x64.zip", "opticon-bundle-old-controller-win-x64.zip.upload"} {
		if _, err := os.Stat(filepath.Join(bundleDir, name)); !os.IsNotExist(err) {
			t.Fatalf("obsolete bundle %q was not removed", name)
		}
	}
}

func TestPruneRejectsUnknownManifestSchemaBeforeDeleting(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	name := "opticon-bundle-current-managed-win-x64.zip"
	path := filepath.Join(bundleDir, name)
	if err := os.WriteFile(path, []byte("preserve me"), 0600); err != nil {
		t.Fatal(err)
	}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 2})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	if err := (&gateway{artifactDir: artifactDir, bundleDir: bundleDir}).pruneUndeclaredBundles(); err == nil {
		t.Fatal("unknown manifest schema was accepted for destructive pruning")
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("bundle was deleted before manifest validation: %v", err)
	}
}
func TestArtifactRejectsUndeclaredOrWrongSizedBundle(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	name := "opticon-bundle-stale.zip"
	if err := os.WriteFile(filepath.Join(bundleDir, name), []byte("stale"), 0600); err != nil {
		t.Fatal(err)
	}
	manifest, _ := json.Marshal(artifactManifest{})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	g := &gateway{artifactDir: artifactDir, bundleDir: bundleDir}
	result := httptest.NewRecorder()
	g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+name, nil))
	if result.Code != http.StatusNotFound {
		t.Fatal("undeclared stale bundle remained downloadable")
	}

	digest := sha256.Sum256([]byte("stale"))
	declared := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: name, Size: 999, SHA256: hex.EncodeToString(digest[:])}
	manifest, _ = json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{declared}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	result = httptest.NewRecorder()
	g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+name, nil))
	if result.Code != http.StatusNotFound {
		t.Fatal("wrong-sized bundle remained downloadable")
	}
}

func TestPublicManifestOnlyAdvertisesFinalizedBundlesAndPreservesPins(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("c", 64)
	dependency := bundleArtifact{Product: "Tailscale", Version: "1.2.3", Architecture: "x64", File: "tailscale.msi", Size: 50, SHA256: hash, SignerThumbprint: "PINNED-SIGNER"}
	ready := bundleArtifact{Product: "OpticonBundle", Version: "1.1.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.1.0-managed-win-x64.zip", Size: 16, SHA256: hash}
	pending := bundleArtifact{Product: "OpticonBundle", Version: "1.2.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.2.0-managed-win-x64.zip", Size: 16, SHA256: hash}
	remote := bundleArtifact{Product: "OpticonBundle", Version: "1.3.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.3.0-managed-win-x64.zip", Size: 16, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.3.0/opticon-bundle-1.3.0-managed-win-x64.zip"}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{dependency, ready, pending, remote}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(bundleDir, ready.File), bytes.Repeat([]byte{'r'}, 16), 0444); err != nil {
		t.Fatal(err)
	}
	g := &gateway{artifactDir: artifactDir, bundleDir: bundleDir}

	get := httptest.NewRecorder()
	g.ServeHTTP(get, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if get.Code != http.StatusOK {
		t.Fatalf("manifest returned %d: %s", get.Code, get.Body.String())
	}
	if get.Header().Get("Cache-Control") != "no-store, max-age=0" {
		t.Fatal("public manifest was cacheable")
	}
	var published artifactManifest
	if err := json.Unmarshal(get.Body.Bytes(), &published); err != nil {
		t.Fatal(err)
	}
	if published.SchemaVersion != 1 {
		t.Fatalf("schema version changed to %d", published.SchemaVersion)
	}
	if len(published.Artifacts) != 3 {
		t.Fatalf("published %d records instead of dependency plus local and CloudFront bundles", len(published.Artifacts))
	}
	if published.Artifacts[0].SignerThumbprint != dependency.SignerThumbprint {
		t.Fatal("dependency signer pin was stripped")
	}
	if published.Artifacts[1].File != ready.File {
		t.Fatalf("unexpected bundle was published: %s", published.Artifacts[1].File)
	}
	if published.Artifacts[2].File != remote.File {
		t.Fatalf("immutable CloudFront bundle was hidden: %s", published.Artifacts[2].File)
	}
	if strings.Contains(get.Body.String(), pending.File) {
		t.Fatal("not-yet-finalized bundle was advertised")
	}

	head := httptest.NewRecorder()
	g.ServeHTTP(head, httptest.NewRequest(http.MethodHead, artifactPrefix+"manifest.json", nil))
	if head.Code != http.StatusOK || head.Body.Len() != 0 || head.Header().Get("Content-Length") != get.Header().Get("Content-Length") {
		t.Fatal("manifest HEAD response did not preserve GET metadata without a body")
	}
	if err := os.WriteFile(filepath.Join(bundleDir, pending.File), bytes.Repeat([]byte{'p'}, 15), 0600); err != nil {
		t.Fatal(err)
	}
	wrongSize := httptest.NewRecorder()
	g.ServeHTTP(wrongSize, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if strings.Contains(wrongSize.Body.String(), pending.File) {
		t.Fatal("wrong-sized bundle was advertised")
	}
	if err := os.WriteFile(filepath.Join(bundleDir, pending.File), bytes.Repeat([]byte{'p'}, 16), 0444); err != nil {
		t.Fatal(err)
	}
	finalized := httptest.NewRecorder()
	g.ServeHTTP(finalized, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if !strings.Contains(finalized.Body.String(), pending.File) {
		t.Fatal("finalized bundle did not become visible")
	}
}

func TestAuthenticatedManifestPublicationIsAtomicPersistentAndReplaySafe(t *testing.T) {
	root := t.TempDir()
	manifestPath := filepath.Join(root, "release", "manifest.json")
	if err := os.MkdirAll(filepath.Dir(manifestPath), 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("a", 64)
	old := artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		{Product: "OpticonBundle", Version: "1.1.17", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.1.17-managed-win-x64.zip", Size: 10, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.17/opticon-bundle-1.1.17-managed-win-x64.zip"},
		{Product: "OpticonBundle", Version: "1.1.17", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-1.1.17-controller-win-x64.zip", Size: 20, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.17/opticon-bundle-1.1.17-controller-win-x64.zip"},
	}}
	oldBytes, _ := json.Marshal(old)
	if err := os.WriteFile(manifestPath, oldBytes, 0600); err != nil {
		t.Fatal(err)
	}
	next := artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		{Product: "OpticonBundle", Version: "1.1.18", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.1.18-managed-win-x64.zip", Size: 11, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.18/opticon-bundle-1.1.18-managed-win-x64.zip"},
		{Product: "OpticonBundle", Version: "1.1.18", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-1.1.18-controller-win-x64.zip", Size: 21, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.18/opticon-bundle-1.1.18-controller-win-x64.zip"},
		productionArtifact(bundleArtifact{Product: "OpticonBootstrap", Version: "1.1.18", Architecture: "x64", File: "opticon-bootstrap-1.1.18.exe", Size: 7, SHA256: hash, SignerThumbprint: strings.Repeat("C", 40), DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.18/opticon-bootstrap-1.1.18.exe"}),
	}}
	body, _ := json.Marshal(next)
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{artifactDir: root, manifestPath: manifestPath, adminSecret: secret, nonces: make(map[string]time.Time)}
	request := signedRouteRequest(secret, http.MethodPut, releaseAdminPath, "manifest-nonce-012345678901", body)
	result := httptest.NewRecorder()
	g.ServeHTTP(result, request)
	if result.Code != http.StatusCreated {
		t.Fatalf("manifest publish returned %d: %s", result.Code, result.Body.String())
	}
	published, err := g.readArtifactManifest()
	if err != nil {
		t.Fatal(err)
	}
	if version, ok := highestBundleVersion(published); !ok || version != "1.1.18" {
		t.Fatalf("published version is %q", version)
	}
	replay := httptest.NewRecorder()
	g.ServeHTTP(replay, request)
	if replay.Code != http.StatusUnauthorized {
		t.Fatalf("replayed manifest publish returned %d", replay.Code)
	}
	next.Artifacts[0].Size++
	changedBody, _ := json.Marshal(next)
	changed := httptest.NewRecorder()
	g.ServeHTTP(changed, signedRouteRequest(secret, http.MethodPut, releaseAdminPath, "changed-release-nonce-0123", changedBody))
	if changed.Code != http.StatusCreated {
		t.Fatalf("same-version byte change returned %d", changed.Code)
	}
	republished, err := g.readArtifactManifest()
	if err != nil {
		t.Fatal(err)
	}
	if republished.Artifacts[0].Size != next.Artifacts[0].Size {
		t.Fatal("same-version forced release did not replace the manifest")
	}
}

func TestSourceOnlyManifestHasOneSignedArchivePerRelease(t *testing.T) {
	hash := strings.Repeat("a", 64)
	source := productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.1", Architecture: "source",
		File: "opticon-source-1.2.1.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.1/opticon-source-1.2.1.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
		SourceLauncherFile: "opticon-source-launcher-1.2.1.exe", SourceLauncherSize: 1024, SourceLauncherSHA256: hash,
	})
	manifest := artifactManifest{SchemaVersion: sourceOnlyManifestSchema, Artifacts: []bundleArtifact{source}}
	if err := validateArtifactManifest(manifest); err != nil {
		t.Fatalf("valid source-only manifest was rejected: %v", err)
	}
	if version, ok := highestReleaseVersion(manifest); !ok || version != source.Version {
		t.Fatalf("source-only release version is %q", version)
	}
	if !completeCloudFrontRelease(manifest, source.Version) {
		t.Fatal("complete source-only CloudFront release was rejected")
	}

	duplicate := manifest
	duplicate.Artifacts = append(duplicate.Artifacts, source)
	duplicate.Artifacts[1].File = "opticon-source-1.2.1-copy.zip"
	duplicate.Artifacts[1].DownloadURL = "https://d111.cloudfront.net/opticon/releases/1.2.1/opticon-source-1.2.1-copy.zip"
	if err := validateArtifactManifest(duplicate); err == nil {
		t.Fatal("source-only manifest accepted two archives for the same version")
	}
	withBundle := manifest
	withBundle.Artifacts = append(withBundle.Artifacts, bundleArtifact{
		Product: "OpticonBundle", Version: "1.2.1", Role: "ManagedOnly", Architecture: "x64",
		File: "opticon-bundle-1.2.1-managed-win-x64.zip", Size: 2048, SHA256: hash,
	})
	if err := validateArtifactManifest(withBundle); err == nil {
		t.Fatal("source-only manifest accepted a binary bundle")
	}
	missingLauncher := manifest
	missingLauncher.Artifacts = append([]bundleArtifact(nil), manifest.Artifacts...)
	missingLauncher.Artifacts[0].SourceLauncherSHA256 = ""
	if err := validateArtifactManifest(missingLauncher); err == nil {
		t.Fatal("source-only 1.2.1 manifest accepted missing one-click launcher metadata")
	}
	invalidPolicy := manifest
	invalidPolicy.Artifacts = append([]bundleArtifact(nil), manifest.Artifacts...)
	invalidPolicy.Artifacts[0].ClientInstallValidation.DisabledSteps = []string{"ProtectedPaths", "NotAValidation"}
	if err := validateArtifactManifest(invalidPolicy); err == nil {
		t.Fatal("source-only manifest accepted an unknown client validation step")
	}
	duplicatePolicy := manifest
	duplicatePolicy.Artifacts = append([]bundleArtifact(nil), manifest.Artifacts...)
	duplicatePolicy.Artifacts[0].ClientInstallValidation.DisabledSteps = []string{"ProtectedPaths", "ProtectedPaths"}
	if err := validateArtifactManifest(duplicatePolicy); err == nil {
		t.Fatal("source-only manifest accepted duplicate client validation steps")
	}
}

func TestInstallAndUpdateManifestChannelsCoexistAndRejectCrossPublication(t *testing.T) {
	root := t.TempDir()
	installPath := filepath.Join(root, "release", "manifest.json")
	updatePath := filepath.Join(root, "release", "update-manifest.json")
	if err := os.MkdirAll(filepath.Dir(installPath), 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("a", 64)
	install := artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		productionArtifact(bundleArtifact{Product: "OpticonBundle", Version: "1.2.1", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.2.1-managed-win-x64.zip", Size: 2048, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.1/opticon-bundle-1.2.1-managed-win-x64.zip"}),
		productionArtifact(bundleArtifact{Product: "OpticonBundle", Version: "1.2.1", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-1.2.1-controller-win-x64.zip", Size: 2048, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.1/opticon-bundle-1.2.1-controller-win-x64.zip"}),
		productionArtifact(bundleArtifact{Product: "OpticonBootstrap", Version: "1.2.1", Architecture: "x64", File: "opticon-bootstrap-1.2.1.exe", Size: 2048, SHA256: hash, SignerThumbprint: testProductionProductSigner, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.1/opticon-bootstrap-1.2.1.exe"}),
	}}
	update := artifactManifest{SchemaVersion: sourceOnlyManifestSchema, Artifacts: []bundleArtifact{productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.1", Architecture: "source",
		File: "opticon-source-1.2.1.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.1/opticon-source-1.2.1.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
		SourceLauncherFile: "opticon-source-launcher-1.2.1.exe", SourceLauncherSize: 1024, SourceLauncherSHA256: hash,
	})}}
	installBytes, _ := json.Marshal(install)
	updateBytes, _ := json.Marshal(update)
	if err := os.WriteFile(installPath, installBytes, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(updatePath, updateBytes, 0600); err != nil {
		t.Fatal(err)
	}
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{artifactDir: root, manifestPath: installPath, updateManifestPath: updatePath, adminSecret: secret, nonces: make(map[string]time.Time)}

	for _, test := range []struct {
		path   string
		schema int
	}{
		{path: artifactPrefix + "manifest.json", schema: 1},
		{path: artifactPrefix + "update-manifest.json", schema: sourceOnlyManifestSchema},
	} {
		result := httptest.NewRecorder()
		g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, test.path, nil))
		var published artifactManifest
		if result.Code != http.StatusOK || json.Unmarshal(result.Body.Bytes(), &published) != nil || published.SchemaVersion != test.schema {
			t.Fatalf("public channel %s did not serve schema %d: %d %s", test.path, test.schema, result.Code, result.Body.String())
		}
	}

	wrongInstall := httptest.NewRecorder()
	g.ServeHTTP(wrongInstall, signedRouteRequest(secret, http.MethodPut, releaseAdminPath, "wrong-install-channel-nonce-01", updateBytes))
	if wrongInstall.Code != http.StatusBadRequest {
		t.Fatalf("install channel accepted schema 2: %d", wrongInstall.Code)
	}
	wrongUpdate := httptest.NewRecorder()
	g.ServeHTTP(wrongUpdate, signedRouteRequest(secret, http.MethodPut, updateReleaseAdminPath, "wrong-update-channel-nonce-001", installBytes))
	if wrongUpdate.Code != http.StatusBadRequest {
		t.Fatalf("update channel accepted schema 1: %d", wrongUpdate.Code)
	}
}

func TestForcedSameVersionSourcePublicationProceedsWithActiveInvitationAndNeedsNoLease(t *testing.T) {
	root := t.TempDir()
	manifestPath := filepath.Join(root, "release", "update-manifest.json")
	inviteDir := filepath.Join(root, "invites")
	if err := os.MkdirAll(filepath.Dir(manifestPath), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(inviteDir, 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("a", 64)
	currentSource := productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.1", Architecture: "source",
		File: "opticon-source-1.2.1.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.1/opticon-source-1.2.1.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
		SourceLauncherFile: "opticon-source-launcher-1.2.1.exe", SourceLauncherSize: 1024, SourceLauncherSHA256: hash,
	})
	current := artifactManifest{SchemaVersion: sourceOnlyManifestSchema, Artifacts: []bundleArtifact{currentSource}}
	currentBytes, _ := json.Marshal(current)
	if err := os.WriteFile(manifestPath, currentBytes, 0600); err != nil {
		t.Fatal(err)
	}
	activeInvite := productionInvite(hostedInvite{
		DeviceName: "Invitation created before redeploy", Role: "ManagedOnly", ExpiresAt: time.Now().Add(time.Hour),
		InstallProtocol: sourceInstallProtocol, ReleaseVersion: currentSource.Version,
		SourceFile: currentSource.File, SourceSize: currentSource.Size, SourceSHA256: currentSource.SHA256,
		SourceManifestSHA256: currentSource.SourceManifestSHA256, SDKVersion: currentSource.SDKVersion,
		RuntimeVersion: currentSource.RuntimeVersion, TargetRuntimes: currentSource.TargetRuntimes,
		TailscaleKeyID: "active-redeploy-key", Ciphertext: []byte("opaque"),
	})
	inviteBytes, _ := json.Marshal(activeInvite)
	if err := os.WriteFile(filepath.Join(inviteDir, strings.Repeat("d", 64)+".json"), inviteBytes, 0600); err != nil {
		t.Fatal(err)
	}
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{artifactDir: root, updateManifestPath: manifestPath, inviteDir: inviteDir,
		adminSecret: secret, nonces: make(map[string]time.Time)}
	next := current
	next.Artifacts = append([]bundleArtifact(nil), current.Artifacts...)
	next.Artifacts[0].SHA256 = strings.Repeat("b", 64)
	next.Artifacts[0].SourceManifestSHA256 = strings.Repeat("c", 64)
	next.Artifacts[0].ClientInstallValidation = clientInstallValidationPolicy{DisableAll: true}
	body, _ := json.Marshal(next)
	request := signedRouteRequest(secret, http.MethodPut, updateReleaseAdminPath, "forced-release-nonce-012345", body)
	result := httptest.NewRecorder()
	g.ServeHTTP(result, request)
	if result.Code != http.StatusCreated {
		t.Fatalf("forced same-version publication returned %d: %s", result.Code, result.Body.String())
	}
	published, err := g.readUpdateArtifactManifest()
	if err != nil || published.Artifacts[0].SHA256 != strings.Repeat("b", 64) ||
		!published.Artifacts[0].ClientInstallValidation.DisableAll {
		t.Fatalf("forced same-version publication did not commit exact replacement: %+v, %v", published, err)
	}
}

func TestAuthenticatedSourceOnlyManifestReplacesUnservableLegacyMarker(t *testing.T) {
	root := t.TempDir()
	manifestPath := filepath.Join(root, "release", "update-manifest.json")
	if err := os.MkdirAll(filepath.Dir(manifestPath), 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("a", 64)
	// This is intentionally the obsolete, invalid marker format that causes
	// the currently deployed gateway to return 503. It is structurally readable
	// but not trusted/servable, so only the authenticated source-only PUT may
	// replace it.
	stale := artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{{
		Product: "OpticonBundle", Version: "1.1.40", Role: "ManagedOnly", Architecture: "x64",
		File: "opticon-bundle-1.1.40-managed-win-x64.zip", Size: 2048, SHA256: hash,
		LegacyMigrationSignerThumbprint: invitationSigningKeyID,
	}}}
	staleBytes, _ := json.Marshal(stale)
	if err := os.WriteFile(manifestPath, staleBytes, 0600); err != nil {
		t.Fatal(err)
	}
	next := artifactManifest{SchemaVersion: sourceOnlyManifestSchema, Artifacts: []bundleArtifact{productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.0", Architecture: "source",
		File: "opticon-source-1.2.0.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.0/opticon-source-1.2.0.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
	})}}
	body, _ := json.Marshal(next)
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{artifactDir: root, updateManifestPath: manifestPath, adminSecret: secret, nonces: make(map[string]time.Time)}
	result := httptest.NewRecorder()
	g.ServeHTTP(result, signedRouteRequest(secret, http.MethodPut, updateReleaseAdminPath, "source-only-recovery-nonce-0123", body))
	if result.Code != http.StatusCreated {
		t.Fatalf("source-only recovery returned %d: %s", result.Code, result.Body.String())
	}
	published, err := g.readUpdateArtifactManifest()
	if err != nil {
		t.Fatal(err)
	}
	if published.SchemaVersion != sourceOnlyManifestSchema || len(published.Artifacts) != 1 || published.Artifacts[0].Product != "OpticonSource" {
		t.Fatalf("stale manifest was not atomically replaced with one source record: %#v", published)
	}
}

func TestAuthenticatedManifestPublicationCanRotateAnObsoleteTrustDomain(t *testing.T) {
	root := t.TempDir()
	manifestPath := filepath.Join(root, "manifest.json")
	hash := strings.Repeat("a", 64)
	legacy := artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		{Product: "OpticonBundle", Version: "1.1.38", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.1.38-managed-win-x64.zip", Size: 10, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.38/opticon-bundle-1.1.38-managed-win-x64.zip"},
		{Product: "OpticonBundle", Version: "1.1.38", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-1.1.38-controller-win-x64.zip", Size: 20, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.38/opticon-bundle-1.1.38-controller-win-x64.zip"},
		{Product: "OpticonBootstrap", Version: "1.1.38", Architecture: "x64", File: "opticon-bootstrap-1.1.38.exe", Size: 7, SHA256: hash, SignerThumbprint: invitationSigningKeyID, SigningProfile: "Production", SourceManifestKeyID: invitationSigningKeyID, ProductSigner: invitationSigningKeyID, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.38/opticon-bootstrap-1.1.38.exe"},
	}}
	legacyBytes, _ := json.Marshal(legacy)
	if err := os.WriteFile(manifestPath, legacyBytes, 0600); err != nil {
		t.Fatal(err)
	}
	if err := validateArtifactManifest(legacy); err == nil {
		t.Fatal("legacy invitation-key bootstrap unexpectedly remained trusted")
	}
	next := artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		{Product: "OpticonBundle", Version: "1.1.39", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.1.39-managed-win-x64.zip", Size: 11, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.39/opticon-bundle-1.1.39-managed-win-x64.zip"},
		{Product: "OpticonBundle", Version: "1.1.39", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-1.1.39-controller-win-x64.zip", Size: 21, SHA256: hash, DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.39/opticon-bundle-1.1.39-controller-win-x64.zip"},
		productionArtifact(bundleArtifact{Product: "OpticonBootstrap", Version: "1.1.39", Architecture: "x64", File: "opticon-bootstrap-1.1.39.exe", Size: 7, SHA256: hash, SignerThumbprint: strings.Repeat("C", 40), DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.1.39/opticon-bootstrap-1.1.39.exe"}),
	}}
	body, _ := json.Marshal(next)
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{artifactDir: root, manifestPath: manifestPath, adminSecret: secret, nonces: make(map[string]time.Time)}
	result := httptest.NewRecorder()
	g.ServeHTTP(result, signedRouteRequest(secret, http.MethodPut, releaseAdminPath, "trust-rotation-nonce-012345", body))
	if result.Code != http.StatusCreated {
		t.Fatalf("trust-domain rotation returned %d: %s", result.Code, result.Body.String())
	}
	published, err := g.readArtifactManifest()
	if err != nil {
		t.Fatal(err)
	}
	if version, ok := highestBundleVersion(published); !ok || version != "1.1.39" {
		t.Fatalf("rotated manifest version is %q", version)
	}
}

func TestSourceInvitationPinsAndHashesBothImmutableDownloads(t *testing.T) {
	root := t.TempDir()
	hash := strings.Repeat("b", 64)
	bootstrap := productionArtifact(bundleArtifact{Product: "OpticonBootstrap", Version: "1.2.0", Architecture: "x64", File: "opticon-bootstrap-1.2.0.exe", Size: 20, SHA256: hash, SignerThumbprint: strings.Repeat("C", 40), DownloadURL: "https://d222.cloudfront.net/opticon/releases/1.2.0/opticon-bootstrap-1.2.0.exe"})
	source := productionArtifact(bundleArtifact{Product: "OpticonSource", Version: "1.2.0", Architecture: "source", File: "opticon-source-1.2.0.zip", Size: 30, SHA256: hash, DownloadURL: "https://d222.cloudfront.net/opticon/releases/1.2.0/opticon-source-1.2.0.zip", SDKVersion: pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion, TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash})
	encoded, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{bootstrap, source}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), encoded, 0600); err != nil {
		t.Fatal(err)
	}
	g := &gateway{artifactDir: root}
	result := httptest.NewRecorder()
	g.sourceInvitationLandingSecure(result, httptest.NewRequest(http.MethodGet, "https://example.test/", nil), strings.Repeat("P", 24), productionInvite(hostedInvite{DeviceName: "PC", ExpiresAt: time.Now().Add(time.Hour), BootstrapVersion: bootstrap.Version, BootstrapFile: bootstrap.File, BootstrapSize: bootstrap.Size, BootstrapSHA256: bootstrap.SHA256, BootstrapSigner: bootstrap.SignerThumbprint}), source)
	landing := result.Body.String()
	if !strings.Contains(landing, bootstrap.DownloadURL) || !strings.Contains(landing, source.DownloadURL) ||
		!strings.Contains(landing, "b.size!==size") || !strings.Contains(landing, "Source SHA-256:") ||
		!strings.Contains(landing, "Bootstrap SHA-256:") || strings.Count(landing, hash) < 4 ||
		!strings.Contains(landing, "crypto.subtle.digest") {
		t.Fatal("source invitation did not size-and-SHA256 check both immutable CloudFront downloads")
	}
	for _, forbidden := range []string{".cmd", "ExecutionPolicy", "Start-Process", "downloadStarter"} {
		if strings.Contains(landing, forbidden) {
			t.Fatalf("source invitation retained unsafe fallback %q", forbidden)
		}
	}
	if !strings.Contains(result.Header().Get("Content-Security-Policy"), "connect-src https://d222.cloudfront.net") {
		t.Fatal("invitation CSP did not permit the pinned CloudFront bootstrap origin")
	}
	if strings.Contains(landing, "a.href=\"https://d222.cloudfront.net") {
		t.Fatal("invitation still relies on a cross-origin anchor download filename")
	}
}

func TestSourceOnlyInvitationPinsNoReleaseBootstrap(t *testing.T) {
	root := t.TempDir()
	inviteDir := filepath.Join(root, "invites")
	if err := os.MkdirAll(inviteDir, 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("b", 64)
	launcherBytes := []byte("signed-launcher-test-bytes")
	launcherHash := sha256.Sum256(launcherBytes)
	source := productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.1", Architecture: "source",
		File: "opticon-source-1.2.1.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d222.cloudfront.net/opticon/releases/1.2.1/opticon-source-1.2.1.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
		SourceLauncherFile: "opticon-source-launcher-1.2.1.exe", SourceLauncherSize: int64(len(launcherBytes)),
		SourceLauncherSHA256: hex.EncodeToString(launcherHash[:]),
	})
	if err := os.WriteFile(filepath.Join(root, source.SourceLauncherFile), launcherBytes, 0600); err != nil {
		t.Fatal(err)
	}
	encoded, _ := json.Marshal(artifactManifest{SchemaVersion: sourceOnlyManifestSchema, Artifacts: []bundleArtifact{source}})
	if err := os.WriteFile(filepath.Join(root, "update-manifest.json"), encoded, 0600); err != nil {
		t.Fatal(err)
	}
	secret := []byte("0123456789abcdef0123456789abcdef")
	fixedNow := time.Date(2026, time.August, 10, 21, 30, 0, 0, time.UTC)
	signer := &s3SourceDownloadSigner{
		bucket: "opticon-test-bucket", region: "us-east-1",
		accessKeyID: "AKIAIOSFODNN7EXAMPLE", secretKey: "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
	}
	g := &gateway{artifactDir: root, inviteDir: inviteDir, adminSecret: secret, nonces: make(map[string]time.Time), sourceSigner: signer, now: func() time.Time { return fixedNow }}
	publicID := strings.Repeat("S", 32)
	idHash := sha256.Sum256([]byte(publicID))
	invite := productionInvite(hostedInvite{
		DeviceName: "Source-only PC", Role: "ManagedOnly", ExpiresAt: fixedNow.Add(time.Hour),
		InstallProtocol: sourceInstallProtocol, ReleaseVersion: source.Version,
		SourceFile: source.File, SourceSize: source.Size, SourceSHA256: source.SHA256,
		SourceManifestSHA256: source.SourceManifestSHA256, SDKVersion: source.SDKVersion,
		RuntimeVersion: source.RuntimeVersion, TargetRuntimes: source.TargetRuntimes,
		TailscaleKeyID: "source-only-test-key",
		Ciphertext:     bytes.Repeat([]byte{0x5a}, 96),
	})
	body, _ := json.Marshal(invite)
	put := httptest.NewRecorder()
	g.ServeHTTP(put, signedRouteRequest(secret, http.MethodPut, inviteAdminPrefix+hex.EncodeToString(idHash[:]), "source-only-invite-nonce-0123", body))
	if put.Code != http.StatusCreated {
		t.Fatalf("source-only invitation PUT returned %d: %s", put.Code, put.Body.String())
	}
	landing := httptest.NewRecorder()
	g.ServeHTTP(landing, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID, nil))
	if landing.Code != http.StatusOK || !strings.Contains(landing.Body.String(), invitePublicPrefix+publicID+"/launcher") ||
		!strings.Contains(landing.Body.String(), "valid for 30 minutes") ||
		!strings.Contains(landing.Body.String(), "No ZIP extraction or invitation paste is needed") ||
		!strings.Contains(landing.Body.String(), "Install-Opticon-"+publicID+"--'+key+'--"+source.SourceLauncherSHA256+".exe") ||
		strings.Contains(landing.Body.String(), source.DownloadURL) || strings.Contains(landing.Body.String(), "fetch(") ||
		strings.Contains(landing.Body.String(), "crypto.subtle") || strings.Contains(landing.Body.String(), "opticon-bootstrap-") {
		t.Fatalf("source-only landing is not a one-click fragment-bound installer: %d %s", landing.Code, landing.Body.String())
	}
	launcher := httptest.NewRecorder()
	g.ServeHTTP(launcher, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID+"/launcher", nil))
	if launcher.Code != http.StatusOK || !bytes.Equal(launcher.Body.Bytes(), launcherBytes) ||
		launcher.Header().Get("Cache-Control") != "no-store" ||
		launcher.Header().Get("Content-Disposition") != "" {
		t.Fatalf("signed source launcher returned %d with unexpected headers or bytes", launcher.Code)
	}
	redirect := httptest.NewRecorder()
	g.ServeHTTP(redirect, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID+"/source", nil))
	if redirect.Code != http.StatusTemporaryRedirect || redirect.Header().Get("Cache-Control") != "no-store" {
		t.Fatalf("source download returned %d: %s", redirect.Code, redirect.Body.String())
	}
	location, err := url.Parse(redirect.Header().Get("Location"))
	if err != nil {
		t.Fatal(err)
	}
	if location.Scheme != "https" || location.Host != "opticon-test-bucket.s3.us-east-1.amazonaws.com" ||
		location.Path != "/opticon/releases/1.2.1/opticon-source-1.2.1.zip" ||
		location.Query().Get("X-Amz-Expires") != "1800" ||
		location.Query().Get("X-Amz-Date") != "20260810T213000Z" ||
		!regexp.MustCompile(`^[a-f0-9]{64}$`).MatchString(location.Query().Get("X-Amz-Signature")) {
		t.Fatalf("source download was not an exact 30-minute S3 signature: %s", location.String())
	}
	withBootstrap := invite
	withBootstrap.BootstrapFile = "opticon-bootstrap-1.2.0.exe"
	invalid, _ := json.Marshal(withBootstrap)
	bad := httptest.NewRecorder()
	g.ServeHTTP(bad, signedRouteRequest(secret, http.MethodPut, inviteAdminPrefix+strings.Repeat("a", 64), "source-only-bootstrap-nonce-012", invalid))
	if bad.Code != http.StatusBadRequest {
		t.Fatalf("source-only invitation carrying a bootstrap returned %d", bad.Code)
	}
}

func TestArtifactConcurrencyLimitFailsClosed(t *testing.T) {
	slots := make(chan struct{}, 1)
	slots <- struct{}{}
	g := &gateway{artifactSlots: slots}
	result := httptest.NewRecorder()
	g.artifact(result, httptest.NewRequest(http.MethodGet, artifactPrefix+"busy.zip", nil))
	if result.Code != http.StatusTooManyRequests {
		t.Fatalf("saturated artifact service returned %d", result.Code)
	}
}

type deadlineRecorder struct {
	*httptest.ResponseRecorder
	deadlines []time.Time
}

func (recorder *deadlineRecorder) SetWriteDeadline(deadline time.Time) error {
	recorder.deadlines = append(recorder.deadlines, deadline)
	return nil
}

func TestArtifactWriterRefreshesAndClearsIdleDeadline(t *testing.T) {
	recorder := &deadlineRecorder{ResponseRecorder: httptest.NewRecorder()}
	writer := newIdleDeadlineWriter(recorder, 30*time.Second)
	if _, err := writer.Write([]byte("one")); err != nil {
		t.Fatal(err)
	}
	if _, err := writer.Write([]byte("two")); err != nil {
		t.Fatal(err)
	}
	writer.clear()
	if len(recorder.deadlines) < 4 || !recorder.deadlines[len(recorder.deadlines)-1].IsZero() {
		t.Fatal("artifact writer did not refresh each idle deadline and clear it after streaming")
	}
}

func TestProductionArtifactTrustRejectsDeveloperAndConfusedKeys(t *testing.T) {
	base := productionArtifact(bundleArtifact{})
	if !validProductionArtifactTrust(base) {
		t.Fatal("exact configured production trust was rejected")
	}
	developer := base
	developer.SigningProfile = "Developer"
	if validProductionArtifactTrust(developer) {
		t.Fatal("developer artifact was accepted by the public production manifest")
	}
	confused := base
	confused.ProductSigner = confused.SourceManifestKeyID
	if validProductionArtifactTrust(confused) {
		t.Fatal("source-release/product trust-domain confusion was accepted")
	}
	trustedSigningProfile = "OwnerManaged"
	defer func() { trustedSigningProfile = "Production" }()
	ownerManaged := base
	ownerManaged.SigningProfile = "OwnerManaged"
	if !validProductionArtifactTrust(ownerManaged) || validProductionArtifactTrust(base) {
		t.Fatal("owner-managed trust profile was not enforced exactly")
	}
}

func TestLegacyInvitationLandingIsRetired(t *testing.T) {
	root := t.TempDir()
	publicID := strings.Repeat("L", 24)
	hash := sha256.Sum256([]byte(publicID))
	if err := os.MkdirAll(filepath.Join(root, "invites"), 0700); err != nil {
		t.Fatal(err)
	}
	data, _ := json.Marshal(hostedInvite{DeviceName: "Legacy PC", Role: "ManagedOnly", ExpiresAt: time.Now().Add(time.Hour), Ciphertext: bytes.Repeat([]byte{1}, 96)})
	if err := os.WriteFile(filepath.Join(root, "invites", hex.EncodeToString(hash[:])+".json"), data, 0600); err != nil {
		t.Fatal(err)
	}
	g := &gateway{inviteDir: filepath.Join(root, "invites")}
	result := httptest.NewRecorder()
	g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID, nil))
	if result.Code != http.StatusGone || !strings.Contains(result.Body.String(), "new source-build invitation") {
		t.Fatalf("legacy landing was not retired: %d %s", result.Code, result.Body.String())
	}
}

func TestReleasePreflightRedactsActiveInvitationSecrets(t *testing.T) {
	g, secret, idHash, ciphertextMarker := releasePreflightGatewayFixture(t, "preflight-key-77")
	body := []byte(`{"targetVersion":"1.2.2"}`)
	result := httptest.NewRecorder()
	g.ServeHTTP(result, signedRouteRequest(secret, http.MethodPost, releasePreflightPath, "release-preflight-nonce-012345", body))
	if result.Code != http.StatusOK {
		t.Fatalf("release preflight returned %d: %s", result.Code, result.Body.String())
	}
	if strings.Contains(result.Body.String(), "ciphertext") || strings.Contains(result.Body.String(), "preflight-key-77") ||
		strings.Contains(result.Body.String(), ciphertextMarker) {
		t.Fatal("release preflight exposed invitation ciphertext or network-key metadata")
	}
	var preflight releasePreflightResponse
	if err := json.Unmarshal(result.Body.Bytes(), &preflight); err != nil {
		t.Fatal(err)
	}
	if preflight.SchemaVersion != 1 || preflight.GatewayReleaseProtocol != releaseProtocolVersion ||
		preflight.DeployedVersion != "1.2.1" || preflight.AlreadyDeployed ||
		!preflight.RequiresInvitationRemoval || preflight.CancellationBlocked || len(preflight.BlockingInvitations) != 1 ||
		preflight.BlockingInvitations[0].IDHash != idHash || !preflight.BlockingInvitations[0].CanRevoke {
		t.Fatalf("release preflight did not return the expected sanitized deployment plan: %+v", preflight)
	}

	// The same signed request nonce must remain one-time even on this new admin route.
	replay := httptest.NewRecorder()
	g.ServeHTTP(replay, signedRouteRequest(secret, http.MethodPost, releasePreflightPath, "release-preflight-nonce-012345", body))
	if replay.Code != http.StatusUnauthorized {
		t.Fatalf("release preflight replay returned %d instead of 401", replay.Code)
	}
}

func TestInvitationInventoryShowsGatewayOnlyLinksWithoutSecrets(t *testing.T) {
	g, secret, idHash, ciphertextMarker := releasePreflightGatewayFixture(t, "inventory-key-77")
	result := httptest.NewRecorder()
	g.ServeHTTP(result, signedRouteRequest(secret, http.MethodGet, inviteInventoryPath, "invite-inventory-nonce-012345", nil))
	if result.Code != http.StatusOK {
		t.Fatalf("invitation inventory returned %d: %s", result.Code, result.Body.String())
	}
	if strings.Contains(result.Body.String(), "ciphertext") || strings.Contains(result.Body.String(), "inventory-key-77") ||
		strings.Contains(result.Body.String(), ciphertextMarker) || strings.Contains(result.Body.String(), "tailscaleKeyId") {
		t.Fatal("invitation inventory exposed ciphertext or network-key metadata")
	}
	var inventory invitationInventoryResponse
	if err := json.Unmarshal(result.Body.Bytes(), &inventory); err != nil {
		t.Fatal(err)
	}
	if inventory.SchemaVersion != 1 || len(inventory.Invitations) != 1 ||
		inventory.Invitations[0].IDHash != idHash || inventory.Invitations[0].DeviceName != "Gateway-only invite" ||
		!inventory.Invitations[0].CanRevoke || inventory.Invitations[0].CreatedAt.IsZero() {
		t.Fatalf("inventory did not return the expected sanitized active link: %+v", inventory)
	}

	replay := httptest.NewRecorder()
	g.ServeHTTP(replay, signedRouteRequest(secret, http.MethodGet, inviteInventoryPath, "invite-inventory-nonce-012345", nil))
	if replay.Code != http.StatusUnauthorized {
		t.Fatalf("invitation inventory replay returned %d instead of 401", replay.Code)
	}
}

func TestInvitationDeleteRevokesStoredKeyBeforeRemovingGatewayOnlyLink(t *testing.T) {
	g, secret, idHash, _ := releasePreflightGatewayFixture(t, "inventory-delete-key-42")
	invitePath := filepath.Join(g.inviteDir, idHash+".json")
	var revoked bool
	headscale := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if _, err := os.Stat(invitePath); err != nil {
			t.Fatalf("gateway-only link was deleted before network-key revocation: %v", err)
		}
		var request struct {
			ID string `json:"id"`
		}
		if r.Method != http.MethodPost || r.URL.Path != "/api/v1/preauthkey/expire" ||
			json.NewDecoder(r.Body).Decode(&request) != nil || request.ID != "inventory-delete-key-42" {
			http.Error(w, "unexpected revocation", http.StatusBadRequest)
			return
		}
		revoked = true
		w.WriteHeader(http.StatusOK)
	}))
	defer headscale.Close()
	g.headscaleKey = "headscale-test-key"
	g.headscaleAdminURL = headscale.URL

	result := httptest.NewRecorder()
	path := inviteAdminPrefix + idHash
	g.ServeHTTP(result, signedRouteRequest(secret, http.MethodDelete, path, "invite-delete-nonce-012345678", nil))
	if result.Code != http.StatusNoContent {
		t.Fatalf("gateway-only invitation deletion returned %d: %s", result.Code, result.Body.String())
	}
	if !revoked {
		t.Fatal("gateway-only invitation network key was not revoked")
	}
	if _, err := os.Stat(invitePath); !os.IsNotExist(err) {
		t.Fatalf("gateway-only invitation remained after deletion: %v", err)
	}
}

func TestReleaseRevokeActiveRevokesKeysBeforeDeletingHostedLinks(t *testing.T) {
	g, secret, idHash, _ := releasePreflightGatewayFixture(t, "revoke-key-42")
	invitePath := filepath.Join(g.inviteDir, idHash+".json")
	var revoked bool
	headscale := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.URL.Path != "/api/v1/preauthkey/expire" ||
			r.Header.Get("Authorization") != "Bearer headscale-test-key" {
			http.Error(w, "unexpected request", http.StatusBadRequest)
			return
		}
		if _, err := os.Stat(invitePath); err != nil {
			t.Fatalf("hosted invitation was deleted before its network key was revoked: %v", err)
		}
		var request struct {
			ID string `json:"id"`
		}
		if err := json.NewDecoder(r.Body).Decode(&request); err != nil || request.ID != "revoke-key-42" {
			http.Error(w, "bad key", http.StatusBadRequest)
			return
		}
		revoked = true
		w.WriteHeader(http.StatusOK)
	}))
	defer headscale.Close()
	g.headscaleKey = "headscale-test-key"
	g.headscaleAdminURL = headscale.URL

	preflightBody := []byte(`{"targetVersion":"1.2.2"}`)
	preflightResult := httptest.NewRecorder()
	g.ServeHTTP(preflightResult, signedRouteRequest(secret, http.MethodPost, releasePreflightPath, "release-remove-plan-nonce-012345", preflightBody))
	if preflightResult.Code != http.StatusOK {
		t.Fatalf("release preflight returned %d: %s", preflightResult.Code, preflightResult.Body.String())
	}
	var preflight releasePreflightResponse
	if err := json.Unmarshal(preflightResult.Body.Bytes(), &preflight); err != nil {
		t.Fatal(err)
	}
	cancellationBody, _ := json.Marshal(releaseCancellationRequest{TargetVersion: "1.2.2"})
	cancellationResult := httptest.NewRecorder()
	g.ServeHTTP(cancellationResult, signedRouteRequest(secret, http.MethodPost, releaseRevokeActivePath, "release-remove-confirm-nonce-012", cancellationBody))
	if cancellationResult.Code != http.StatusOK {
		t.Fatalf("release cancellation returned %d: %s", cancellationResult.Code, cancellationResult.Body.String())
	}
	if !revoked {
		t.Fatal("gateway did not revoke the active invitation network key")
	}
	if _, err := os.Stat(invitePath); !os.IsNotExist(err) {
		t.Fatalf("hosted invitation remained after successful key revocation: %v", err)
	}
	var cancellation releaseCancellationResponse
	if err := json.Unmarshal(cancellationResult.Body.Bytes(), &cancellation); err != nil || cancellation.RemovedCount != 1 ||
		len(cancellation.RemovedInviteIDs) != 1 || cancellation.RemovedInviteIDs[0] != idHash {
		t.Fatalf("release cancellation response is incomplete: %+v (%v)", cancellation, err)
	}
}

func TestReleaseRevokeActiveUsesCurrentInvitationSet(t *testing.T) {
	g, secret, idHash, _ := releasePreflightGatewayFixture(t, "known-key-42")
	preflightBody := []byte(`{"targetVersion":"1.2.2"}`)
	preflightResult := httptest.NewRecorder()
	g.ServeHTTP(preflightResult, signedRouteRequest(secret, http.MethodPost, releasePreflightPath, "release-race-plan-nonce-012345", preflightBody))
	if preflightResult.Code != http.StatusOK {
		t.Fatalf("release preflight returned %d: %s", preflightResult.Code, preflightResult.Body.String())
	}
	// Simulate an invite created after the operator saw the confirmation but
	// before submitting it. Direct revocation uses the set active at call time.
	originalPath := filepath.Join(g.inviteDir, idHash+".json")
	data, err := os.ReadFile(originalPath)
	if err != nil {
		t.Fatal(err)
	}
	var later hostedInvite
	if err := json.Unmarshal(data, &later); err != nil {
		t.Fatal(err)
	}
	later.DeviceName = "Created after confirmation"
	later.TailscaleKeyID = "later-key-77"
	data, err = json.Marshal(later)
	if err != nil {
		t.Fatal(err)
	}
	laterIDHash := strings.Repeat("e", 64)
	laterPath := filepath.Join(g.inviteDir, laterIDHash+".json")
	if err := os.WriteFile(laterPath, data, 0600); err != nil {
		t.Fatal(err)
	}

	revoked := make(map[string]bool)
	headscale := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var request struct {
			ID string `json:"id"`
		}
		_ = json.NewDecoder(r.Body).Decode(&request)
		revoked[request.ID] = true
		w.WriteHeader(http.StatusOK)
	}))
	defer headscale.Close()
	g.headscaleKey = "headscale-test-key"
	g.headscaleAdminURL = headscale.URL
	body, _ := json.Marshal(releaseCancellationRequest{TargetVersion: "1.2.2"})
	result := httptest.NewRecorder()
	g.ServeHTTP(result, signedRouteRequest(secret, http.MethodPost, releaseRevokeActivePath, "release-current-set-nonce-012", body))
	if result.Code != http.StatusOK || !revoked["known-key-42"] || !revoked["later-key-77"] {
		t.Fatalf("direct revocation did not use the current active invitation set: %d %+v", result.Code, revoked)
	}
	for _, path := range []string{originalPath, laterPath} {
		if _, err := os.Stat(path); !os.IsNotExist(err) {
			t.Fatalf("current active invitation remained after revocation: %v", err)
		}
	}
}

func TestReleasePreflightAllowsExplicitKeylessLegacyLinkAbandonment(t *testing.T) {
	g, secret, idHash, _ := releasePreflightGatewayFixture(t, "")
	preflightBody := []byte(`{"targetVersion":"1.2.2"}`)
	preflightResult := httptest.NewRecorder()
	g.ServeHTTP(preflightResult, signedRouteRequest(secret, http.MethodPost, releasePreflightPath, "release-legacy-plan-nonce-012345", preflightBody))
	if preflightResult.Code != http.StatusOK {
		t.Fatalf("legacy release preflight returned %d: %s", preflightResult.Code, preflightResult.Body.String())
	}
	var preflight releasePreflightResponse
	if err := json.Unmarshal(preflightResult.Body.Bytes(), &preflight); err != nil {
		t.Fatal(err)
	}
	if !preflight.RequiresInvitationRemoval || preflight.CancellationBlocked || len(preflight.BlockingInvitations) != 1 ||
		preflight.BlockingInvitations[0].CanRevoke || preflight.BlockingInvitations[0].BlockedReason == "" {
		t.Fatalf("keyless legacy invitation did not produce an explicit abandonment warning: %+v", preflight)
	}
	cancellationBody, _ := json.Marshal(releaseCancellationRequest{TargetVersion: "1.2.2"})
	cancellationResult := httptest.NewRecorder()
	g.ServeHTTP(cancellationResult, signedRouteRequest(secret, http.MethodPost, releaseRevokeActivePath, "release-legacy-abandon-nonce-01", cancellationBody))
	if cancellationResult.Code != http.StatusOK {
		t.Fatalf("keyless legacy abandonment returned %d: %s", cancellationResult.Code, cancellationResult.Body.String())
	}
	if _, err := os.Stat(filepath.Join(g.inviteDir, idHash+".json")); !os.IsNotExist(err) {
		t.Fatalf("keyless legacy hosted link remained after confirmed abandonment: %v", err)
	}
}

func releasePreflightGatewayFixture(t *testing.T, keyID string) (*gateway, []byte, string, string) {
	t.Helper()
	root := t.TempDir()
	inviteDir := filepath.Join(root, "invites")
	if err := os.MkdirAll(inviteDir, 0700); err != nil {
		t.Fatal(err)
	}
	hash := strings.Repeat("a", 64)
	source := productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.1", Architecture: "source",
		File: "opticon-source-1.2.1.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d222.cloudfront.net/opticon/releases/1.2.1/opticon-source-1.2.1.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
		SourceLauncherFile: "opticon-source-launcher-1.2.1.exe", SourceLauncherSize: 12,
		SourceLauncherSHA256: hash,
	})
	install := artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		productionArtifact(bundleArtifact{Product: "OpticonBundle", Version: "1.2.1", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.2.1-managed-win-x64.zip", Size: 2048, SHA256: hash, DownloadURL: "https://d222.cloudfront.net/opticon/releases/1.2.1/opticon-bundle-1.2.1-managed-win-x64.zip"}),
		productionArtifact(bundleArtifact{Product: "OpticonBundle", Version: "1.2.1", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-1.2.1-controller-win-x64.zip", Size: 2048, SHA256: hash, DownloadURL: "https://d222.cloudfront.net/opticon/releases/1.2.1/opticon-bundle-1.2.1-controller-win-x64.zip"}),
		productionArtifact(bundleArtifact{Product: "OpticonBootstrap", Version: "1.2.1", Architecture: "x64", File: "opticon-bootstrap-1.2.1.exe", Size: 2048, SHA256: hash, SignerThumbprint: testProductionProductSigner, DownloadURL: "https://d222.cloudfront.net/opticon/releases/1.2.1/opticon-bootstrap-1.2.1.exe"}),
	}}
	manifest, _ := json.Marshal(install)
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	updateManifest, _ := json.Marshal(artifactManifest{SchemaVersion: sourceOnlyManifestSchema, Artifacts: []bundleArtifact{source}})
	if err := os.WriteFile(filepath.Join(root, "update-manifest.json"), updateManifest, 0600); err != nil {
		t.Fatal(err)
	}
	idHash := strings.Repeat("d", 64)
	ciphertextMarker := "release-preflight-secret-marker"
	invite := productionInvite(hostedInvite{
		DeviceName: "Gateway-only invite", Role: "ManagedOnly", ExpiresAt: time.Now().Add(time.Hour),
		InstallProtocol: sourceInstallProtocol, ReleaseVersion: source.Version,
		SourceFile: source.File, SourceSize: source.Size, SourceSHA256: source.SHA256,
		SourceManifestSHA256: source.SourceManifestSHA256, SDKVersion: source.SDKVersion,
		RuntimeVersion: source.RuntimeVersion, TargetRuntimes: source.TargetRuntimes,
		TailscaleKeyID: keyID, Ciphertext: []byte(ciphertextMarker),
	})
	// The preflight reader only needs opaque ciphertext metadata. Keep the test
	// body valid for invitation storage semantics as well.
	invite.Ciphertext = append(invite.Ciphertext, bytes.Repeat([]byte{0x5a}, 96)...)
	data, _ := json.Marshal(invite)
	if err := os.WriteFile(filepath.Join(inviteDir, idHash+".json"), data, 0600); err != nil {
		t.Fatal(err)
	}
	secret := []byte("0123456789abcdef0123456789abcdef")
	return &gateway{adminSecret: secret, inviteDir: inviteDir, artifactDir: root, nonces: make(map[string]time.Time)}, secret, idHash, ciphertextMarker
}

func TestUnsafeBundleFilenameCannotReachInstallerCommand(t *testing.T) {
	root := t.TempDir()
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(bundleDir, 0700); err != nil {
		t.Fatal(err)
	}
	unsafeName := "opticon-bundle-x';Start-Process calc;#.zip"
	artifact := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: unsafeName, Size: 10, SHA256: strings.Repeat("d", 64)}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{artifact}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(bundleDir, unsafeName), bytes.Repeat([]byte{'x'}, 10), 0600); err != nil {
		t.Fatal(err)
	}
	g := &gateway{artifactDir: root, bundleDir: bundleDir}
	if _, err := g.bundleForRole("ManagedOnly"); err == nil {
		t.Fatal("unsafe bundle filename was selected")
	}
	result := httptest.NewRecorder()
	g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if result.Code != http.StatusServiceUnavailable || strings.Contains(result.Body.String(), unsafeName) {
		t.Fatal("unsafe bundle manifest did not fail closed")
	}
}

func TestMigrateBundleUploadsRequiresManifestSizeAndHash(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	stagingDir := filepath.Join(root, "staging")
	bundleDir := filepath.Join(root, "bundles")
	for _, directory := range []string{artifactDir, stagingDir, bundleDir} {
		if err := os.MkdirAll(directory, 0700); err != nil {
			t.Fatal(err)
		}
	}
	goodBytes := []byte("verified legacy upload")
	goodHash := sha256.Sum256(goodBytes)
	badBytes := []byte("tampered legacy upload")
	badHash := sha256.Sum256([]byte("different signed bytes"))
	good := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-good-managed-win-x64.zip", Size: int64(len(goodBytes)), SHA256: hex.EncodeToString(goodHash[:])}
	bad := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-bad-controller-win-x64.zip", Size: int64(len(badBytes)), SHA256: hex.EncodeToString(badHash[:])}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{good, bad}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(stagingDir, good.File+".upload"), goodBytes, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(stagingDir, bad.File+".upload"), badBytes, 0600); err != nil {
		t.Fatal(err)
	}
	if err := migrateBundleUploads(stagingDir, artifactDir, bundleDir); err != nil {
		t.Fatal(err)
	}
	stored, err := os.ReadFile(filepath.Join(bundleDir, good.File))
	if err != nil || !bytes.Equal(stored, goodBytes) {
		t.Fatal("verified legacy upload was not migrated")
	}
	if _, err := os.Stat(filepath.Join(bundleDir, bad.File)); !os.IsNotExist(err) {
		t.Fatal("hash-mismatched legacy upload became final")
	}
	if _, err := os.Stat(filepath.Join(stagingDir, bad.File+".upload")); !os.IsNotExist(err) {
		t.Fatal("invalid legacy upload was not discarded")
	}
}

func TestPublicRoutesExcludeAdminAndHelperPages(t *testing.T) {
	for _, path := range []string{"/api/v1/node", "/swagger", "/version", "/apple", "/windows", "/register/abc", "/auth/abc", "/"} {
		if isPublicControlRoute(http.MethodGet, path) {
			t.Fatalf("helper/admin route became public: %s", path)
		}
	}
	for _, path := range []string{"/key", "/ts2021", "/machine/map", "/derp", "/bootstrap-dns"} {
		if !isPublicControlRoute(http.MethodGet, path) {
			t.Fatalf("required control route was blocked: %s", path)
		}
	}
}

func TestAdminAllowlist(t *testing.T) {
	allowed := [][2]string{{"GET", "api/v1/node"}, {"POST", "api/v1/preauthkey"}, {"POST", "api/v1/node/7/tags"}, {"DELETE", "api/v1/node/7"}}
	for _, item := range allowed {
		if !isAllowedAdminRoute(item[0], item[1]) {
			t.Fatalf("expected allowed: %v", item)
		}
	}
	for _, path := range []string{"api/v1/apikey", "api/v1/user", "api/v1/policy", "swagger"} {
		if isAllowedAdminRoute(http.MethodGet, path) || isAllowedAdminRoute(http.MethodPost, path) {
			t.Fatalf("unexpected admin route: %s", path)
		}
	}
}

func TestHMACRejectsReplayAndStaleTimestamp(t *testing.T) {
	now := time.Unix(1_800_000_000, 0)
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, nonces: make(map[string]time.Time)}
	body := []byte(`{"user":"1"}`)
	r := signedRequest(secret, now, "fresh-nonce-012345678901234", body)
	if !g.authenticate(r, body, now) {
		t.Fatal("valid HMAC was rejected")
	}
	if g.authenticate(r, body, now) {
		t.Fatal("replayed nonce was accepted")
	}
	stale := signedRequest(secret, now.Add(-10*time.Minute), "stale-nonce-012345678901234", body)
	if g.authenticate(stale, body, now) {
		t.Fatal("stale HMAC was accepted")
	}
}

func TestHMACReplayRemainsRejectedAcrossGatewayRestart(t *testing.T) {
	now := time.Unix(1_800_000_000, 0)
	secret := []byte("0123456789abcdef0123456789abcdef")
	nonceDir := t.TempDir()
	body := []byte(`{"user":"1"}`)
	r := signedRequest(secret, now, "durable-nonce-012345678901", body)
	first := &gateway{adminSecret: secret, nonceDir: nonceDir}
	if !first.authenticate(r, body, now) {
		t.Fatal("valid HMAC was rejected")
	}
	second := &gateway{adminSecret: secret, nonceDir: nonceDir}
	if second.authenticate(r, body, now) {
		t.Fatal("replayed nonce was accepted after gateway restart")
	}
}

func TestPersistentNonceNeverDeletesRecentInFlightTarget(t *testing.T) {
	now := time.Now()
	nonce := "in-flight-nonce-012345678901"
	nonceDir := t.TempDir()
	hash := sha256.Sum256([]byte(nonce))
	path := filepath.Join(nonceDir, hex.EncodeToString(hash[:])+".nonce")
	if err := os.WriteFile(path, nil, 0600); err != nil {
		t.Fatal(err)
	}
	g := &gateway{nonceDir: nonceDir}
	if g.consumePersistentNonce(nonce, now) {
		t.Fatal("a recent O_EXCL nonce placeholder was deleted and replayed")
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("recent in-flight nonce was removed: %v", err)
	}
}

func TestPersistentNonceIsAtomicAcrossGatewayInstances(t *testing.T) {
	now := time.Now()
	nonceDir := t.TempDir()
	start := make(chan struct{})
	results := make(chan bool, 2)
	for range 2 {
		g := &gateway{nonceDir: nonceDir}
		go func() {
			<-start
			results <- g.consumePersistentNonce("concurrent-nonce-0123456789", now)
		}()
	}
	close(start)
	accepted := 0
	for range 2 {
		if <-results {
			accepted++
		}
	}
	if accepted != 1 {
		t.Fatalf("expected exactly one persistent nonce winner, got %d", accepted)
	}
}

func TestAdministrativeBodyConcurrencyCapFailsClosed(t *testing.T) {
	g := &gateway{adminSlots: make(chan struct{}, 1)}
	g.adminSlots <- struct{}{}
	result := httptest.NewRecorder()
	g.ServeHTTP(result, httptest.NewRequest(http.MethodPut, releaseAdminPath, strings.NewReader(`{}`)))
	if result.Code != http.StatusTooManyRequests {
		t.Fatalf("saturated administrative body reader returned %d", result.Code)
	}
}

func TestPublicControlConcurrencyCapsFailClosedByRouteClass(t *testing.T) {
	for _, test := range []struct {
		name   string
		path   string
		stream bool
	}{
		{name: "machine control", path: "/machine/register"},
		{name: "DERP stream", path: "/derp", stream: true},
		{name: "ts2021 stream", path: "/ts2021", stream: true},
	} {
		t.Run(test.name, func(t *testing.T) {
			g := &gateway{proxySlots: make(chan struct{}, 1), streamSlots: make(chan struct{}, 1)}
			slots := g.proxySlots
			if test.stream {
				slots = g.streamSlots
			}
			slots <- struct{}{}
			result := httptest.NewRecorder()
			g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, test.path, nil))
			if result.Code != http.StatusTooManyRequests {
				t.Fatalf("saturated route returned %d", result.Code)
			}
			if result.Header().Get("Retry-After") != "5" {
				t.Fatal("saturated public control route omitted Retry-After")
			}
		})
	}
}

func signedRequest(secret []byte, timestamp time.Time, nonce string, body []byte) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "https://example.test/opticon/v1/headscale/api/v1/preauthkey", strings.NewReader(string(body)))
	hash := sha256.Sum256(body)
	hashText := hex.EncodeToString(hash[:])
	timeText := strconv.FormatInt(timestamp.Unix(), 10)
	canonical := strings.Join([]string{r.Method, r.URL.RequestURI(), timeText, nonce, hashText}, "\n")
	signature := hmac.New(sha256.New, secret)
	_, _ = signature.Write([]byte(canonical))
	r.Header.Set("X-Opticon-Key-Id", "primary")
	r.Header.Set("X-Opticon-Timestamp", timeText)
	r.Header.Set("X-Opticon-Nonce", nonce)
	r.Header.Set("X-Opticon-Content-SHA256", hashText)
	r.Header.Set("X-Opticon-Signature", hex.EncodeToString(signature.Sum(nil)))
	return r
}

func signedRouteRequest(secret []byte, method, path, nonce string, body []byte) *http.Request {
	if body == nil {
		body = []byte{}
	}
	r := httptest.NewRequest(method, "https://example.test"+path, bytes.NewReader(body))
	hash := sha256.Sum256(body)
	hashText := hex.EncodeToString(hash[:])
	timeText := strconv.FormatInt(time.Now().Unix(), 10)
	canonical := strings.Join([]string{method, r.URL.RequestURI(), timeText, nonce, hashText}, "\n")
	signature := hmac.New(sha256.New, secret)
	_, _ = signature.Write([]byte(canonical))
	r.Header.Set("X-Opticon-Key-Id", "primary")
	r.Header.Set("X-Opticon-Timestamp", timeText)
	r.Header.Set("X-Opticon-Nonce", nonce)
	r.Header.Set("X-Opticon-Content-SHA256", hashText)
	r.Header.Set("X-Opticon-Signature", hex.EncodeToString(signature.Sum(nil)))
	return r
}
