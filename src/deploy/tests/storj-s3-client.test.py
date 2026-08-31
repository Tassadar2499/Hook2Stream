#!/usr/bin/env python3
"""Security-boundary tests for the narrow Storj operator S3 client."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import stat
import sys
import tempfile
import types
import unittest
from pathlib import Path
from typing import Any

sys.dont_write_bytecode = True


class StubBotoCoreError(Exception):
    pass


class StubClientError(Exception):
    def __init__(self, response: dict[str, Any], operation_name: str):
        super().__init__(operation_name)
        self.response = response
        self.operation_name = operation_name


class StubConfig:
    def __init__(self, **values: Any):
        self.values = values


def load_client_module():
    boto3 = types.ModuleType("boto3")
    boto3.__version__ = "1.35.99"
    boto3.client = lambda *_args, **_kwargs: None

    botocore = types.ModuleType("botocore")
    botocore.__path__ = []
    botocore.__version__ = "1.35.99"
    botocore_config = types.ModuleType("botocore.config")
    botocore_config.Config = StubConfig
    botocore_exceptions = types.ModuleType("botocore.exceptions")
    botocore_exceptions.BotoCoreError = StubBotoCoreError
    botocore_exceptions.ClientError = StubClientError

    sys.modules["boto3"] = boto3
    sys.modules["botocore"] = botocore
    sys.modules["botocore.config"] = botocore_config
    sys.modules["botocore.exceptions"] = botocore_exceptions

    client_path = Path(__file__).resolve().parents[1] / "storj" / "storj-s3-client.py"
    spec = importlib.util.spec_from_file_location("hook2stream_storj_s3_client", client_path)
    if spec is None or spec.loader is None:
        raise RuntimeError("could not load the Storj S3 client")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


CLIENT = load_client_module()
GLOBAL_ARGUMENTS = [
    "--endpoint-url",
    "https://gateway.storjshare.io",
    "--region",
    "global",
    "s3api",
]
MEDIA_BUCKET = "hook2stream-com-staging-media"


class StorjS3ClientTests(unittest.TestCase):
    def setUp(self) -> None:
        self.original_factory = CLIENT.boto3.client

    def tearDown(self) -> None:
        CLIENT.boto3.client = self.original_factory

    def run_main(self, arguments: list[str]) -> tuple[int, str, str]:
        stdout = io.StringIO()
        stderr = io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            result = CLIENT.main(arguments)
        return result, stdout.getvalue(), stderr.getvalue()

    def test_invalid_contract_is_rejected_before_client_or_credentials(self) -> None:
        constructed = False

        def forbidden_factory(*_args: Any, **_kwargs: Any):
            nonlocal constructed
            constructed = True
            raise AssertionError("credential-bearing client must not be constructed")

        CLIENT.boto3.client = forbidden_factory
        result, _stdout, stderr = self.run_main(
            GLOBAL_ARGUMENTS
            + ["head-bucket", "--bucket", "attacker-controlled-bucket"]
        )
        self.assertEqual(2, result)
        self.assertFalse(constructed)
        self.assertEqual(
            "Storj S3 client: bucket is outside the fixed Hook2Stream allowlist\n",
            stderr,
        )

        result, _stdout, stderr = self.run_main(
            GLOBAL_ARGUMENTS + ["put-bucket-cors", "--bucket", MEDIA_BUCKET]
        )
        self.assertEqual(2, result)
        self.assertFalse(constructed)
        self.assertIn("only the fixed S3 API operation allowlist", stderr)

    def test_head_error_uses_exact_parseable_grammar(self) -> None:
        class DeniedClient:
            def head_bucket(self, **_kwargs: Any) -> None:
                raise StubClientError(
                    {"Error": {"Code": "AccessDenied", "Message": " Access\n denied "}},
                    "HeadBucket",
                )

        CLIENT.boto3.client = lambda *_args, **_kwargs: DeniedClient()
        result, stdout, stderr = self.run_main(
            GLOBAL_ARGUMENTS + ["head-bucket", "--bucket", MEDIA_BUCKET]
        )
        self.assertEqual(1, result)
        self.assertEqual("", stdout)
        self.assertEqual(
            "An error occurred (AccessDenied) when calling the HeadBucket operation: Access denied\n",
            stderr,
        )

    def test_network_failure_is_one_line_and_fail_closed(self) -> None:
        class OfflineClient:
            def head_bucket(self, **_kwargs: Any) -> None:
                raise StubBotoCoreError("network\nfailed")

        CLIENT.boto3.client = lambda *_args, **_kwargs: OfflineClient()
        result, stdout, stderr = self.run_main(
            GLOBAL_ARGUMENTS + ["head-bucket", "--bucket", MEDIA_BUCKET]
        )
        self.assertEqual(2, result)
        self.assertEqual("", stdout)
        self.assertEqual("Storj S3 client: network failed\n", stderr)

    def test_query_and_request_kwargs_are_exact(self) -> None:
        requests: list[dict[str, Any]] = []
        configs: list[StubConfig] = []

        class HeadClient:
            def head_object(self, **kwargs: Any) -> dict[str, Any]:
                requests.append(kwargs)
                return {"ContentLength": 31, "ResponseMetadata": {"ignored": True}}

        def factory(service: str, **kwargs: Any) -> HeadClient:
            self.assertEqual("s3", service)
            self.assertEqual("https://gateway.storjshare.io", kwargs["endpoint_url"])
            self.assertEqual("global", kwargs["region_name"])
            configs.append(kwargs["config"])
            return HeadClient()

        CLIENT.boto3.client = factory
        result, stdout, stderr = self.run_main(
            GLOBAL_ARGUMENTS
            + [
                "head-object",
                "--bucket",
                MEDIA_BUCKET,
                "--key",
                "object-key",
                "--version-id",
                "version-1",
                "--query",
                "ContentLength",
                "--output",
                "text",
            ]
        )
        self.assertEqual(0, result)
        self.assertEqual("31\n", stdout)
        self.assertEqual("", stderr)
        self.assertEqual(
            [{"Bucket": MEDIA_BUCKET, "Key": "object-key", "VersionId": "version-1"}],
            requests,
        )
        self.assertEqual("s3v4", configs[0].values["signature_version"])
        self.assertEqual({"addressing_style": "path"}, configs[0].values["s3"])

    def test_exclusive_download_is_private_and_cleans_partial_output(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            destination = Path(temporary_directory) / "download"
            CLIENT.write_stream_exclusive(io.BytesIO(b"ciphertext"), str(destination))
            self.assertEqual(b"ciphertext", destination.read_bytes())
            self.assertEqual(0o600, stat.S_IMODE(destination.stat().st_mode))

            with self.assertRaises(FileExistsError):
                CLIENT.write_stream_exclusive(io.BytesIO(b"replacement"), str(destination))
            self.assertEqual(b"ciphertext", destination.read_bytes())

            partial_destination = Path(temporary_directory) / "partial"

            class FailingStream:
                def __init__(self) -> None:
                    self.reads = 0
                    self.closed = False

                def read(self, _size: int) -> bytes:
                    self.reads += 1
                    if self.reads == 1:
                        return b"partial"
                    raise OSError("stream failed")

                def close(self) -> None:
                    self.closed = True

            stream = FailingStream()
            with self.assertRaises(OSError):
                CLIENT.write_stream_exclusive(stream, str(partial_destination))
            self.assertFalse(partial_destination.exists())
            self.assertTrue(stream.closed)


if __name__ == "__main__":
    unittest.main()
