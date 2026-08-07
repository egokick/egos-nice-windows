package main

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestHostedInvitationLifecycle(t *testing.T) {
	root := t.TempDir()
	inviteDir := filepath.Join(root, "invites")
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(inviteDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	bundleBytes := []byte("signed reusable bundle fixture")
	bundleHash := sha256.Sum256(bundleBytes)
	bundle := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.0.0-managed-win-x64.zip", Size: int64(len(bundleBytes)), SHA256: hex.EncodeToString(bundleHash[:])}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{bundle}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	if err := os.WriteFile(filepath.Join(bundleDir, bundle.File), bundleBytes, 0444); err != nil { t.Fatal(err) }

	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, inviteDir: inviteDir, artifactDir: artifactDir, bundleDir: bundleDir, publicOrigin: "https://opticon.example.test", nonces: make(map[string]time.Time)}
	publicID := strings.Repeat("A", 24)
	idHash := sha256.Sum256([]byte(publicID))
	ciphertext := bytes.Repeat([]byte{0x5a}, 96)
	upload, _ := json.Marshal(hostedInvite{DeviceName: "Mom & Dad PC", Role: "ManagedOnly", ExpiresAt: time.Now().Add(14 * 24 * time.Hour), Ciphertext: ciphertext})
	adminPath := inviteAdminPrefix + hex.EncodeToString(idHash[:])
	put := signedRouteRequest(secret, http.MethodPut, adminPath, "put-nonce-012345678901234", upload)
	putResult := httptest.NewRecorder(); g.ServeHTTP(putResult, put)
	if putResult.Code != http.StatusCreated { t.Fatalf("PUT returned %d: %s", putResult.Code, putResult.Body.String()) }

	landingResult := httptest.NewRecorder(); g.ServeHTTP(landingResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID, nil))
	if landingResult.Code != http.StatusOK { t.Fatalf("landing returned %d", landingResult.Code) }
	landing := landingResult.Body.String()
	if !strings.Contains(landing, "Mom &amp; Dad PC") || !strings.Contains(landing, "opticon-bootstrap-1.0.0.exe") || strings.Contains(landing, ".cmd") || strings.Contains(landing, "ExecutionPolicy Bypass") {
		t.Fatal("landing page did not offer the signed bootstrap safely")
	}
	if strings.Contains(landing, "private-fragment-test") { t.Fatal("landing page leaked a fragment key") }

	bundleResult := httptest.NewRecorder(); g.ServeHTTP(bundleResult, httptest.NewRequest(http.MethodGet, artifactPrefix+bundle.File, nil))
	if bundleResult.Code != http.StatusOK || !bytes.Equal(bundleResult.Body.Bytes(), bundleBytes) {
		t.Fatal("landing page bundle was not downloadable from finalized storage")
	}
	downloadResult := httptest.NewRecorder(); g.ServeHTTP(downloadResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID+"/invite.tdinvite", nil))
	if downloadResult.Code != http.StatusOK || !bytes.Equal(downloadResult.Body.Bytes(), ciphertext) { t.Fatal("encrypted invitation download changed") }

	deleteRequest := signedRouteRequest(secret, http.MethodDelete, adminPath, "delete-nonce-0123456789012", nil)
	deleteResult := httptest.NewRecorder(); g.ServeHTTP(deleteResult, deleteRequest)
	if deleteResult.Code != http.StatusNoContent { t.Fatalf("DELETE returned %d", deleteResult.Code) }
	missingResult := httptest.NewRecorder(); g.ServeHTTP(missingResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID, nil))
	if missingResult.Code != http.StatusNotFound { t.Fatal("deleted invitation remained public") }
}

func TestBundleForRoleSelectsHighestSemanticVersion(t *testing.T) {
	root := t.TempDir()
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	hash := strings.Repeat("a", 64)
	prior := bundleArtifact{Product: "OpticonBundle", Version: "1.9.9", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.9.9-managed-win-x64.zip", Size: 10, SHA256: hash}
	candidate := bundleArtifact{Product: "OpticonBundle", Version: "1.10.0-rc.2", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.10.0-rc.2-managed-win-x64.zip", Size: 10, SHA256: hash}
	current := bundleArtifact{Product: "OpticonBundle", Version: "1.10.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.10.0-managed-win-x64.zip", Size: 10, SHA256: hash}
	pending := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-2.0.0-managed-win-x64.zip", Size: 10, SHA256: hash}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{
		prior, candidate, current, pending,
		{Product: "OpticonBundle", Version: "01.99.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-01.99.0-managed-win-x64.zip", Size: 10, SHA256: hash},
		{Product: "OpticonBundle", Version: "99.0.0", Role: "ManagedOnly", Architecture: "arm64", File: "opticon-bundle-99.0.0-managed-win-arm64.zip", Size: 10, SHA256: hash},
	}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	for _, artifact := range []bundleArtifact{prior, candidate, current} {
		if err := os.WriteFile(filepath.Join(bundleDir, artifact.File), bytes.Repeat([]byte{'x'}, 10), 0444); err != nil { t.Fatal(err) }
	}
	selected, err := (&gateway{artifactDir: root, bundleDir: bundleDir}).bundleForRole("ManagedOnly")
	if err != nil { t.Fatal(err) }
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
		valid bool
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

func TestCloudFrontBundleURLIsStrictAndDoesNotNeedFlyVolume(t *testing.T) {
	artifact := bundleArtifact{
		Product: "OpticonBundle", Version: "1.2.3", Role: "ManagedOnly", Architecture: "x64",
		File: "opticon-bundle-1.2.3-managed-win-x64.zip", Size: 1024, SHA256: strings.Repeat("a", 64),
		DownloadURL: "https://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
	}
	if !validCloudFrontDownloadURL(artifact) { t.Fatal("valid immutable CloudFront URL was rejected") }
	g := &gateway{bundleDir: t.TempDir()}
	if !g.bundleIsAvailable(artifact) { t.Fatal("CloudFront artifact incorrectly required a Fly-volume copy") }
	if url := bundleDownloadURL("https://control.example.test", artifact); url != artifact.DownloadURL {
		t.Fatalf("installer retained Fly URL instead of CloudFront URL: %s", url)
	}
	for _, unsafe := range []string{
		"http://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
		"https://user:secret@d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
		"https://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/other.zip",
		"https://evil.example.test/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip",
		"https://d111111abcdef8.cloudfront.net/opticon/releases/1.2.3/opticon-bundle-1.2.3-managed-win-x64.zip#fragment",
	} {
		candidate := artifact; candidate.DownloadURL = unsafe
		if validCloudFrontDownloadURL(candidate) { t.Fatalf("unsafe URL accepted: %s", unsafe) }
	}
}

func TestBundleForRoleRejectsAmbiguousEquivalentRelease(t *testing.T) {
	root := t.TempDir()
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	hash := strings.Repeat("b", 64)
	first := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-2.0.0-first-managed-win-x64.zip", Size: 10, SHA256: hash}
	second := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-2.0.0-second-managed-win-x64.zip", Size: 10, SHA256: hash}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{first, second}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	for _, artifact := range []bundleArtifact{first, second} {
		if err := os.WriteFile(filepath.Join(bundleDir, artifact.File), bytes.Repeat([]byte{'x'}, 10), 0444); err != nil { t.Fatal(err) }
	}
	if _, err := (&gateway{artifactDir: root, bundleDir: bundleDir}).bundleForRole("ManagedOnly"); err == nil {
		t.Fatal("precedence-equivalent conflicting releases were accepted")
	}
}

func TestDuplicateBundleFilenameFailsBeforeServingSelectionOrUpload(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	payload := []byte("duplicate bundle fixture")
	digest := sha256.Sum256(payload)
	fileName := "opticon-bundle-duplicate-win-x64.zip"
	first := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: fileName, Size: int64(len(payload)), SHA256: hex.EncodeToString(digest[:])}
	second := bundleArtifact{Product: "OpticonBundle", Version: "2.0.0", Role: "ControllerAndManaged", Architecture: "x64", File: fileName, Size: int64(len(payload)), SHA256: hex.EncodeToString(digest[:])}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{first, second}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	if err := os.WriteFile(filepath.Join(bundleDir, fileName), payload, 0444); err != nil { t.Fatal(err) }
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, artifactDir: artifactDir, bundleDir: bundleDir, nonces: make(map[string]time.Time)}
	if _, err := g.readArtifactManifest(); err == nil { t.Fatal("duplicate bundle filename passed central manifest validation") }
	if _, err := g.bundleForRole("ManagedOnly"); err == nil { t.Fatal("duplicate bundle filename reached role selection") }
	if _, err := g.bundleByFile(fileName); err == nil { t.Fatal("duplicate bundle filename reached file selection") }

	public := httptest.NewRecorder(); g.ServeHTTP(public, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if public.Code != http.StatusServiceUnavailable { t.Fatalf("duplicate manifest was served with status %d", public.Code) }
	uploadPath := bundleAdminPrefix + fileName + "?offset=0&total=" + strconv.FormatInt(first.Size, 10) + "&sha256=" + first.SHA256
	upload := signedRouteRequest(secret, http.MethodPut, uploadPath, "duplicate-upload-nonce-0123", payload)
	uploadResult := httptest.NewRecorder(); g.ServeHTTP(uploadResult, upload)
	if uploadResult.Code != http.StatusNotFound { t.Fatalf("duplicate manifest authorized upload with status %d", uploadResult.Code) }
	if _, err := os.Stat(filepath.Join(bundleDir, fileName+".upload")); !os.IsNotExist(err) {
		t.Fatal("duplicate manifest created an upload staging file")
	}
}

func TestBundleUploadRequiresHMACAndManifestPins(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	payload := []byte("a signed reusable Opticon bundle")
	digest := sha256.Sum256(payload)
	artifact := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-test.zip", Size: int64(len(payload)), SHA256: hex.EncodeToString(digest[:])}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{artifact}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, artifactDir: artifactDir, bundleDir: bundleDir, nonces: make(map[string]time.Time)}
	path := bundleAdminPrefix + artifact.File + "?offset=0&total=" + strconv.FormatInt(artifact.Size, 10) + "&sha256=" + artifact.SHA256
	unsignedResult := httptest.NewRecorder(); g.ServeHTTP(unsignedResult, httptest.NewRequest(http.MethodPut, path, bytes.NewReader(payload)))
	if unsignedResult.Code != http.StatusUnauthorized { t.Fatal("unsigned bundle upload was accepted") }
	signed := signedRouteRequest(secret, http.MethodPut, path, "bundle-nonce-0123456789012", payload)
	result := httptest.NewRecorder(); g.ServeHTTP(result, signed)
	if result.Code != http.StatusCreated { t.Fatalf("bundle upload returned %d: %s", result.Code, result.Body.String()) }
	stored, err := os.ReadFile(filepath.Join(bundleDir, artifact.File))
	if err != nil || !bytes.Equal(stored, payload) { t.Fatal("stored bundle differs from hash-pinned upload") }
	declaredDelete := signedRouteRequest(secret, http.MethodDelete, bundleAdminPrefix+artifact.File, "declared-delete-nonce-012345", nil)
	declaredResult := httptest.NewRecorder(); g.ServeHTTP(declaredResult, declaredDelete)
	if declaredResult.Code != http.StatusConflict { t.Fatal("declared bundle deletion was accepted") }
	partialPath := filepath.Join(bundleDir, artifact.File+".upload")
	if err := os.WriteFile(partialPath, []byte("partial"), 0600); err != nil { t.Fatal(err) }
	resetUpload := signedRouteRequest(secret, http.MethodDelete, bundleAdminPrefix+artifact.File+"?upload=true", "upload-reset-nonce-012345", nil)
	resetResult := httptest.NewRecorder(); g.ServeHTTP(resetResult, resetUpload)
	if resetResult.Code != http.StatusNoContent { t.Fatalf("partial upload reset returned %d", resetResult.Code) }
	if _, err := os.Stat(partialPath); !os.IsNotExist(err) { t.Fatal("partial upload remained on disk") }
	if stored, err := os.ReadFile(filepath.Join(bundleDir, artifact.File)); err != nil || !bytes.Equal(stored, payload) { t.Fatal("partial upload reset damaged final bundle") }
	manifest, _ = json.Marshal(artifactManifest{})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	obsoleteDelete := signedRouteRequest(secret, http.MethodDelete, bundleAdminPrefix+artifact.File, "obsolete-delete-nonce-01234", nil)
	obsoleteResult := httptest.NewRecorder(); g.ServeHTTP(obsoleteResult, obsoleteDelete)
	if obsoleteResult.Code != http.StatusNoContent { t.Fatalf("obsolete bundle deletion returned %d", obsoleteResult.Code) }
	if _, err := os.Stat(filepath.Join(bundleDir, artifact.File)); !os.IsNotExist(err) { t.Fatal("obsolete bundle remained on disk") }
}

func TestPruneUndeclaredBundlesPreservesCurrentArtifacts(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	current := "opticon-bundle-current-managed-win-x64.zip"
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{{Product: "OpticonBundle", File: current}}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	for _, name := range []string{current, current + ".upload", "opticon-bundle-old-managed-win-x64.zip", "opticon-bundle-old-controller-win-x64.zip.upload", "unrelated.data"} {
		if err := os.WriteFile(filepath.Join(bundleDir, name), []byte(name), 0600); err != nil { t.Fatal(err) }
	}
	g := &gateway{artifactDir: artifactDir, bundleDir: bundleDir}
	if err := g.pruneUndeclaredBundles(); err != nil { t.Fatal(err) }
	for _, name := range []string{current, current + ".upload", "unrelated.data"} {
		if _, err := os.Stat(filepath.Join(bundleDir, name)); err != nil { t.Fatalf("current or unrelated file %q was removed: %v", name, err) }
	}
	for _, name := range []string{"opticon-bundle-old-managed-win-x64.zip", "opticon-bundle-old-controller-win-x64.zip.upload"} {
		if _, err := os.Stat(filepath.Join(bundleDir, name)); !os.IsNotExist(err) { t.Fatalf("obsolete bundle %q was not removed", name) }
	}
}

func TestPruneRejectsUnknownManifestSchemaBeforeDeleting(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	name := "opticon-bundle-current-managed-win-x64.zip"
	path := filepath.Join(bundleDir, name)
	if err := os.WriteFile(path, []byte("preserve me"), 0600); err != nil { t.Fatal(err) }
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 2})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	if err := (&gateway{artifactDir: artifactDir, bundleDir: bundleDir}).pruneUndeclaredBundles(); err == nil {
		t.Fatal("unknown manifest schema was accepted for destructive pruning")
	}
	if _, err := os.Stat(path); err != nil { t.Fatalf("bundle was deleted before manifest validation: %v", err) }
}
func TestArtifactRejectsUndeclaredOrWrongSizedBundle(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	name := "opticon-bundle-stale.zip"
	if err := os.WriteFile(filepath.Join(bundleDir, name), []byte("stale"), 0600); err != nil { t.Fatal(err) }
	manifest, _ := json.Marshal(artifactManifest{})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	g := &gateway{artifactDir: artifactDir, bundleDir: bundleDir}
	result := httptest.NewRecorder(); g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+name, nil))
	if result.Code != http.StatusNotFound { t.Fatal("undeclared stale bundle remained downloadable") }

	digest := sha256.Sum256([]byte("stale"))
	declared := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: name, Size: 999, SHA256: hex.EncodeToString(digest[:])}
	manifest, _ = json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{declared}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	result = httptest.NewRecorder(); g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+name, nil))
	if result.Code != http.StatusNotFound { t.Fatal("wrong-sized bundle remained downloadable") }
}

func TestPublicManifestOnlyAdvertisesFinalizedBundlesAndPreservesPins(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	hash := strings.Repeat("c", 64)
	dependency := bundleArtifact{Product: "Tailscale", Version: "1.2.3", Architecture: "x64", File: "tailscale.msi", Size: 50, SHA256: hash, SignerThumbprint: "PINNED-SIGNER"}
	ready := bundleArtifact{Product: "OpticonBundle", Version: "1.1.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.1.0-managed-win-x64.zip", Size: 16, SHA256: hash}
	pending := bundleArtifact{Product: "OpticonBundle", Version: "1.2.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-1.2.0-managed-win-x64.zip", Size: 16, SHA256: hash}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{dependency, ready, pending}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	if err := os.WriteFile(filepath.Join(bundleDir, ready.File), bytes.Repeat([]byte{'r'}, 16), 0444); err != nil { t.Fatal(err) }
	g := &gateway{artifactDir: artifactDir, bundleDir: bundleDir}

	get := httptest.NewRecorder(); g.ServeHTTP(get, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if get.Code != http.StatusOK { t.Fatalf("manifest returned %d: %s", get.Code, get.Body.String()) }
	if get.Header().Get("Cache-Control") != "no-store, max-age=0" { t.Fatal("public manifest was cacheable") }
	var published artifactManifest
	if err := json.Unmarshal(get.Body.Bytes(), &published); err != nil { t.Fatal(err) }
	if published.SchemaVersion != 1 { t.Fatalf("schema version changed to %d", published.SchemaVersion) }
	if len(published.Artifacts) != 2 { t.Fatalf("published %d records instead of dependency plus finalized bundle", len(published.Artifacts)) }
	if published.Artifacts[0].SignerThumbprint != dependency.SignerThumbprint { t.Fatal("dependency signer pin was stripped") }
	if published.Artifacts[1].File != ready.File { t.Fatalf("unexpected bundle was published: %s", published.Artifacts[1].File) }
	if strings.Contains(get.Body.String(), pending.File) { t.Fatal("not-yet-finalized bundle was advertised") }

	head := httptest.NewRecorder(); g.ServeHTTP(head, httptest.NewRequest(http.MethodHead, artifactPrefix+"manifest.json", nil))
	if head.Code != http.StatusOK || head.Body.Len() != 0 || head.Header().Get("Content-Length") != get.Header().Get("Content-Length") {
		t.Fatal("manifest HEAD response did not preserve GET metadata without a body")
	}
	if err := os.WriteFile(filepath.Join(bundleDir, pending.File), bytes.Repeat([]byte{'p'}, 15), 0600); err != nil { t.Fatal(err) }
	wrongSize := httptest.NewRecorder(); g.ServeHTTP(wrongSize, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if strings.Contains(wrongSize.Body.String(), pending.File) { t.Fatal("wrong-sized bundle was advertised") }
	if err := os.WriteFile(filepath.Join(bundleDir, pending.File), bytes.Repeat([]byte{'p'}, 16), 0444); err != nil { t.Fatal(err) }
	finalized := httptest.NewRecorder(); g.ServeHTTP(finalized, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if !strings.Contains(finalized.Body.String(), pending.File) { t.Fatal("finalized bundle did not become visible") }
}

func TestUnsafeBundleFilenameCannotReachInstallerCommand(t *testing.T) {
	root := t.TempDir()
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	unsafeName := "opticon-bundle-x';Start-Process calc;#.zip"
	artifact := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: unsafeName, Size: 10, SHA256: strings.Repeat("d", 64)}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{artifact}})
	if err := os.WriteFile(filepath.Join(root, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	if err := os.WriteFile(filepath.Join(bundleDir, unsafeName), bytes.Repeat([]byte{'x'}, 10), 0600); err != nil { t.Fatal(err) }
	g := &gateway{artifactDir: root, bundleDir: bundleDir}
	if _, err := g.bundleForRole("ManagedOnly"); err == nil { t.Fatal("unsafe bundle filename was selected") }
	result := httptest.NewRecorder(); g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+"manifest.json", nil))
	if result.Code != http.StatusOK || strings.Contains(result.Body.String(), unsafeName) { t.Fatal("unsafe bundle filename was publicly advertised") }
	command := buildInstallerCommand("https://opticon.example.test", strings.Repeat("B", 24), artifact)
	escapedURL := powerShellSingleQuoted("https://opticon.example.test" + artifactPrefix + url.PathEscape(unsafeName))
	if !strings.Contains(command, "Get-OpticonFile '"+escapedURL+"' $bundle") {
		t.Fatal("bundle URL was not path-encoded and contained as one PowerShell string literal")
	}
}

func TestMigrateBundleUploadsRequiresManifestSizeAndHash(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	stagingDir := filepath.Join(root, "staging")
	bundleDir := filepath.Join(root, "bundles")
	for _, directory := range []string{artifactDir, stagingDir, bundleDir} {
		if err := os.MkdirAll(directory, 0700); err != nil { t.Fatal(err) }
	}
	goodBytes := []byte("verified legacy upload")
	goodHash := sha256.Sum256(goodBytes)
	badBytes := []byte("tampered legacy upload")
	badHash := sha256.Sum256([]byte("different signed bytes"))
	good := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-good-managed-win-x64.zip", Size: int64(len(goodBytes)), SHA256: hex.EncodeToString(goodHash[:])}
	bad := bundleArtifact{Product: "OpticonBundle", Version: "1.0.0", Role: "ControllerAndManaged", Architecture: "x64", File: "opticon-bundle-bad-controller-win-x64.zip", Size: int64(len(badBytes)), SHA256: hex.EncodeToString(badHash[:])}
	manifest, _ := json.Marshal(artifactManifest{SchemaVersion: 1, Artifacts: []bundleArtifact{good, bad}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	if err := os.WriteFile(filepath.Join(stagingDir, good.File+".upload"), goodBytes, 0600); err != nil { t.Fatal(err) }
	if err := os.WriteFile(filepath.Join(stagingDir, bad.File+".upload"), badBytes, 0600); err != nil { t.Fatal(err) }
	if err := migrateBundleUploads(stagingDir, artifactDir, bundleDir); err != nil { t.Fatal(err) }
	stored, err := os.ReadFile(filepath.Join(bundleDir, good.File))
	if err != nil || !bytes.Equal(stored, goodBytes) { t.Fatal("verified legacy upload was not migrated") }
	if _, err := os.Stat(filepath.Join(bundleDir, bad.File)); !os.IsNotExist(err) { t.Fatal("hash-mismatched legacy upload became final") }
	if _, err := os.Stat(filepath.Join(stagingDir, bad.File+".upload")); !os.IsNotExist(err) { t.Fatal("invalid legacy upload was not discarded") }
}

func TestInstallerCommandPinsBundleAndSetupSigner(t *testing.T) {
	bundle := bundleArtifact{File: "opticon-bundle-test-managed-win-x64.zip", Size: 12345, SHA256: strings.Repeat("a", 64)}
	command := buildInstallerCommand("https://opticon.example.test", strings.Repeat("B", 24), bundle)
	for _, expected := range []string{"12345", bundle.SHA256, "FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53", "__OPTICON_FRAGMENT_KEY__", "https://opticon.example.test/opticon/i/"} {
		if !strings.Contains(command, expected) { t.Fatalf("installer command omitted %s", expected) }
	}
}
func TestInstallerCommandPrefersCurlAndRetainsWebRequestFallback(t *testing.T) {
	bundle := bundleArtifact{File: "opticon-bundle-test-managed-win-x64.zip", Size: 12345, SHA256: strings.Repeat("a", 64)}
	command := buildInstallerCommand("https://opticon.example.test", strings.Repeat("B", 24), bundle)
	for _, expected := range []string{"Get-Command curl.exe", "--retry 3", "$ProgressPreference='SilentlyContinue'", "Invoke-WebRequest"} {
		if !strings.Contains(command, expected) { t.Fatalf("installer command omitted download behavior %s", expected) }
	}
	if strings.Index(command, "Get-Command curl.exe") > strings.Index(command, "Invoke-WebRequest") {
		t.Fatal("installer command does not prefer curl before Invoke-WebRequest")
	}
}
func TestPublicRoutesExcludeAdminAndHelperPages(t *testing.T) {
	for _, path := range []string{"/api/v1/node", "/swagger", "/version", "/apple", "/windows", "/register/abc", "/auth/abc", "/"} {
		if isPublicControlRoute(http.MethodGet, path) { t.Fatalf("helper/admin route became public: %s", path) }
	}
	for _, path := range []string{"/key", "/ts2021", "/machine/map", "/derp", "/bootstrap-dns"} {
		if !isPublicControlRoute(http.MethodGet, path) { t.Fatalf("required control route was blocked: %s", path) }
	}
}

func TestAdminAllowlist(t *testing.T) {
	allowed := [][2]string{{"GET", "api/v1/node"}, {"POST", "api/v1/preauthkey"}, {"POST", "api/v1/node/7/tags"}, {"DELETE", "api/v1/node/7"}}
	for _, item := range allowed { if !isAllowedAdminRoute(item[0], item[1]) { t.Fatalf("expected allowed: %v", item) } }
	for _, path := range []string{"api/v1/apikey", "api/v1/user", "api/v1/policy", "swagger"} {
		if isAllowedAdminRoute(http.MethodGet, path) || isAllowedAdminRoute(http.MethodPost, path) { t.Fatalf("unexpected admin route: %s", path) }
	}
}

func TestHMACRejectsReplayAndStaleTimestamp(t *testing.T) {
	now := time.Unix(1_800_000_000, 0)
	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, nonces: make(map[string]time.Time)}
	body := []byte(`{"user":"1"}`)
	r := signedRequest(secret, now, "fresh-nonce-012345678901234", body)
	if !g.authenticate(r, body, now) { t.Fatal("valid HMAC was rejected") }
	if g.authenticate(r, body, now) { t.Fatal("replayed nonce was accepted") }
	stale := signedRequest(secret, now.Add(-10*time.Minute), "stale-nonce-012345678901234", body)
	if g.authenticate(stale, body, now) { t.Fatal("stale HMAC was accepted") }
}

func signedRequest(secret []byte, timestamp time.Time, nonce string, body []byte) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "https://example.test/opticon/v1/headscale/api/v1/preauthkey", strings.NewReader(string(body)))
	hash := sha256.Sum256(body)
	hashText := hex.EncodeToString(hash[:])
	timeText := strconv.FormatInt(timestamp.Unix(), 10)
	canonical := strings.Join([]string{r.Method, r.URL.RequestURI(), timeText, nonce, hashText}, "\n")
	signature := hmac.New(sha256.New, secret); _, _ = signature.Write([]byte(canonical))
	r.Header.Set("X-Opticon-Key-Id", "primary")
	r.Header.Set("X-Opticon-Timestamp", timeText)
	r.Header.Set("X-Opticon-Nonce", nonce)
	r.Header.Set("X-Opticon-Content-SHA256", hashText)
	r.Header.Set("X-Opticon-Signature", hex.EncodeToString(signature.Sum(nil)))
	return r
}

func signedRouteRequest(secret []byte, method, path, nonce string, body []byte) *http.Request {
	if body == nil { body = []byte{} }
	r := httptest.NewRequest(method, "https://example.test"+path, bytes.NewReader(body))
	hash := sha256.Sum256(body)
	hashText := hex.EncodeToString(hash[:])
	timeText := strconv.FormatInt(time.Now().Unix(), 10)
	canonical := strings.Join([]string{method, r.URL.RequestURI(), timeText, nonce, hashText}, "\n")
	signature := hmac.New(sha256.New, secret); _, _ = signature.Write([]byte(canonical))
	r.Header.Set("X-Opticon-Key-Id", "primary")
	r.Header.Set("X-Opticon-Timestamp", timeText)
	r.Header.Set("X-Opticon-Nonce", nonce)
	r.Header.Set("X-Opticon-Content-SHA256", hashText)
	r.Header.Set("X-Opticon-Signature", hex.EncodeToString(signature.Sum(nil)))
	return r
}
