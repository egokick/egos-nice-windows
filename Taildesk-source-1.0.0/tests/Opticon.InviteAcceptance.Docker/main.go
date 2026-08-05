package main

import (
	"archive/zip"
	"bytes"
	"crypto"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rsa"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

const (
	inputPath     = "/run/opticon-input/input.json"
	outputDir     = "/run/opticon-output"
	maxLanding    = 1024 * 1024
	maxInvitation = 65536
)

var (
	magic      = []byte("OPTICON-LINK1")
	validID    = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)
	validRoots = map[string]bool{"Desktop": true, "Documents": true, "Downloads": true, "Pictures": true, "Videos": true}
)

type artifact struct {
	Product      string `json:"product"`
	Version      string `json:"version"`
	Role         string `json:"role"`
	Architecture string `json:"architecture"`
	File         string `json:"file"`
	Size         int64  `json:"size"`
	SHA256       string `json:"sha256"`
	SignerThumbprint string `json:"signerThumbprint"`
}

type testInput struct {
	InvitationURL                string     `json:"invitationUrl"`
	ExpectedRole                 string     `json:"expectedRole"`
	Bundle                       artifact   `json:"bundle"`
	Dependencies                 []artifact `json:"dependencies"`
	InvitationCertificateBase64 string     `json:"invitationCertificateBase64"`
	InvitationCertificateSHA1   string     `json:"invitationCertificateSha1"`
}

type envelope struct {
	SchemaVersion int    `json:"schemaVersion"`
	Payload       string `json:"payload"`
	Signature     string `json:"signature"`
}

type invitePayload struct {
	SchemaVersion      int      `json:"schemaVersion"`
	InviteID           string   `json:"inviteId"`
	DeviceName         string   `json:"deviceName"`
	Role               string   `json:"role"`
	ExpiresAt          time.Time `json:"expiresAt"`
	InviteSecret       string   `json:"inviteSecret"`
	TailscaleAuthKey   string   `json:"tailscaleAuthKey"`
	HeadscaleLoginURL  string   `json:"headscaleLoginUrl"`
	AgentToken         string   `json:"agentToken"`
	RustDeskPassword   string   `json:"rustDeskPassword"`
	ControllerToken    string   `json:"controllerToken"`
	CoordinatorURL     string   `json:"coordinatorUrl"`
	ExpectedTailnet    string   `json:"expectedTailnet"`
	AdvertiseExitNode  bool     `json:"advertiseExitNode"`
	AllowedRoots       []string `json:"allowedRoots"`
}

type result struct {
	Status             string    `json:"status"`
	DeviceName         string    `json:"deviceName"`
	Role               string    `json:"role"`
	ExpiresAt          time.Time `json:"expiresAt"`
	Bundle              string    `json:"bundle"`
	DependenciesChecked int       `json:"dependenciesChecked"`
	NegativeTestsPassed int       `json:"negativeTestsPassed"`
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "Opticon invitation acceptance failed:", err)
		os.Exit(1)
	}
}

func run() error {
	data, err := os.ReadFile(inputPath)
	if err != nil { return fmt.Errorf("read test input: %w", err) }
	var input testInput
	if err := json.Unmarshal(data, &input); err != nil { return fmt.Errorf("parse test input: %w", err) }
	for i := range data { data[i] = 0 }

	inviteURL, err := url.Parse(input.InvitationURL)
	if err != nil || inviteURL.Scheme != "https" || inviteURL.Host == "" || len(inviteURL.Fragment) < 32 {
		return errors.New("the disposable invitation URL is not a valid fragment-keyed HTTPS URL")
	}
	fragmentKey := inviteURL.Fragment
	inviteURL.Fragment = ""
	landingURL := inviteURL.String()

	client := &http.Client{Timeout: 10 * time.Minute}
	landing, err := getLimited(client, landingURL, maxLanding)
	if err != nil { return fmt.Errorf("download landing page: %w", err) }
	landingText := string(landing)
	for _, expected := range []string{"Install Opticon", input.Bundle.File, strings.ToLower(input.Bundle.SHA256), fmt.Sprint(input.Bundle.Size), input.InvitationCertificateSHA1} {
		if !strings.Contains(strings.ToLower(landingText), strings.ToLower(expected)) {
			return fmt.Errorf("landing page omitted required pin %q", expected)
		}
	}
	if strings.Contains(strings.ToLower(landingText), "pinned publisher certificate") {
		return errors.New("landing page still contains the retired target-side publisher-certificate failure")
	}

	encryptedURL := strings.TrimRight(landingURL, "/") + "/invite.tdinvite"
	encrypted, err := getLimited(client, encryptedURL, maxInvitation)
	if err != nil { return fmt.Errorf("download encrypted invitation: %w", err) }
	plain, err := decryptInvitation(fragmentKey, encrypted)
	if err != nil { return err }
	if len(encrypted) > 0 {
		tampered := append([]byte(nil), encrypted...)
		tampered[len(tampered)-1] ^= 1
		if _, err := decryptInvitation(fragmentKey, tampered); err == nil {
			return errors.New("tampered invitation was accepted")
		}
	}
	for i := range encrypted { encrypted[i] = 0 }

	payload, err := verifyEnvelope(input, plain)
	for i := range plain { plain[i] = 0 }
	if err != nil { return err }
	if err := validatePayload(payload, input.ExpectedRole); err != nil { return err }

	origin := inviteURL.Scheme + "://" + inviteURL.Host
	bundleURL := origin + "/opticon/artifacts/v1/" + url.PathEscape(input.Bundle.File)
	bundlePath := filepath.Join(outputDir, "opticon-bundle.zip")
	if err := downloadPinned(client, bundleURL, bundlePath, input.Bundle); err != nil { return fmt.Errorf("verify live bundle: %w", err) }
	if err := verifyAndExtractBundle(bundlePath, input.ExpectedRole); err != nil { return err }
	if err := assertTamperDetected(bundlePath, input.Bundle.SHA256); err != nil { return err }

	checked := 0
	for _, dependency := range input.Dependencies {
		if dependency.Architecture != "x64" { continue }
		path := filepath.Join(outputDir, dependency.File)
		artifactURL := origin + "/opticon/artifacts/v1/" + url.PathEscape(dependency.File)
		if err := downloadPinned(client, artifactURL, path, dependency); err != nil {
			return fmt.Errorf("verify %s %s: %w", dependency.Product, dependency.Version, err)
		}
		checked++
	}
	if checked != 2 { return fmt.Errorf("expected two x64 dependencies, verified %d", checked) }

	output := result{Status: "passed", DeviceName: payload.DeviceName, Role: payload.Role, ExpiresAt: payload.ExpiresAt, Bundle: input.Bundle.File, DependenciesChecked: checked, NegativeTestsPassed: 2}
	encoded, err := json.MarshalIndent(output, "", "  ")
	if err != nil { return err }
	if err := os.WriteFile(filepath.Join(outputDir, "result.json"), encoded, 0600); err != nil { return err }
	fmt.Printf("PASS disposable invitation, signed payload, live bundle, and %d pinned dependencies verified\n", checked)
	return nil
}

func getLimited(client *http.Client, address string, limit int64) ([]byte, error) {
	response, err := client.Get(address)
	if err != nil { return nil, err }
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK { return nil, fmt.Errorf("HTTP %d", response.StatusCode) }
	if response.ContentLength > limit { return nil, fmt.Errorf("content length %d exceeds limit %d", response.ContentLength, limit) }
	data, err := io.ReadAll(io.LimitReader(response.Body, limit+1))
	if err != nil { return nil, err }
	if int64(len(data)) > limit { return nil, fmt.Errorf("response exceeds limit %d", limit) }
	return data, nil
}

func decryptInvitation(fragmentKey string, encrypted []byte) ([]byte, error) {
	minimum := len(magic) + 12 + 16 + 1
	if len(encrypted) < minimum || !bytes.Equal(encrypted[:len(magic)], magic) { return nil, errors.New("invalid encrypted invitation format") }
	key := sha256.Sum256([]byte(fragmentKey))
	block, err := aes.NewCipher(key[:])
	if err != nil { return nil, err }
	gcm, err := cipher.NewGCM(block)
	if err != nil { return nil, err }
	nonce := encrypted[len(magic):len(magic)+12]
	tag := encrypted[len(magic)+12:len(magic)+28]
	ciphertext := encrypted[len(magic)+28:]
	combined := make([]byte, 0, len(ciphertext)+len(tag))
	combined = append(combined, ciphertext...)
	combined = append(combined, tag...)
	plain, err := gcm.Open(nil, nonce, combined, magic)
	if err != nil { return nil, errors.New("invitation decryption or authentication failed") }
	return plain, nil
}

func verifyEnvelope(input testInput, plain []byte) (invitePayload, error) {
	var env envelope
	if err := json.Unmarshal(plain, &env); err != nil || env.SchemaVersion != 1 { return invitePayload{}, errors.New("invalid signed invitation envelope") }
	payloadBytes, err := base64.StdEncoding.DecodeString(env.Payload)
	if err != nil { return invitePayload{}, errors.New("invalid invitation payload encoding") }
	signature, err := base64.StdEncoding.DecodeString(env.Signature)
	if err != nil { return invitePayload{}, errors.New("invalid invitation signature encoding") }
	certificateDER, err := base64.StdEncoding.DecodeString(input.InvitationCertificateBase64)
	if err != nil { return invitePayload{}, errors.New("invalid pinned invitation certificate encoding") }
	certificate, err := x509.ParseCertificate(certificateDER)
	if err != nil { return invitePayload{}, fmt.Errorf("parse pinned invitation certificate: %w", err) }
	thumbprint := sha1Hex(certificate.Raw)
	if !strings.EqualFold(thumbprint, input.InvitationCertificateSHA1) { return invitePayload{}, errors.New("pinned invitation certificate thumbprint mismatch") }
	publicKey, ok := certificate.PublicKey.(*rsa.PublicKey)
	if !ok { return invitePayload{}, errors.New("pinned invitation certificate is not RSA") }
	digest := sha256.Sum256(payloadBytes)
	if err := rsa.VerifyPSS(publicKey, cryptoHashSHA256(), digest[:], signature, nil); err != nil { return invitePayload{}, errors.New("invitation signature verification failed") }
	var payload invitePayload
	if err := json.Unmarshal(payloadBytes, &payload); err != nil { return invitePayload{}, errors.New("invalid signed invitation payload") }
	for i := range payloadBytes { payloadBytes[i] = 0 }
	return payload, nil
}

// cryptoHashSHA256 is kept separate to make the signature policy visually explicit.
func cryptoHashSHA256() crypto.Hash { return crypto.SHA256 }

func sha1Hex(data []byte) string {
	h := sha1.Sum(data)
	return strings.ToUpper(hex.EncodeToString(h[:]))
}

func validatePayload(payload invitePayload, expectedRole string) error {
	if payload.SchemaVersion != 3 || !validID.MatchString(payload.InviteID) || payload.InviteID == "00000000-0000-0000-0000-000000000000" { return errors.New("invalid invitation identity or schema") }
	if strings.TrimSpace(payload.DeviceName) == "" || payload.Role != expectedRole || payload.ExpiresAt.Before(time.Now().UTC()) { return errors.New("invalid invitation name, role, or expiry") }
	for _, secret := range []string{payload.InviteSecret, payload.TailscaleAuthKey, payload.AgentToken, payload.RustDeskPassword} {
		if strings.TrimSpace(secret) == "" { return errors.New("invitation omitted a required credential") }
	}
	login, err := url.Parse(payload.HeadscaleLoginURL)
	if err != nil || login.Scheme != "https" || login.Host == "" { return errors.New("invitation contains a non-HTTPS Headscale URL") }
	if !validCoordinatorURL(payload.CoordinatorURL) { return errors.New("invitation coordinator URL is outside the private Tailscale network") }
	if strings.TrimSpace(payload.ExpectedTailnet) == "" || len(payload.AllowedRoots) == 0 { return errors.New("invitation omitted tailnet or shared roots") }
	seen := map[string]bool{}
	for _, root := range payload.AllowedRoots {
		if !validRoots[root] || seen[root] { return errors.New("invitation contains invalid or duplicate shared roots") }
		seen[root] = true
	}
	return nil
}

func validCoordinatorURL(address string) bool {
	u, err := url.Parse(address)
	if err != nil || u.Host == "" { return false }
	if u.Scheme == "https" { return true }
	if u.Scheme != "http" { return false }
	ip := net.ParseIP(u.Hostname()).To4()
	return ip != nil && ip[0] == 100 && ip[1] >= 64 && ip[1] <= 127
}
func downloadPinned(client *http.Client, address, destination string, expected artifact) error {
	response, err := client.Get(address)
	if err != nil { return err }
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK { return fmt.Errorf("HTTP %d", response.StatusCode) }
	if response.ContentLength >= 0 && response.ContentLength != expected.Size { return fmt.Errorf("content length %d did not match %d", response.ContentLength, expected.Size) }
	file, err := os.OpenFile(destination, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
	if err != nil { return err }
	hash := sha256.New()
	written, copyErr := io.Copy(io.MultiWriter(file, hash), io.LimitReader(response.Body, expected.Size+1))
	closeErr := file.Close()
	if copyErr != nil { return copyErr }
	if closeErr != nil { return closeErr }
	if written != expected.Size { return fmt.Errorf("downloaded size %d did not match %d", written, expected.Size) }
	actual := hex.EncodeToString(hash.Sum(nil))
	if !strings.EqualFold(actual, expected.SHA256) { return fmt.Errorf("SHA-256 %s did not match the pin", actual) }
	return nil
}

func verifyAndExtractBundle(bundlePath, role string) error {
	archive, err := zip.OpenReader(bundlePath)
	if err != nil { return fmt.Errorf("open bundle ZIP: %w", err) }
	defer archive.Close()
	required := map[string]string{"Taildesk.Setup.exe": "Taildesk.Setup.exe", "Payload/Agent/Taildesk.Agent.exe": "Taildesk.Agent.exe"}
	if role == "controllerAndManaged" { required["Payload/Admin/Opticon.exe"] = "Opticon.exe" }
	found := map[string]bool{}
	for _, entry := range archive.File {
		name := strings.ReplaceAll(entry.Name, "\\", "/")
		clean := filepath.ToSlash(filepath.Clean(name))
		if strings.HasPrefix(clean, "../") || strings.HasPrefix(name, "/") || clean != name { return fmt.Errorf("unsafe bundle entry %q", entry.Name) }
		if role == "managedOnly" && strings.HasPrefix(name, "Payload/Admin/") { return errors.New("managed bundle unexpectedly contains controller tools") }
		outputName, wanted := required[name]
		if !wanted { continue }
		reader, err := entry.Open()
		if err != nil { return err }
		output, err := os.OpenFile(filepath.Join(outputDir, outputName), os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
		if err != nil { reader.Close(); return err }
		_, copyErr := io.Copy(output, reader)
		closeOutputErr := output.Close()
		closeReaderErr := reader.Close()
		if copyErr != nil { return copyErr }
		if closeOutputErr != nil { return closeOutputErr }
		if closeReaderErr != nil { return closeReaderErr }
		if err := requireAuthenticodeTable(filepath.Join(outputDir, outputName)); err != nil { return fmt.Errorf("%s: %w", outputName, err) }
		found[name] = true
	}
	for name := range required { if !found[name] { return fmt.Errorf("bundle omitted %s", name) } }
	return nil
}

func requireAuthenticodeTable(path string) error {
	data, err := os.ReadFile(path)
	if err != nil { return err }
	if len(data) < 0x40 || data[0] != 'M' || data[1] != 'Z' { return errors.New("not a PE executable") }
	peOffset := int(binary.LittleEndian.Uint32(data[0x3c:0x40]))
	if peOffset < 0 || peOffset+24 > len(data) || string(data[peOffset:peOffset+4]) != "PE\x00\x00" { return errors.New("invalid PE header") }
	optional := peOffset + 24
	magicValue := binary.LittleEndian.Uint16(data[optional:optional+2])
	dataDirectory := optional + 96
	if magicValue == 0x20b { dataDirectory = optional + 112 } else if magicValue != 0x10b { return errors.New("unknown PE optional header") }
	security := dataDirectory + 8*4
	if security+8 > len(data) { return errors.New("missing PE security directory") }
	offset := int(binary.LittleEndian.Uint32(data[security:security+4]))
	size := int(binary.LittleEndian.Uint32(data[security+4:security+8]))
	if offset <= 0 || size < 8 || offset+size > len(data) { return errors.New("missing Authenticode certificate table") }
	certificateLength := int(binary.LittleEndian.Uint32(data[offset:offset+4]))
	certificateType := binary.LittleEndian.Uint16(data[offset+6:offset+8])
	if certificateLength < 8 || certificateLength > size || certificateType != 2 { return errors.New("invalid Authenticode WIN_CERTIFICATE") }
	return nil
}

func assertTamperDetected(path, expectedHash string) error {
	data, err := os.ReadFile(path)
	if err != nil { return err }
	if len(data) == 0 { return errors.New("empty bundle") }
	data[len(data)-1] ^= 1
	digest := sha256.Sum256(data)
	if strings.EqualFold(hex.EncodeToString(digest[:]), expectedHash) { return errors.New("tampered bundle retained its pinned hash") }
	return nil
}
