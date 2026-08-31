package main

import (
	"bytes"
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"filippo.io/age"
	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/s3"
	"github.com/aws/aws-sdk-go-v2/service/s3/types"
)

type fakeMultipartClient struct {
	pages      []*s3.ListMultipartUploadsOutput
	listError  error
	abortError error
	listCalls  int
	listInputs []*s3.ListMultipartUploadsInput
	aborted    []string
}

func (client *fakeMultipartClient) ListMultipartUploads(
	_ context.Context,
	input *s3.ListMultipartUploadsInput,
	_ ...func(*s3.Options),
) (*s3.ListMultipartUploadsOutput, error) {
	client.listInputs = append(client.listInputs, input)
	client.listCalls++
	if client.listError != nil {
		return nil, client.listError
	}
	if client.listCalls > len(client.pages) {
		return &s3.ListMultipartUploadsOutput{}, nil
	}
	page := client.pages[client.listCalls-1]
	return page, nil
}

func (client *fakeMultipartClient) AbortMultipartUpload(
	_ context.Context,
	input *s3.AbortMultipartUploadInput,
	_ ...func(*s3.Options),
) (*s3.AbortMultipartUploadOutput, error) {
	if client.abortError != nil {
		return nil, client.abortError
	}
	client.aborted = append(client.aborted, aws.ToString(input.Key)+":"+aws.ToString(input.UploadId))
	return &s3.AbortMultipartUploadOutput{}, nil
}

func TestEncryptAgeX25519RoundTrip(t *testing.T) {
	identity, err := age.GenerateX25519Identity()
	if err != nil {
		t.Fatalf("generate identity: %v", err)
	}
	plaintext := []byte("hook2stream encrypted PostgreSQL backup\n")
	outputPath := filepath.Join(t.TempDir(), "backup.dump.age")

	if err := encryptAge(
		[]string{"--recipient", identity.Recipient().String(), "--output", outputPath},
		bytes.NewReader(plaintext),
	); err != nil {
		t.Fatalf("encrypt: %v", err)
	}

	info, err := os.Stat(outputPath)
	if err != nil {
		t.Fatalf("stat ciphertext: %v", err)
	}
	if got := info.Mode().Perm(); got != 0o600 {
		t.Fatalf("ciphertext mode = %04o, want 0600", got)
	}

	ciphertext, err := os.Open(outputPath)
	if err != nil {
		t.Fatalf("open ciphertext: %v", err)
	}
	defer ciphertext.Close()
	decrypted, err := age.Decrypt(ciphertext, identity)
	if err != nil {
		t.Fatalf("initialize decryption: %v", err)
	}
	actual, err := io.ReadAll(decrypted)
	if err != nil {
		t.Fatalf("decrypt: %v", err)
	}
	if !bytes.Equal(actual, plaintext) {
		t.Fatalf("decrypted payload = %q, want %q", actual, plaintext)
	}
}

func TestEncryptAgeX25519RejectsInvalidRecipientWithoutOutput(t *testing.T) {
	outputPath := filepath.Join(t.TempDir(), "invalid.dump.age")
	if err := encryptAge(
		[]string{"--recipient", "../not-an-age-recipient", "--output", outputPath},
		bytes.NewReader([]byte("payload")),
	); err == nil {
		t.Fatal("encryptAge accepted an invalid recipient")
	}
	if _, err := os.Stat(outputPath); !os.IsNotExist(err) {
		t.Fatalf("invalid recipient left an output file: %v", err)
	}
}

func TestS3TargetRequiresSafeCredentialFreeOrigin(t *testing.T) {
	valid := s3Target{
		endpoint:      "https://gateway.storjshare.io",
		region:        "global",
		bucket:        "hook2stream-com-staging-media",
		accessKeyFile: "/run/secrets/access",
		secretKeyFile: "/run/secrets/secret",
	}
	if err := valid.validate(); err != nil {
		t.Fatalf("valid Storj target rejected: %v", err)
	}

	invalidEndpoints := []string{
		"http://gateway.storjshare.io",
		"http://minio:9001",
		"http://localhost:9000",
		"https://access:secret@gateway.storjshare.io",
		"https://gateway.storjshare.io/path",
		"https://gateway.storjshare.io?token=secret",
	}
	for _, endpoint := range invalidEndpoints {
		t.Run(endpoint, func(t *testing.T) {
			target := valid
			target.endpoint = endpoint
			if err := target.validate(); err == nil {
				t.Fatalf("target accepted unsafe endpoint %q", endpoint)
			}
		})
	}

	localMinIO := valid
	localMinIO.endpoint = "http://minio:9000"
	if err := localMinIO.validate(); err != nil {
		t.Fatalf("exact local/CI MinIO target rejected: %v", err)
	}
}

func TestReadCredentialFile(t *testing.T) {
	directory := t.TempDir()
	validPath := filepath.Join(directory, "valid")
	if err := os.WriteFile(validPath, []byte("access-key\n"), 0o600); err != nil {
		t.Fatalf("write valid credential: %v", err)
	}
	value, err := readCredentialFile(validPath)
	if err != nil {
		t.Fatalf("read valid credential: %v", err)
	}
	if value != "access-key" {
		t.Fatalf("credential = %q, want access-key", value)
	}

	for name, contents := range map[string]string{
		"empty":     "",
		"multiline": "first\nsecond\n",
		"leading":   " access-key\n",
		"trailing":  "access-key \n",
		"carriage":  "access-key\r\n",
	} {
		t.Run(name, func(t *testing.T) {
			path := filepath.Join(directory, name)
			if err := os.WriteFile(path, []byte(contents), 0o600); err != nil {
				t.Fatalf("write credential: %v", err)
			}
			if _, err := readCredentialFile(path); err == nil {
				t.Fatalf("unsafe credential contents %q were accepted", contents)
			}
		})
	}
}

func TestSingleRangePattern(t *testing.T) {
	for _, value := range []string{"bytes=0-0", "bytes=12-18", "bytes=1048576-2097151"} {
		if !singleRangePattern.MatchString(value) {
			t.Fatalf("single range rejected: %q", value)
		}
	}
	for _, value := range []string{"bytes=0-1,4-5", "bytes=-10", "bytes=10-", "items=0-1"} {
		if singleRangePattern.MatchString(value) {
			t.Fatalf("invalid range accepted: %q", value)
		}
	}
}

func TestAbortExpiredMultipartUploadsUsesStorjSinglePageAndAbortsOnlyExpired(t *testing.T) {
	cutoff := time.Date(2026, 8, 28, 12, 0, 0, 0, time.UTC)
	old := cutoff.Add(-time.Second)
	boundary := cutoff
	newUpload := cutoff.Add(time.Second)
	client := &fakeMultipartClient{pages: []*s3.ListMultipartUploadsOutput{{
		Uploads: []types.MultipartUpload{
			{Key: aws.String("old"), UploadId: aws.String("old-id"), Initiated: &old},
			{Key: aws.String("new"), UploadId: aws.String("new-id"), Initiated: &newUpload},
			{Key: aws.String("boundary"), UploadId: aws.String("boundary-id"), Initiated: &boundary},
		},
	}}}

	aborted, err := abortExpiredMultipartUploads(
		context.Background(),
		client,
		"hook2stream-com-staging-media",
		cutoff,
	)
	if err != nil {
		t.Fatalf("abort expired multipart uploads: %v", err)
	}
	if aborted != 2 {
		t.Fatalf("aborted = %d, want 2", aborted)
	}
	if client.listCalls != 1 {
		t.Fatalf("list calls = %d, want 1", client.listCalls)
	}
	input := client.listInputs[0]
	if aws.ToInt32(input.MaxUploads) != 1000 || input.KeyMarker != nil || input.UploadIdMarker != nil {
		t.Fatalf("unsafe Storj list input: %#v", input)
	}
	if got := strings.Join(client.aborted, ","); got != "old:old-id,boundary:boundary-id" {
		t.Fatalf("aborted uploads = %q", got)
	}
}

func TestAbortExpiredMultipartUploadsFailsClosed(t *testing.T) {
	cutoff := time.Now().UTC()
	old := cutoff.Add(-time.Hour)
	for name, upload := range map[string]types.MultipartUpload{
		"empty-key":       {Key: aws.String(""), UploadId: aws.String("upload-id"), Initiated: &old},
		"empty-upload-id": {Key: aws.String("key"), UploadId: aws.String(""), Initiated: &old},
		"missing-time":    {Key: aws.String("key"), UploadId: aws.String("upload-id")},
	} {
		t.Run(name, func(t *testing.T) {
			client := &fakeMultipartClient{pages: []*s3.ListMultipartUploadsOutput{{
				Uploads: []types.MultipartUpload{upload},
			}}}
			if _, err := abortExpiredMultipartUploads(
				context.Background(), client, "bucket", cutoff,
			); err == nil || !strings.Contains(err.Error(), "incomplete multipart metadata") {
				t.Fatalf("incomplete metadata error = %v", err)
			}
			if len(client.aborted) != 0 {
				t.Fatalf("incomplete metadata caused aborts: %v", client.aborted)
			}
		})
	}

	t.Run("list-error", func(t *testing.T) {
		client := &fakeMultipartClient{listError: errors.New("list failed")}
		if _, err := abortExpiredMultipartUploads(
			context.Background(), client, "bucket", cutoff,
		); err == nil || !strings.Contains(err.Error(), "list multipart uploads") {
			t.Fatalf("list error = %v", err)
		}
	})

	t.Run("truncated-page", func(t *testing.T) {
		client := &fakeMultipartClient{pages: []*s3.ListMultipartUploadsOutput{{
			IsTruncated: aws.Bool(true),
			Uploads: []types.MultipartUpload{
				{Key: aws.String("old"), UploadId: aws.String("old-id"), Initiated: &old},
			},
		}}}
		if _, err := abortExpiredMultipartUploads(
			context.Background(), client, "bucket", cutoff,
		); err == nil || !strings.Contains(err.Error(), "more than 1000") {
			t.Fatalf("truncated page error = %v", err)
		}
		if len(client.aborted) != 0 {
			t.Fatalf("truncated page caused partial cleanup: %v", client.aborted)
		}
		if client.listCalls != 1 {
			t.Fatalf("truncated page caused %d list calls, want 1", client.listCalls)
		}
	})

	t.Run("abort-error", func(t *testing.T) {
		client := &fakeMultipartClient{
			pages: []*s3.ListMultipartUploadsOutput{{Uploads: []types.MultipartUpload{
				{Key: aws.String("old"), UploadId: aws.String("old-id"), Initiated: &old},
			}}},
			abortError: errors.New("abort failed"),
		}
		if _, err := abortExpiredMultipartUploads(
			context.Background(), client, "bucket", cutoff,
		); err == nil || !strings.Contains(err.Error(), "abort expired multipart upload") {
			t.Fatalf("abort error = %v", err)
		}
	})
}
