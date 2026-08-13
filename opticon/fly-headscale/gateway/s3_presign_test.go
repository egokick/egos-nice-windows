package main

import (
	"net/url"
	"strings"
	"testing"
	"time"
)

func TestLocalArtifactSourceDownloadSigner(t *testing.T) {
	hash := strings.Repeat("a", 64)
	source := productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.18", Architecture: "source",
		File: "opticon-source-1.2.18.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.18/opticon-source-1.2.18.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
		SourceLauncherFile: "opticon-source-launcher-1.2.18.exe", SourceLauncherSize: 1024,
		SourceLauncherSHA256: hash,
	})
	signer := &localArtifactSourceDownloadSigner{publicOrigin: "https://opticon-e2e.test:8443"}
	location, err := signer.Presign(source, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if location != "https://opticon-e2e.test:8443/opticon/artifacts/v1/opticon-source-1.2.18.zip" {
		t.Fatalf("unexpected local source location %q", location)
	}
	if _, err := requireLocalE2EPublicOrigin("http://opticon-e2e.test:8443"); err == nil {
		t.Fatal("an HTTP local origin was accepted")
	}
}

func TestS3SourceDownloadSignerIsExactAndShortLived(t *testing.T) {
	hash := strings.Repeat("a", 64)
	source := productionArtifact(bundleArtifact{
		Product: "OpticonSource", Version: "1.2.0", Architecture: "source",
		File: "opticon-source-1.2.0.zip", Size: 2048, SHA256: hash,
		DownloadURL: "https://d111.cloudfront.net/opticon/releases/1.2.0/opticon-source-1.2.0.zip",
		SDKVersion:  pinnedSDKVersion, RuntimeVersion: pinnedRuntimeVersion,
		TargetRuntimes: []string{"win-x64", "win-arm64"}, SourceManifestSHA256: hash,
	})
	signer := &s3SourceDownloadSigner{
		bucket: "opticon-test-bucket", region: "us-east-1",
		accessKeyID: "AKIAIOSFODNN7EXAMPLE", secretKey: "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
		sessionToken: "test-session-token",
	}
	presigned, err := signer.Presign(source, time.Date(2026, time.August, 10, 21, 30, 0, 0, time.UTC))
	if err != nil {
		t.Fatal(err)
	}
	parsed, err := url.Parse(presigned)
	if err != nil {
		t.Fatal(err)
	}
	query := parsed.Query()
	if parsed.Host != "opticon-test-bucket.s3.us-east-1.amazonaws.com" ||
		parsed.Path != "/opticon/releases/1.2.0/opticon-source-1.2.0.zip" ||
		query.Get("X-Amz-Algorithm") != "AWS4-HMAC-SHA256" || query.Get("X-Amz-Expires") != "1800" ||
		query.Get("X-Amz-Security-Token") != "test-session-token" || query.Get("X-Amz-SignedHeaders") != "host" ||
		len(query.Get("X-Amz-Signature")) != 64 {
		t.Fatalf("unexpected source signature: %s", presigned)
	}

	changed := source
	changed.DownloadURL = "https://d111.cloudfront.net/opticon/releases/1.2.0/other.zip"
	if _, err := signer.Presign(changed, time.Now()); err == nil {
		t.Fatal("presigner accepted a source URL that did not map to the exact immutable object")
	}
	changed = source
	changed.Product = "OpticonBundle"
	if _, err := signer.Presign(changed, time.Now()); err == nil {
		t.Fatal("presigner accepted a non-source artifact")
	}
}

func TestS3SourceDownloadSignerRejectsMissingOrMalformedSecrets(t *testing.T) {
	for _, signer := range []*s3SourceDownloadSigner{
		{},
		{bucket: "../bucket", region: "us-east-1", accessKeyID: "AKIAIOSFODNN7EXAMPLE", secretKey: strings.Repeat("x", 40)},
		{bucket: "valid-bucket", region: "not-a-region", accessKeyID: "AKIAIOSFODNN7EXAMPLE", secretKey: strings.Repeat("x", 40)},
		{bucket: "valid-bucket", region: "us-east-1", accessKeyID: "bad", secretKey: strings.Repeat("x", 40)},
		{bucket: "valid-bucket", region: "us-east-1", accessKeyID: "AKIAIOSFODNN7EXAMPLE", secretKey: "too-short"},
	} {
		if err := signer.validate(); err == nil {
			t.Fatalf("malformed signer configuration was accepted: %#v", signer)
		}
	}
}
