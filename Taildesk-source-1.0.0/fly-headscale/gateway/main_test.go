package main

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
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
	if err := os.MkdirAll(inviteDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	bundleBytes := []byte("signed reusable bundle fixture")
	bundleHash := sha256.Sum256(bundleBytes)
	bundle := bundleArtifact{Product: "OpticonBundle", Role: "ManagedOnly", Architecture: "x64", File: "managed.zip", Size: int64(len(bundleBytes)), SHA256: hex.EncodeToString(bundleHash[:])}
	manifest, _ := json.Marshal(artifactManifest{Artifacts: []bundleArtifact{bundle}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	if err := os.WriteFile(filepath.Join(artifactDir, bundle.File), bundleBytes, 0600); err != nil { t.Fatal(err) }

	secret := []byte("0123456789abcdef0123456789abcdef")
	g := &gateway{adminSecret: secret, inviteDir: inviteDir, artifactDir: artifactDir, publicOrigin: "https://opticon.example.test", nonces: make(map[string]time.Time)}
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
	if !strings.Contains(landing, "Mom &amp; Dad PC") || !strings.Contains(landing, bundle.SHA256) { t.Fatal("landing page omitted escaped device or bundle pin") }
	if strings.Contains(landing, "private-fragment-test") { t.Fatal("landing page leaked a fragment key") }

	downloadResult := httptest.NewRecorder(); g.ServeHTTP(downloadResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID+"/invite.tdinvite", nil))
	if downloadResult.Code != http.StatusOK || !bytes.Equal(downloadResult.Body.Bytes(), ciphertext) { t.Fatal("encrypted invitation download changed") }

	deleteRequest := signedRouteRequest(secret, http.MethodDelete, adminPath, "delete-nonce-0123456789012", nil)
	deleteResult := httptest.NewRecorder(); g.ServeHTTP(deleteResult, deleteRequest)
	if deleteResult.Code != http.StatusNoContent { t.Fatalf("DELETE returned %d", deleteResult.Code) }
	missingResult := httptest.NewRecorder(); g.ServeHTTP(missingResult, httptest.NewRequest(http.MethodGet, invitePublicPrefix+publicID, nil))
	if missingResult.Code != http.StatusNotFound { t.Fatal("deleted invitation remained public") }
}

func TestBundleUploadRequiresHMACAndManifestPins(t *testing.T) {
	root := t.TempDir()
	artifactDir := filepath.Join(root, "artifacts")
	bundleDir := filepath.Join(root, "bundles")
	if err := os.MkdirAll(artifactDir, 0700); err != nil { t.Fatal(err) }
	if err := os.MkdirAll(bundleDir, 0700); err != nil { t.Fatal(err) }
	payload := []byte("a signed reusable Opticon bundle")
	digest := sha256.Sum256(payload)
	artifact := bundleArtifact{Product: "OpticonBundle", Role: "ManagedOnly", Architecture: "x64", File: "opticon-bundle-test.zip", Size: int64(len(payload)), SHA256: hex.EncodeToString(digest[:])}
	manifest, _ := json.Marshal(artifactManifest{Artifacts: []bundleArtifact{artifact}})
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
	declared := bundleArtifact{Product: "OpticonBundle", File: name, Size: 999, SHA256: hex.EncodeToString(digest[:])}
	manifest, _ = json.Marshal(artifactManifest{Artifacts: []bundleArtifact{declared}})
	if err := os.WriteFile(filepath.Join(artifactDir, "manifest.json"), manifest, 0600); err != nil { t.Fatal(err) }
	result = httptest.NewRecorder(); g.ServeHTTP(result, httptest.NewRequest(http.MethodGet, artifactPrefix+name, nil))
	if result.Code != http.StatusNotFound { t.Fatal("wrong-sized bundle remained downloadable") }
}
func TestInstallerCommandPinsBundleAndSetupSigner(t *testing.T) {
	bundle := bundleArtifact{File: "managed.zip", Size: 12345, SHA256: strings.Repeat("a", 64)}
	command := buildInstallerCommand("https://opticon.example.test", strings.Repeat("B", 24), bundle)
	for _, expected := range []string{"12345", bundle.SHA256, "FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53", "__OPTICON_FRAGMENT_KEY__", "https://opticon.example.test/opticon/i/"} {
		if !strings.Contains(command, expected) { t.Fatalf("installer command omitted %s", expected) }
	}
}
func TestInstallerCommandPrefersCurlAndRetainsWebRequestFallback(t *testing.T) {
	bundle := bundleArtifact{File: "managed.zip", Size: 12345, SHA256: strings.Repeat("a", 64)}
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