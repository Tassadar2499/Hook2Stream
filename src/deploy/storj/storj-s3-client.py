#!/usr/bin/env python3
"""Narrow operator-only S3 client for the Hook2Stream Storj contract."""

from __future__ import annotations

import datetime as dt
import json
import os
import re
import sys
from pathlib import Path
from typing import Any

import boto3
import botocore
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError

CLIENT_SCHEMA_VERSION = 1
REQUIRED_BOTO3_VERSION = "1.35.99"
REQUIRED_BOTOCORE_VERSION = "1.35.99"
CANONICAL_BUCKETS = {
    "hook2stream-com-staging-media",
    "hook2stream-com-staging-pg-backups",
    "hook2stream-com-production-media",
    "hook2stream-com-production-pg-backups",
}
SUPPORTED_OPERATIONS = {
    "abort-multipart-upload",
    "create-bucket",
    "create-multipart-upload",
    "delete-object",
    "get-bucket-versioning",
    "get-object",
    "head-bucket",
    "head-object",
    "list-multipart-uploads",
    "list-objects-v2",
    "put-bucket-versioning",
    "put-object",
    "upload-part",
}
OPERATION_NAMES = {
    "abort-multipart-upload": "AbortMultipartUpload",
    "create-bucket": "CreateBucket",
    "create-multipart-upload": "CreateMultipartUpload",
    "delete-object": "DeleteObject",
    "get-bucket-versioning": "GetBucketVersioning",
    "get-object": "GetObject",
    "head-bucket": "HeadBucket",
    "head-object": "HeadObject",
    "list-multipart-uploads": "ListMultipartUploads",
    "list-objects-v2": "ListObjectsV2",
    "put-bucket-versioning": "PutBucketVersioning",
    "put-object": "PutObject",
    "upload-part": "UploadPart",
}


class ContractError(Exception):
    pass


def one_line(value: object) -> str:
    return re.sub(r"\s+", " ", str(value)).strip()


def fail(message: str) -> int:
    print(f"Storj S3 client: {one_line(message)}", file=sys.stderr)
    return 2


def parse_options(
    arguments: list[str],
    *,
    required: set[str],
    optional: set[str] | None = None,
    positional_count: int = 0,
) -> tuple[dict[str, str], list[str]]:
    allowed = required | (optional or set())
    values: dict[str, str] = {}
    positionals: list[str] = []
    index = 0
    while index < len(arguments):
        token = arguments[index]
        if token.startswith("--"):
            if token not in allowed or token in values:
                raise ContractError(f"unsupported or duplicate option for operation: {token}")
            if index + 1 >= len(arguments) or arguments[index + 1].startswith("--"):
                raise ContractError(f"option requires one value: {token}")
            values[token] = arguments[index + 1]
            index += 2
            continue
        positionals.append(token)
        index += 1
    missing = sorted(required - values.keys())
    if missing:
        raise ContractError(f"missing required option: {missing[0]}")
    if len(positionals) != positional_count:
        raise ContractError("unexpected positional argument count")
    return values, positionals


def integer(value: str, option: str, minimum: int, maximum: int) -> int:
    if not value.isdigit():
        raise ContractError(f"{option} must be an integer")
    result = int(value)
    if result < minimum or result > maximum:
        raise ContractError(f"{option} is outside the allowed range")
    return result


def validate_output_options(
    operation: str,
    options: dict[str, str],
) -> None:
    query_by_operation = {
        "create-multipart-upload": "UploadId",
        "get-bucket-versioning": "Status",
        "head-object": "ContentLength",
        "put-object": "VersionId",
    }
    output = options.get("--output", "json")
    query = options.get("--query")
    if query is None:
        if output != "json":
            raise ContractError("text output requires an allowed query")
        return
    if output != "text" or query_by_operation.get(operation) != query:
        raise ContractError("unsupported query/output combination")


def validate_request(operation: str, arguments: list[str]) -> None:
    contracts = {
        "abort-multipart-upload": (
            {"--bucket", "--key", "--upload-id"},
            set(),
            0,
        ),
        "create-bucket": (
            {"--bucket", "--create-bucket-configuration"},
            {"--output"},
            0,
        ),
        "create-multipart-upload": (
            {"--bucket", "--key"},
            {"--query", "--output"},
            0,
        ),
        "delete-object": ({"--bucket", "--key"}, set(), 0),
        "get-bucket-versioning": (
            {"--bucket"},
            {"--query", "--output"},
            0,
        ),
        "get-object": (
            {"--bucket", "--key"},
            {"--range", "--version-id"},
            1,
        ),
        "head-bucket": ({"--bucket"}, set(), 0),
        "head-object": (
            {"--bucket", "--key"},
            {"--version-id", "--query", "--output"},
            0,
        ),
        "list-multipart-uploads": (
            {"--bucket"},
            {"--max-uploads", "--output"},
            0,
        ),
        "list-objects-v2": (
            {"--bucket"},
            {"--prefix", "--max-keys", "--output"},
            0,
        ),
        "put-bucket-versioning": (
            {"--bucket", "--versioning-configuration"},
            set(),
            0,
        ),
        "put-object": (
            {"--bucket", "--key", "--body"},
            {"--content-type", "--metadata", "--query", "--output"},
            0,
        ),
        "upload-part": (
            {"--bucket", "--key", "--upload-id", "--part-number", "--body"},
            set(),
            0,
        ),
    }
    required, optional, positional_count = contracts[operation]
    options, positionals = parse_options(
        arguments,
        required=required,
        optional=optional,
        positional_count=positional_count,
    )
    if options["--bucket"] not in CANONICAL_BUCKETS:
        raise ContractError("bucket is outside the fixed Hook2Stream allowlist")

    validate_output_options(operation, options)
    if operation == "create-bucket" and (
        options["--create-bucket-configuration"]
        != "LocationConstraint=global-1"
    ):
        raise ContractError("bucket location must be exactly global-1")
    if operation == "put-bucket-versioning" and (
        options["--versioning-configuration"] != "Status=Enabled"
    ):
        raise ContractError("only backup versioning enablement is allowed")
    if operation == "put-bucket-versioning" and not options["--bucket"].endswith(
        "-pg-backups"
    ):
        raise ContractError("versioning may be enabled only on a backup bucket")
    if operation in {"put-object", "upload-part"}:
        body_path = Path(options["--body"])
        if not body_path.is_file() or body_path.is_symlink():
            raise ContractError("--body must name a regular non-symlink file")
    if metadata_value := options.get("--metadata"):
        if "=" not in metadata_value:
            raise ContractError("metadata must be one key=value pair")
        metadata_key, metadata_item = metadata_value.split("=", 1)
        if not metadata_key or not metadata_item:
            raise ContractError("metadata key and value must be non-empty")
    if range_value := options.get("--range"):
        if not re.fullmatch(r"bytes=[0-9]+-[0-9]+", range_value):
            raise ContractError("only one explicit byte range is allowed")
    if part_number := options.get("--part-number"):
        integer(part_number, "--part-number", 1, 10000)
    if max_uploads := options.get("--max-uploads"):
        integer(max_uploads, "--max-uploads", 1, 1000)
    if max_keys := options.get("--max-keys"):
        integer(max_keys, "--max-keys", 1, 1000)
    if prefix := options.get("--prefix"):
        if not prefix.endswith("/"):
            raise ContractError("Storj list prefix must end with a slash")
    if operation == "get-object":
        destination = Path(positionals[0])
        if destination.exists() or destination.is_symlink():
            raise ContractError("get-object destination must not already exist")
        if not destination.parent.is_dir():
            raise ContractError("get-object destination parent is unavailable")


def safe_json(value: Any) -> Any:
    if isinstance(value, dict):
        return {
            key: safe_json(item)
            for key, item in value.items()
            if key != "ResponseMetadata" and key != "Body"
        }
    if isinstance(value, list):
        return [safe_json(item) for item in value]
    if isinstance(value, dt.datetime):
        return value.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z")
    if isinstance(value, bytes):
        return value.hex()
    return value


def emit(response: dict[str, Any], options: dict[str, str], *, silent: bool = False) -> None:
    if silent:
        return
    output = options.get("--output", "json")
    query = options.get("--query")
    if output not in {"json", "text"}:
        raise ContractError("--output must be json or text")
    if query is not None:
        if output != "text" or query not in {
            "ContentLength",
            "Status",
            "UploadId",
            "VersionId",
        }:
            raise ContractError("unsupported query/output combination")
        value = response.get(query)
        print("None" if value is None else value)
        return
    if output != "json":
        raise ContractError("text output requires an allowed query")
    print(json.dumps(safe_json(response), separators=(",", ":"), sort_keys=True))


def open_body(path_value: str):
    path = Path(path_value)
    if not path.is_file() or path.is_symlink():
        raise ContractError("--body must name a regular non-symlink file")
    return path.open("rb")


def write_stream_exclusive(stream: Any, destination_value: str) -> None:
    destination = Path(destination_value)
    descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "wb") as output:
            while chunk := stream.read(1024 * 1024):
                output.write(chunk)
    except BaseException:
        destination.unlink(missing_ok=True)
        raise
    finally:
        stream.close()


def execute(client: Any, operation: str, arguments: list[str]) -> None:
    if operation == "head-bucket":
        options, _ = parse_options(arguments, required={"--bucket"})
        client.head_bucket(Bucket=options["--bucket"])
        return

    if operation == "create-bucket":
        options, _ = parse_options(
            arguments,
            required={"--bucket", "--create-bucket-configuration"},
            optional={"--output"},
        )
        configuration = options["--create-bucket-configuration"]
        if configuration != "LocationConstraint=global-1":
            raise ContractError("bucket location must be exactly global-1")
        response = client.create_bucket(
            Bucket=options["--bucket"],
            CreateBucketConfiguration={"LocationConstraint": "global-1"},
        )
        emit(response, options)
        return

    if operation == "get-bucket-versioning":
        options, _ = parse_options(
            arguments,
            required={"--bucket"},
            optional={"--query", "--output"},
        )
        emit(client.get_bucket_versioning(Bucket=options["--bucket"]), options)
        return

    if operation == "put-bucket-versioning":
        options, _ = parse_options(
            arguments,
            required={"--bucket", "--versioning-configuration"},
        )
        if options["--versioning-configuration"] != "Status=Enabled":
            raise ContractError("only backup versioning enablement is allowed")
        client.put_bucket_versioning(
            Bucket=options["--bucket"], VersioningConfiguration={"Status": "Enabled"}
        )
        return

    if operation == "put-object":
        options, _ = parse_options(
            arguments,
            required={"--bucket", "--key", "--body"},
            optional={"--content-type", "--metadata", "--query", "--output"},
        )
        request: dict[str, Any] = {
            "Bucket": options["--bucket"],
            "Key": options["--key"],
        }
        if content_type := options.get("--content-type"):
            request["ContentType"] = content_type
        if metadata_value := options.get("--metadata"):
            if "=" not in metadata_value:
                raise ContractError("metadata must be one key=value pair")
            metadata_key, metadata_item = metadata_value.split("=", 1)
            if not metadata_key or not metadata_item:
                raise ContractError("metadata key and value must be non-empty")
            request["Metadata"] = {metadata_key: metadata_item}
        with open_body(options["--body"]) as body:
            request["Body"] = body
            response = client.put_object(**request)
        emit(response, options)
        return

    if operation == "head-object":
        options, _ = parse_options(
            arguments,
            required={"--bucket", "--key"},
            optional={"--version-id", "--query", "--output"},
        )
        request = {"Bucket": options["--bucket"], "Key": options["--key"]}
        if version_id := options.get("--version-id"):
            request["VersionId"] = version_id
        emit(client.head_object(**request), options)
        return

    if operation == "get-object":
        options, positionals = parse_options(
            arguments,
            required={"--bucket", "--key"},
            optional={"--range", "--version-id"},
            positional_count=1,
        )
        request = {"Bucket": options["--bucket"], "Key": options["--key"]}
        if range_value := options.get("--range"):
            if not re.fullmatch(r"bytes=[0-9]+-[0-9]+", range_value):
                raise ContractError("only one explicit byte range is allowed")
            request["Range"] = range_value
        if version_id := options.get("--version-id"):
            request["VersionId"] = version_id
        response = client.get_object(**request)
        if "Body" not in response:
            raise ContractError("get-object response omitted the response body")
        write_stream_exclusive(response["Body"], positionals[0])
        return

    if operation == "delete-object":
        options, _ = parse_options(
            arguments,
            required={"--bucket", "--key"},
        )
        request = {"Bucket": options["--bucket"], "Key": options["--key"]}
        emit(client.delete_object(**request), options)
        return

    if operation == "create-multipart-upload":
        options, _ = parse_options(
            arguments,
            required={"--bucket", "--key"},
            optional={"--query", "--output"},
        )
        response = client.create_multipart_upload(
            Bucket=options["--bucket"], Key=options["--key"]
        )
        emit(response, options)
        return

    if operation == "upload-part":
        options, _ = parse_options(
            arguments,
            required={
                "--bucket",
                "--key",
                "--upload-id",
                "--part-number",
                "--body",
            },
        )
        with open_body(options["--body"]) as body:
            response = client.upload_part(
                Bucket=options["--bucket"],
                Key=options["--key"],
                UploadId=options["--upload-id"],
                PartNumber=integer(options["--part-number"], "--part-number", 1, 10000),
                Body=body,
            )
        emit(response, options)
        return

    if operation == "list-multipart-uploads":
        options, _ = parse_options(
            arguments,
            required={"--bucket"},
            optional={"--max-uploads", "--output"},
        )
        request = {"Bucket": options["--bucket"]}
        if max_uploads := options.get("--max-uploads"):
            request["MaxUploads"] = integer(max_uploads, "--max-uploads", 1, 1000)
        emit(client.list_multipart_uploads(**request), options)
        return

    if operation == "abort-multipart-upload":
        options, _ = parse_options(
            arguments,
            required={"--bucket", "--key", "--upload-id"},
        )
        emit(
            client.abort_multipart_upload(
                Bucket=options["--bucket"],
                Key=options["--key"],
                UploadId=options["--upload-id"],
            ),
            options,
        )
        return

    if operation == "list-objects-v2":
        options, _ = parse_options(
            arguments,
            required={"--bucket"},
            optional={"--prefix", "--max-keys", "--output"},
        )
        request = {"Bucket": options["--bucket"]}
        if prefix := options.get("--prefix"):
            if not prefix.endswith("/"):
                raise ContractError("Storj list prefix must end with a slash")
            request["Prefix"] = prefix
        if max_keys := options.get("--max-keys"):
            request["MaxKeys"] = integer(max_keys, "--max-keys", 1, 1000)
        emit(client.list_objects_v2(**request), options)
        return

    raise ContractError("operation is outside the fixed S3 allowlist")


def main(arguments: list[str]) -> int:
    if arguments == ["--self-check"]:
        if sys.version_info[:2] != (3, 12):
            return fail("self-check requires Python 3.12")
        if boto3.__version__ != REQUIRED_BOTO3_VERSION:
            return fail("self-check found an incompatible boto3 version")
        if botocore.__version__ != REQUIRED_BOTOCORE_VERSION:
            return fail("self-check found an incompatible botocore version")
        print(
            "hook2stream-storj-s3/1 "
            f"boto3/{boto3.__version__} "
            f"botocore/{botocore.__version__} Python/3.12"
        )
        return 0

    if len(arguments) < 6:
        return fail("expected fixed endpoint, region, s3api, and operation")
    if arguments[0] != "--endpoint-url" or arguments[2] != "--region":
        return fail("global options are out of contract")
    endpoint = arguments[1]
    region = arguments[3]
    if endpoint != "https://gateway.storjshare.io" or region != "global":
        return fail("endpoint and signing region must match the Storj contract")
    if arguments[4] != "s3api" or arguments[5] not in SUPPORTED_OPERATIONS:
        return fail("only the fixed S3 API operation allowlist is supported")
    operation = arguments[5]

    try:
        # Validate the entire fixed command contract before boto3 constructs a
        # client and resolves AWS_SHARED_CREDENTIALS_FILE.
        validate_request(operation, arguments[6:])
        client = boto3.client(
            "s3",
            endpoint_url=endpoint,
            region_name=region,
            config=Config(
                # botocore 1.35.99 predates the incompatible default flexible
                # checksum behavior; its Config rejects the newer checksum
                # knobs, so compatibility is enforced by the exact version.
                signature_version="s3v4",
                s3={"addressing_style": "path"},
                retries={"mode": "standard", "max_attempts": 3},
            ),
        )
        execute(client, operation, arguments[6:])
        return 0
    except ClientError as error:
        details = error.response.get("Error", {})
        code = one_line(details.get("Code", "Unknown"))
        message = one_line(details.get("Message", "provider rejected the request"))
        if not re.fullmatch(r"[A-Za-z0-9]+", code):
            code = "Unknown"
        print(
            f"An error occurred ({code}) when calling the "
            f"{OPERATION_NAMES[operation]} operation: {message}",
            file=sys.stderr,
        )
        return 1
    except (BotoCoreError, ContractError, OSError, ValueError) as error:
        return fail(error)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
