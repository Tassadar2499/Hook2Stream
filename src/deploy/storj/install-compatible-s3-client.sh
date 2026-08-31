#!/bin/sh
set -eu

fail() { printf '%s\n' "Storj S3 client installer: $*" >&2; exit 1; }

case "$0" in
    /*) installer_path=$0 ;;
    */*) installer_path=$PWD/$0 ;;
    *) installer_path=$PWD/$0 ;;
esac
installer_parent=${installer_path%/*}
script_dir=$(CDPATH= cd -P -- "$installer_parent" && pwd -P)
lock_file=$script_dir/boto3-requirements.lock
client_source=$script_dir/storj-s3-client.py
strict_probes=$script_dir/strict-probes.sh
# The content prefix makes a reviewed client change install side-by-side instead
# of accidentally accepting stale code with the same dependency versions.
install_root=/opt/hook2stream-storj-s3-client-v1-boto3-1.35.99-1feba5d7c2f0
installed_client=$install_root/storj-s3-client.py
python_bin=/usr/bin/python3

# shellcheck source=./strict-probes.sh
. "$strict_probes"

[ "$(/usr/bin/id -u)" = 0 ] || fail "run as root on the operator workstation"
[ "$(/usr/bin/uname -m)" = x86_64 ] \
    || fail "the checked wheel lock supports Ubuntu 24.04 amd64 only"
for source_file in "$lock_file" "$client_source" "$strict_probes"; do
    [ -f "$source_file" ] && [ ! -L "$source_file" ] \
        || fail "required source is missing or is a symlink"
done
[ -x "$python_bin" ] || fail "Ubuntu system Python is missing"
python_version=$(
    "$python_bin" -I -E -s -c \
        'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}")'
) || fail "could not read the system Python version"
[ "$python_version" = 3.12 ] \
    || fail "the checked client requires Ubuntu 24.04 system Python 3.12"
source_digest=$(/usr/bin/sha256sum "$client_source") \
    || fail "could not hash the checked-in S3 client"
source_digest=${source_digest%% *}
[ "$source_digest" = "$STORJ_S3_CLIENT_SHA256" ] \
    || fail "checked-in S3 client digest does not match the installer contract"
storj_require_safe_root_path /opt directory \
    || fail "the /opt install boundary is not root-owned and non-writable"
storj_require_safe_tool_ancestors "$install_root" \
    || fail "the /opt install boundary has unsafe ancestors"

if [ -e "$install_root" ]; then
    storj_initialize_operator_runtime \
        || fail "existing pinned client is unsafe, incomplete, or incompatible"
    printf '%s\n' "Storj S3 client installer: compatible client already installed"
    exit 0
fi

created_install=false
cleanup() {
    cleanup_status=$?
    trap - EXIT HUP INT TERM
    if [ "$created_install" = true ] && [ "$cleanup_status" -ne 0 ]; then
        /usr/bin/rm -rf -- "$install_root"
    fi
    exit "$cleanup_status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

created_install=true
/usr/bin/env -i \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    HOME=/nonexistent LC_ALL=C LANG=C PYTHONNOUSERSITE=1 \
    "$python_bin" -I -E -s -m venv "$install_root" \
    || fail "could not create the pinned virtual environment"
/usr/bin/env -i \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    HOME=/nonexistent LC_ALL=C LANG=C PYTHONNOUSERSITE=1 \
    PIP_CONFIG_FILE=/dev/null PIP_NO_INPUT=1 \
    "$install_root/bin/python" -I -E -s -m pip --isolated install \
    --disable-pip-version-check \
    --no-cache-dir \
    --only-binary=:all: \
    --require-hashes \
    --requirement "$lock_file" \
    || fail "hash-locked client installation failed"
/usr/bin/install -o root -g root -m 0555 "$client_source" "$installed_client"

/usr/bin/chown -R 0:0 "$install_root"
/usr/bin/chmod -R go-w "$install_root"
storj_initialize_operator_runtime \
    || fail "installed client did not match the pinned security and compatibility contract"
created_install=false
printf '%s\n' \
    "Storj S3 client installer: installed boto3/1.35.99 with botocore/1.35.99"
