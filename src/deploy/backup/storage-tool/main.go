package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"math"
	"net/url"
	"os"
	"os/signal"
	"regexp"
	"strings"
	"syscall"
	"time"
	"unicode"

	"filippo.io/age"
	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/credentials"
	"github.com/aws/aws-sdk-go-v2/service/s3"
)

const toolName = "hook2stream-storage-tool"

var singleRangePattern = regexp.MustCompile(`^bytes=[0-9]+-[0-9]+$`)

type s3Target struct {
	endpoint      string
	region        string
	bucket        string
	accessKeyFile string
	secretKeyFile string
}

type multipartClient interface {
	ListMultipartUploads(
		context.Context,
		*s3.ListMultipartUploadsInput,
		...func(*s3.Options),
	) (*s3.ListMultipartUploadsOutput, error)
	AbortMultipartUpload(
		context.Context,
		*s3.AbortMultipartUploadInput,
		...func(*s3.Options),
	) (*s3.AbortMultipartUploadOutput, error)
}

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGHUP, syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	if err := run(ctx, os.Args[1:]); err != nil {
		fmt.Fprintf(os.Stderr, "%s: %v\n", toolName, err)
		os.Exit(1)
	}
}

func run(ctx context.Context, args []string) error {
	if len(args) == 0 {
		return errors.New("a command is required")
	}

	switch args[0] {
	case "encrypt-age-x25519":
		return encryptAge(args[1:], os.Stdin)
	case "put-object":
		return putObject(ctx, args[1:])
	case "get-object":
		return getObject(ctx, args[1:])
	case "head-object":
		return headObject(ctx, args[1:])
	case "delete-object":
		return deleteObject(ctx, args[1:])
	case "abort-multipart-older-than":
		return abortMultipartOlderThan(ctx, args[1:])
	default:
		return fmt.Errorf("unsupported command %q", args[0])
	}
}

func newFlagSet(command string) *flag.FlagSet {
	flags := flag.NewFlagSet(command, flag.ContinueOnError)
	flags.SetOutput(io.Discard)
	return flags
}

func addS3TargetFlags(flags *flag.FlagSet) *s3Target {
	target := &s3Target{}
	flags.StringVar(&target.endpoint, "endpoint", "", "credential-free S3 origin")
	flags.StringVar(&target.region, "region", "", "S3 signing region")
	flags.StringVar(&target.bucket, "bucket", "", "S3 bucket")
	flags.StringVar(&target.accessKeyFile, "access-key-file", "", "file containing the S3 access key ID")
	flags.StringVar(&target.secretKeyFile, "secret-key-file", "", "file containing the S3 secret access key")
	return target
}

func parseFlags(flags *flag.FlagSet, args []string) error {
	if err := flags.Parse(args); err != nil {
		return fmt.Errorf("invalid %s arguments: %w", flags.Name(), err)
	}
	if flags.NArg() != 0 {
		return fmt.Errorf("%s does not accept positional arguments", flags.Name())
	}
	return nil
}

func (target s3Target) validate() error {
	parsed, err := url.Parse(target.endpoint)
	if err != nil {
		return errors.New("S3 endpoint is not a valid URL")
	}
	isLocalMinIO := target.endpoint == "http://minio:9000"
	if (!isLocalMinIO && parsed.Scheme != "https") || parsed.Host == "" || parsed.User != nil ||
		parsed.Path != "" || parsed.RawQuery != "" || parsed.Fragment != "" {
		return errors.New("S3 endpoint must be a credential-free HTTPS origin or the exact local/CI MinIO origin")
	}
	if target.region == "" {
		return errors.New("S3 region is required")
	}
	if target.bucket == "" {
		return errors.New("S3 bucket is required")
	}
	if target.accessKeyFile == "" || target.secretKeyFile == "" {
		return errors.New("S3 access-key and secret-key files are required")
	}
	return nil
}

func (target s3Target) client(ctx context.Context) (*s3.Client, error) {
	if err := target.validate(); err != nil {
		return nil, err
	}

	accessKeyID, err := readCredentialFile(target.accessKeyFile)
	if err != nil {
		return nil, fmt.Errorf("read S3 access-key file: %w", err)
	}
	secretAccessKey, err := readCredentialFile(target.secretKeyFile)
	if err != nil {
		return nil, fmt.Errorf("read S3 secret-key file: %w", err)
	}
	awsConfig := aws.Config{
		Region: target.region,
		Credentials: aws.NewCredentialsCache(credentials.NewStaticCredentialsProvider(
			accessKeyID,
			secretAccessKey,
			"",
		)),
		RequestChecksumCalculation: aws.RequestChecksumCalculationWhenRequired,
		ResponseChecksumValidation: aws.ResponseChecksumValidationWhenRequired,
	}

	return s3.NewFromConfig(awsConfig, func(options *s3.Options) {
		options.BaseEndpoint = aws.String(target.endpoint)
		options.UsePathStyle = true
	}), nil
}

func readCredentialFile(path string) (string, error) {
	credentialFile, err := os.Open(path)
	if err != nil {
		return "", errors.New("credential file is not readable")
	}
	defer credentialFile.Close()

	info, err := credentialFile.Stat()
	if err != nil || !info.Mode().IsRegular() {
		return "", errors.New("credential file must be a regular file")
	}
	valueBytes, err := io.ReadAll(io.LimitReader(credentialFile, 4097))
	if err != nil {
		return "", errors.New("credential file could not be read")
	}
	if len(valueBytes) == 0 || len(valueBytes) > 4096 {
		return "", errors.New("credential file must contain a bounded non-empty value")
	}

	value := strings.TrimSuffix(string(valueBytes), "\n")
	if value == "" || strings.ContainsRune(value, '\n') || strings.ContainsRune(value, '\r') ||
		strings.IndexFunc(value, unicode.IsSpace) >= 0 {
		return "", errors.New("credential file must contain exactly one unpadded line")
	}
	return value, nil
}

func encryptAge(args []string, input io.Reader) (returnErr error) {
	flags := newFlagSet("encrypt-age-x25519")
	recipientText := flags.String("recipient", "", "public X25519 age recipient")
	outputPath := flags.String("output", "", "ciphertext output path")
	if err := parseFlags(flags, args); err != nil {
		return err
	}
	if *recipientText == "" || *outputPath == "" {
		return errors.New("encrypt-age-x25519 requires --recipient and --output")
	}

	recipient, err := age.ParseX25519Recipient(*recipientText)
	if err != nil {
		return errors.New("invalid X25519 age recipient")
	}

	output, err := os.OpenFile(*outputPath, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return fmt.Errorf("create age ciphertext output: %w", err)
	}
	defer func() {
		if returnErr != nil {
			_ = os.Remove(*outputPath)
		}
	}()

	encrypted, err := age.Encrypt(output, recipient)
	if err != nil {
		_ = output.Close()
		return fmt.Errorf("initialize age encryption: %w", err)
	}
	if _, err = io.Copy(encrypted, input); err != nil {
		_ = output.Close()
		return fmt.Errorf("encrypt age payload: %w", err)
	}
	if err = encrypted.Close(); err != nil {
		_ = output.Close()
		return fmt.Errorf("finalize age ciphertext: %w", err)
	}
	if err = output.Close(); err != nil {
		return fmt.Errorf("close age ciphertext: %w", err)
	}
	return nil
}

func putObject(ctx context.Context, args []string) error {
	flags := newFlagSet("put-object")
	target := addS3TargetFlags(flags)
	key := flags.String("key", "", "object key")
	bodyPath := flags.String("body", "", "object body file")
	if err := parseFlags(flags, args); err != nil {
		return err
	}
	if *key == "" || *bodyPath == "" {
		return errors.New("put-object requires --key and --body")
	}

	body, err := os.Open(*bodyPath)
	if err != nil {
		return fmt.Errorf("open PUT body: %w", err)
	}
	defer body.Close()
	stat, err := body.Stat()
	if err != nil {
		return fmt.Errorf("stat PUT body: %w", err)
	}
	if !stat.Mode().IsRegular() {
		return errors.New("PUT body must be a regular file")
	}

	client, err := target.client(ctx)
	if err != nil {
		return err
	}
	response, err := client.PutObject(ctx, &s3.PutObjectInput{
		Bucket:        aws.String(target.bucket),
		Key:           aws.String(*key),
		Body:          body,
		ContentLength: aws.Int64(stat.Size()),
	})
	if err != nil {
		return fmt.Errorf("put S3 object: %w", err)
	}

	return writeJSON(struct {
		VersionID string `json:"versionId"`
	}{VersionID: aws.ToString(response.VersionId)})
}

func getObject(ctx context.Context, args []string) (returnErr error) {
	flags := newFlagSet("get-object")
	target := addS3TargetFlags(flags)
	key := flags.String("key", "", "object key")
	outputPath := flags.String("output", "", "download output path")
	rangeHeader := flags.String("range", "", "single byte Range header")
	if err := parseFlags(flags, args); err != nil {
		return err
	}
	if *key == "" || *outputPath == "" {
		return errors.New("get-object requires --key and --output")
	}
	if *rangeHeader != "" && !singleRangePattern.MatchString(*rangeHeader) {
		return errors.New("get-object --range must be one closed byte range")
	}

	client, err := target.client(ctx)
	if err != nil {
		return err
	}
	request := &s3.GetObjectInput{
		Bucket: aws.String(target.bucket),
		Key:    aws.String(*key),
	}
	if *rangeHeader != "" {
		request.Range = rangeHeader
	}
	response, err := client.GetObject(ctx, request)
	if err != nil {
		return fmt.Errorf("get S3 object: %w", err)
	}
	defer response.Body.Close()

	output, err := os.OpenFile(*outputPath, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return fmt.Errorf("create GET output: %w", err)
	}
	defer func() {
		if returnErr != nil {
			_ = os.Remove(*outputPath)
		}
	}()
	if _, err = io.Copy(output, response.Body); err != nil {
		_ = output.Close()
		return fmt.Errorf("write GET output: %w", err)
	}
	if err = output.Close(); err != nil {
		return fmt.Errorf("close GET output: %w", err)
	}
	if err = response.Body.Close(); err != nil {
		return fmt.Errorf("close S3 response: %w", err)
	}
	return nil
}

func headObject(ctx context.Context, args []string) error {
	flags := newFlagSet("head-object")
	target := addS3TargetFlags(flags)
	key := flags.String("key", "", "object key")
	if err := parseFlags(flags, args); err != nil {
		return err
	}
	if *key == "" {
		return errors.New("head-object requires --key")
	}

	client, err := target.client(ctx)
	if err != nil {
		return err
	}
	response, err := client.HeadObject(ctx, &s3.HeadObjectInput{
		Bucket: aws.String(target.bucket),
		Key:    aws.String(*key),
	})
	if err != nil {
		return fmt.Errorf("head S3 object: %w", err)
	}
	return writeJSON(struct {
		ContentLength int64 `json:"contentLength"`
	}{ContentLength: aws.ToInt64(response.ContentLength)})
}

func deleteObject(ctx context.Context, args []string) error {
	flags := newFlagSet("delete-object")
	target := addS3TargetFlags(flags)
	key := flags.String("key", "", "object key")
	if err := parseFlags(flags, args); err != nil {
		return err
	}
	if *key == "" {
		return errors.New("delete-object requires --key")
	}

	client, err := target.client(ctx)
	if err != nil {
		return err
	}
	if _, err := client.DeleteObject(ctx, &s3.DeleteObjectInput{
		Bucket: aws.String(target.bucket),
		Key:    aws.String(*key),
	}); err != nil {
		return fmt.Errorf("delete S3 object: %w", err)
	}
	return writeJSON(struct {
		Deleted bool `json:"deleted"`
	}{Deleted: true})
}

func abortMultipartOlderThan(ctx context.Context, args []string) error {
	flags := newFlagSet("abort-multipart-older-than")
	target := addS3TargetFlags(flags)
	olderThanSeconds := flags.Int64("older-than-seconds", 0, "minimum multipart upload age")
	if err := parseFlags(flags, args); err != nil {
		return err
	}
	if *olderThanSeconds <= 0 || *olderThanSeconds > math.MaxInt64/int64(time.Second) {
		return errors.New("abort-multipart-older-than requires a positive non-overflowing --older-than-seconds")
	}

	client, err := target.client(ctx)
	if err != nil {
		return err
	}
	cutoff := time.Now().UTC().Add(-time.Duration(*olderThanSeconds) * time.Second)
	aborted, err := abortExpiredMultipartUploads(ctx, client, target.bucket, cutoff)
	if err != nil {
		return err
	}

	return writeJSON(struct {
		Aborted int `json:"aborted"`
	}{Aborted: aborted})
}

func abortExpiredMultipartUploads(
	ctx context.Context,
	client multipartClient,
	bucket string,
	cutoff time.Time,
) (int, error) {
	// Storj Gateway-MT does not support UploadIdMarker or
	// NextUploadIdMarker. Request its documented maximum single page and refuse
	// a truncated response rather than partially cleaning an unknowable set.
	page, err := client.ListMultipartUploads(ctx, &s3.ListMultipartUploadsInput{
		Bucket:     aws.String(bucket),
		MaxUploads: aws.Int32(1000),
	})
	if err != nil {
		return 0, fmt.Errorf("list multipart uploads: %w", err)
	}
	if aws.ToBool(page.IsTruncated) {
		return 0, errors.New("Storj returned more than 1000 incomplete multipart uploads; refusing unsupported partial cleanup")
	}

	for _, upload := range page.Uploads {
		if aws.ToString(upload.Key) == "" || aws.ToString(upload.UploadId) == "" || upload.Initiated == nil {
			return 0, errors.New("S3 returned incomplete multipart metadata")
		}
	}

	aborted := 0
	for _, upload := range page.Uploads {
		if upload.Initiated.After(cutoff) {
			continue
		}
		if _, err := client.AbortMultipartUpload(ctx, &s3.AbortMultipartUploadInput{
			Bucket:   aws.String(bucket),
			Key:      upload.Key,
			UploadId: upload.UploadId,
		}); err != nil {
			return 0, fmt.Errorf("abort expired multipart upload: %w", err)
		}
		aborted++
	}

	return aborted, nil
}

func writeJSON(value any) error {
	encoder := json.NewEncoder(os.Stdout)
	if err := encoder.Encode(value); err != nil {
		return fmt.Errorf("write JSON result: %w", err)
	}
	return nil
}
