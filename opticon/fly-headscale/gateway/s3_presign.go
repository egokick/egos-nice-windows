package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"net/url"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"
)

const sourceDownloadLifetime = 30 * time.Minute

var s3BucketPattern = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$`)
var awsRegionPattern = regexp.MustCompile(`^[a-z]{2}(?:-gov)?-[a-z0-9-]+-[0-9]+$`)
var awsAccessKeyPattern = regexp.MustCompile(`^[A-Z0-9]{16,128}$`)

type sourceDownloadSigner interface {
	Presign(source bundleArtifact, now time.Time) (string, error)
}

type s3SourceDownloadSigner struct {
	bucket       string
	region       string
	accessKeyID  string
	secretKey    string
	sessionToken string
}

func newS3SourceDownloadSignerFromEnvironment() (*s3SourceDownloadSigner, error) {
	signer := &s3SourceDownloadSigner{
		bucket:       strings.TrimSpace(os.Getenv("OPTICON_S3_BUCKET")),
		region:       strings.TrimSpace(os.Getenv("OPTICON_S3_REGION")),
		accessKeyID:  strings.TrimSpace(os.Getenv("OPTICON_S3_ACCESS_KEY_ID")),
		secretKey:    strings.TrimSpace(os.Getenv("OPTICON_S3_SECRET_ACCESS_KEY")),
		sessionToken: strings.TrimSpace(os.Getenv("OPTICON_S3_SESSION_TOKEN")),
	}
	if err := signer.validate(); err != nil {
		return nil, err
	}
	return signer, nil
}

func (s *s3SourceDownloadSigner) validate() error {
	if s == nil || !s3BucketPattern.MatchString(s.bucket) || !awsRegionPattern.MatchString(s.region) ||
		!awsAccessKeyPattern.MatchString(s.accessKeyID) || len(s.secretKey) < 32 || strings.ContainsAny(s.secretKey, "\r\n") ||
		strings.ContainsAny(s.sessionToken, "\r\n") {
		return errors.New("the private Opticon S3 presigner configuration is missing or invalid")
	}
	return nil
}

func (s *s3SourceDownloadSigner) Presign(source bundleArtifact, now time.Time) (string, error) {
	if err := s.validate(); err != nil {
		return "", err
	}
	if !validSourceArtifact(source) || !validCloudFrontDownloadURL(source) {
		return "", errors.New("the source release is not an exact trusted CloudFront artifact")
	}
	parsed, err := url.Parse(source.DownloadURL)
	if err != nil {
		return "", errors.New("the source release URL is invalid")
	}
	key := strings.TrimPrefix(parsed.Path, "/")
	expectedKey := "opticon/releases/" + source.Version + "/" + source.File
	if key != expectedKey || strings.Contains(key, "..") {
		return "", errors.New("the source release does not map to the exact immutable S3 object")
	}

	now = now.UTC()
	amzDate := now.Format("20060102T150405Z")
	shortDate := now.Format("20060102")
	scope := shortDate + "/" + s.region + "/s3/aws4_request"
	host := s.bucket + ".s3." + s.region + ".amazonaws.com"
	objectURL := &url.URL{Scheme: "https", Host: host, Path: "/" + key}
	query := objectURL.Query()
	query.Set("X-Amz-Algorithm", "AWS4-HMAC-SHA256")
	query.Set("X-Amz-Credential", s.accessKeyID+"/"+scope)
	query.Set("X-Amz-Date", amzDate)
	query.Set("X-Amz-Expires", strconv.FormatInt(int64(sourceDownloadLifetime/time.Second), 10))
	query.Set("X-Amz-SignedHeaders", "host")
	if s.sessionToken != "" {
		query.Set("X-Amz-Security-Token", s.sessionToken)
	}
	canonicalQuery := query.Encode()
	canonicalRequest := strings.Join([]string{
		"GET",
		objectURL.EscapedPath(),
		canonicalQuery,
		"host:" + host + "\n",
		"host",
		"UNSIGNED-PAYLOAD",
	}, "\n")
	requestHash := sha256.Sum256([]byte(canonicalRequest))
	stringToSign := strings.Join([]string{
		"AWS4-HMAC-SHA256",
		amzDate,
		scope,
		hex.EncodeToString(requestHash[:]),
	}, "\n")
	kDate := hmacSHA256([]byte("AWS4"+s.secretKey), shortDate)
	kRegion := hmacSHA256(kDate, s.region)
	kService := hmacSHA256(kRegion, "s3")
	kSigning := hmacSHA256(kService, "aws4_request")
	signature := hmacSHA256(kSigning, stringToSign)
	query.Set("X-Amz-Signature", hex.EncodeToString(signature))
	objectURL.RawQuery = query.Encode()
	return objectURL.String(), nil
}

func hmacSHA256(key []byte, value string) []byte {
	mac := hmac.New(sha256.New, key)
	_, _ = mac.Write([]byte(value))
	return mac.Sum(nil)
}
