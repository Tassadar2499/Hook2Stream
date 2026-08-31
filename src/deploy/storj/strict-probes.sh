#!/bin/sh
# Shared fail-closed security and response parsing for operator-only Storj tools.

STORJ_TRUSTED_PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
STORJ_REQUIRED_BOTO3_VERSION=1.35.99
STORJ_REQUIRED_BOTOCORE_VERSION=1.35.99
STORJ_CLIENT_ROOT=/opt/hook2stream-storj-s3-client-v1-boto3-1.35.99-1feba5d7c2f0
STORJ_PYTHON_BIN=$STORJ_CLIENT_ROOT/bin/python
STORJ_S3_CLIENT_BIN=$STORJ_CLIENT_ROOT/storj-s3-client.py
STORJ_S3_CLIENT_SHA256=1feba5d7c2f0ed9cf587ba14172b333caa96d36a0a42f0ab95bc20a8bce1817a

# These four binaries are the trust bootstrap. They are fixed Ubuntu system
# paths rather than PATH lookups; storj_initialize_operator_runtime validates
# them before using them to resolve the remaining tools.
STORJ_ENV_BIN=/usr/bin/env
STORJ_READLINK_BIN=/usr/bin/readlink
STORJ_STAT_BIN=/usr/bin/stat
STORJ_ID_BIN=/usr/bin/id

storj_security_error() {
    printf '%s\n' "Storj operator security: $*" >&2
    return 1
}

storj_reject_set_environment_variable() {
    [ "$2" != set ] \
        || storj_security_error "$1 must be absent; start the operator tool with env -i"
}

storj_reject_inherited_code_environment() {
    # These variables can load code or change the interpreter/toolchain before
    # an operator credential is consumed. Empty-but-exported values are also
    # rejected so the invocation contract stays deterministic and auditable.
    storj_reject_set_environment_variable BASH_ENV "${BASH_ENV+set}" || return 1
    storj_reject_set_environment_variable ENV "${ENV+set}" || return 1
    storj_reject_set_environment_variable GCONV_PATH "${GCONV_PATH+set}" || return 1
    storj_reject_set_environment_variable GLIBC_TUNABLES "${GLIBC_TUNABLES+set}" || return 1
    storj_reject_set_environment_variable LD_AUDIT "${LD_AUDIT+set}" || return 1
    storj_reject_set_environment_variable LD_DEBUG "${LD_DEBUG+set}" || return 1
    storj_reject_set_environment_variable LD_LIBRARY_PATH "${LD_LIBRARY_PATH+set}" || return 1
    storj_reject_set_environment_variable LD_PRELOAD "${LD_PRELOAD+set}" || return 1
    storj_reject_set_environment_variable LOCPATH "${LOCPATH+set}" || return 1
    storj_reject_set_environment_variable NLSPATH "${NLSPATH+set}" || return 1
    storj_reject_set_environment_variable PYTHONBREAKPOINT "${PYTHONBREAKPOINT+set}" || return 1
    storj_reject_set_environment_variable PYTHONHOME "${PYTHONHOME+set}" || return 1
    storj_reject_set_environment_variable PYTHONINSPECT "${PYTHONINSPECT+set}" || return 1
    storj_reject_set_environment_variable PYTHONPATH "${PYTHONPATH+set}" || return 1
    storj_reject_set_environment_variable PYTHONSTARTUP "${PYTHONSTARTUP+set}" || return 1
    storj_reject_set_environment_variable PYTHONUSERBASE "${PYTHONUSERBASE+set}" || return 1
    storj_reject_set_environment_variable BOTO_CONFIG "${BOTO_CONFIG+set}" || return 1
    storj_reject_set_environment_variable AWS_DATA_PATH "${AWS_DATA_PATH+set}" || return 1
    storj_reject_set_environment_variable AWS_CLI_PLUGIN_PATH "${AWS_CLI_PLUGIN_PATH+set}" || return 1
}

storj_mode_is_group_or_other_writable() {
    storj_mode_value=$1
    storj_other_digit=${storj_mode_value#${storj_mode_value%?}}
    storj_without_other=${storj_mode_value%?}
    storj_group_digit=${storj_without_other#${storj_without_other%?}}
    case "$storj_group_digit$storj_other_digit" in
        *[2367]* ) return 0 ;;
        * ) return 1 ;;
    esac
}

storj_require_safe_root_path() {
    storj_safe_path=$1
    storj_safe_kind=$2
    [ -e "$storj_safe_path" ] \
        || storj_security_error "trusted ${storj_safe_kind} is missing: ${storj_safe_path}" \
        || return 1
    storj_safe_uid=$($STORJ_STAT_BIN -c %u -- "$storj_safe_path") || return 1
    storj_safe_mode=$($STORJ_STAT_BIN -c %a -- "$storj_safe_path") || return 1
    [ "$storj_safe_uid" = 0 ] \
        || storj_security_error "trusted ${storj_safe_kind} is not root-owned: ${storj_safe_path}" \
        || return 1
    ! storj_mode_is_group_or_other_writable "$storj_safe_mode" \
        || storj_security_error "trusted ${storj_safe_kind} is group/other-writable: ${storj_safe_path}" \
        || return 1
}

storj_require_safe_tool_ancestors() {
    storj_ancestor_path=${1%/*}
    while [ -n "$storj_ancestor_path" ]; do
        storj_require_safe_root_path "$storj_ancestor_path" directory || return 1
        [ "$storj_ancestor_path" != / ] || break
        storj_ancestor_path=${storj_ancestor_path%/*}
        [ -n "$storj_ancestor_path" ] || storj_ancestor_path=/
    done
}

storj_resolve_trusted_tool() {
    storj_tool_name=$1
    storj_tool_path=$(command -v "$storj_tool_name" 2>/dev/null) \
        || storj_security_error "${storj_tool_name} is required in the trusted system PATH" \
        || return 1
    case "$storj_tool_path" in
        /*) ;;
        *) storj_security_error "${storj_tool_name} did not resolve to an absolute system path"; return 1 ;;
    esac
    storj_tool_canonical=$($STORJ_READLINK_BIN -f -- "$storj_tool_path") \
        || storj_security_error "cannot canonicalize ${storj_tool_path}" \
        || return 1
    [ -f "$storj_tool_canonical" ] && [ -x "$storj_tool_canonical" ] \
        || storj_security_error "trusted tool is not a regular executable: ${storj_tool_canonical}" \
        || return 1
    storj_require_safe_root_path "$storj_tool_canonical" tool || return 1
    storj_require_safe_tool_ancestors "$storj_tool_canonical" || return 1
    printf '%s\n' "$storj_tool_canonical"
}

storj_first_unsafe_client_tree_entry() {
    storj_tree_root=$1
    storj_tree_expected_uid=$2
    "$STORJ_FIND_BIN" "$storj_tree_root" -xdev ! -type l \
        \( ! -uid "$storj_tree_expected_uid" -o -perm /022 \) \
        -print -quit
}

storj_require_safe_client_tree() {
    [ -d "$STORJ_CLIENT_ROOT" ] && [ ! -L "$STORJ_CLIENT_ROOT" ] \
        || storj_security_error "pinned Storj client root is not a regular directory" \
        || return 1
    storj_require_safe_root_path "$STORJ_CLIENT_ROOT" directory || return 1
    storj_require_safe_tool_ancestors "$STORJ_CLIENT_ROOT" || return 1

    storj_unsafe_tree_entry=$(storj_first_unsafe_client_tree_entry \
        "$STORJ_CLIENT_ROOT" 0) || return 1
    [ -z "$storj_unsafe_tree_entry" ] \
        || storj_security_error \
            "pinned Storj client tree has unsafe ownership or mode: ${storj_unsafe_tree_entry}" \
        || return 1
    storj_unsafe_tree_type=$("$STORJ_FIND_BIN" "$STORJ_CLIENT_ROOT" -xdev \
        ! \( -type d -o -type f -o -type l \) -print -quit) || return 1
    [ -z "$storj_unsafe_tree_type" ] \
        || storj_security_error \
            "pinned Storj client tree contains an unsupported file type: ${storj_unsafe_tree_type}" \
        || return 1

    for storj_client_link in \
        "$STORJ_CLIENT_ROOT/bin/python" \
        "$STORJ_CLIENT_ROOT/bin/python3" \
        "$STORJ_CLIENT_ROOT/bin/python3.12" \
        "$STORJ_CLIENT_ROOT/lib64"; do
        [ -L "$storj_client_link" ] \
            || storj_security_error \
                "pinned Storj client symlink contract is incomplete: ${storj_client_link}" \
            || return 1
        "$STORJ_FIND_BIN" "$storj_client_link" -maxdepth 0 -type l -uid 0 \
            -print -quit | "$STORJ_GREP_BIN" -Fqx "$storj_client_link" \
            || storj_security_error \
                "pinned Storj client symlink is not root-owned: ${storj_client_link}" \
            || return 1
        storj_client_link_target=$($STORJ_READLINK_BIN -f -- "$storj_client_link") \
            || storj_security_error \
                "cannot canonicalize pinned Storj client symlink: ${storj_client_link}" \
            || return 1
        storj_require_safe_root_path "$storj_client_link_target" target || return 1
        storj_require_safe_tool_ancestors "$storj_client_link_target" || return 1
    done
    storj_extra_client_link=$("$STORJ_FIND_BIN" "$STORJ_CLIENT_ROOT" -xdev \
        -type l \
        ! -path "$STORJ_CLIENT_ROOT/bin/python" \
        ! -path "$STORJ_CLIENT_ROOT/bin/python3" \
        ! -path "$STORJ_CLIENT_ROOT/bin/python3.12" \
        ! -path "$STORJ_CLIENT_ROOT/lib64" \
        -print -quit) || return 1
    [ -z "$storj_extra_client_link" ] \
        || storj_security_error \
            "pinned Storj client tree contains an unexpected symlink: ${storj_extra_client_link}" \
        || return 1
}

storj_require_safe_python_link() {
    [ -e "$STORJ_PYTHON_BIN" ] \
        || storj_security_error "pinned Storj Python is missing: ${STORJ_PYTHON_BIN}" \
        || return 1
    storj_require_safe_tool_ancestors "$STORJ_PYTHON_BIN" || return 1
    storj_python_canonical=$($STORJ_READLINK_BIN -f -- "$STORJ_PYTHON_BIN") \
        || storj_security_error "cannot canonicalize pinned Storj Python" \
        || return 1
    [ -f "$storj_python_canonical" ] && [ -x "$storj_python_canonical" ] \
        || storj_security_error "pinned Storj Python target is not executable" \
        || return 1
    storj_require_safe_root_path "$storj_python_canonical" tool || return 1
    storj_require_safe_tool_ancestors "$storj_python_canonical" || return 1
}

storj_require_compatible_s3_client() {
    storj_client_actual_sha=$($STORJ_SHA256SUM_BIN "$STORJ_S3_CLIENT_BIN") \
        || storj_security_error "could not hash the pinned Storj S3 client" \
        || return 1
    storj_client_actual_sha=${storj_client_actual_sha%% *}
    [ "$storj_client_actual_sha" = "$STORJ_S3_CLIENT_SHA256" ] \
        || storj_security_error "pinned Storj S3 client digest mismatch" \
        || return 1
    storj_client_version_output=$(
        "$STORJ_ENV_BIN" -i \
            PATH="$STORJ_TRUSTED_PATH" \
            HOME=/nonexistent \
            LC_ALL=C LANG=C \
            PYTHONNOUSERSITE=1 \
            "$STORJ_PYTHON_BIN" -I -E -s \
            "$STORJ_S3_CLIENT_BIN" --self-check 2>&1
    ) || {
        storj_security_error "pinned Storj S3 client self-check failed"
        return 1
    }
    [ "$storj_client_version_output" = \
        "hook2stream-storj-s3/1 boto3/${STORJ_REQUIRED_BOTO3_VERSION} botocore/${STORJ_REQUIRED_BOTOCORE_VERSION} Python/3.12" ] \
        || {
            storj_security_error \
                "Storj requires the pinned boto3/${STORJ_REQUIRED_BOTO3_VERSION} and botocore/${STORJ_REQUIRED_BOTOCORE_VERSION} client"
            return 1
        }
}

storj_initialize_operator_runtime() {
    storj_reject_inherited_code_environment || return 1

    # Do not consult the caller's PATH. The fixed bootstrap commands and every
    # resolved executable must be root-owned and not group/other-writable.
    PATH=$STORJ_TRUSTED_PATH
    export PATH
    for storj_bootstrap_tool in \
        "$STORJ_ENV_BIN" "$STORJ_READLINK_BIN" "$STORJ_STAT_BIN" "$STORJ_ID_BIN"; do
        [ -f "$storj_bootstrap_tool" ] && [ -x "$storj_bootstrap_tool" ] \
            || storj_security_error "fixed trust-bootstrap tool is unavailable: ${storj_bootstrap_tool}" \
            || return 1
        storj_require_safe_root_path "$storj_bootstrap_tool" tool || return 1
        storj_require_safe_tool_ancestors "$storj_bootstrap_tool" || return 1
    done

    STORJ_OPERATOR_UID=$($STORJ_ID_BIN -u) || return 1
    STORJ_AWK_BIN=$(storj_resolve_trusted_tool awk) || return 1
    STORJ_CMP_BIN=$(storj_resolve_trusted_tool cmp) || return 1
    STORJ_CURL_BIN=$(storj_resolve_trusted_tool curl) || return 1
    STORJ_FIND_BIN=$(storj_resolve_trusted_tool find) || return 1
    STORJ_GREP_BIN=$(storj_resolve_trusted_tool grep) || return 1
    STORJ_JQ_BIN=$(storj_resolve_trusted_tool jq) || return 1
    STORJ_MKDIR_BIN=$(storj_resolve_trusted_tool mkdir) || return 1
    STORJ_MKTEMP_BIN=$(storj_resolve_trusted_tool mktemp) || return 1
    STORJ_RM_BIN=$(storj_resolve_trusted_tool rm) || return 1
    STORJ_SHA256SUM_BIN=$(storj_resolve_trusted_tool sha256sum) || return 1
    storj_require_safe_client_tree || return 1
    storj_require_safe_python_link || return 1
    [ -f "$STORJ_S3_CLIENT_BIN" ] && [ -x "$STORJ_S3_CLIENT_BIN" ] \
        || storj_security_error "pinned Storj S3 client is unavailable" \
        || return 1
    storj_require_safe_root_path "$STORJ_S3_CLIENT_BIN" tool || return 1
    storj_require_safe_tool_ancestors "$STORJ_S3_CLIENT_BIN" || return 1
    storj_require_compatible_s3_client || return 1
}

storj_validate_credential_file() {
    storj_credential_path=$1
    storj_credential_label=$2
    case "$storj_credential_path" in
        /*) ;;
        *) storj_security_error "${storj_credential_label} credential path must be absolute"; return 1 ;;
    esac
    [ -f "$storj_credential_path" ] && [ ! -L "$storj_credential_path" ] \
        || storj_security_error "${storj_credential_label} credential must be a regular non-symlink file" \
        || return 1
    storj_credential_canonical=$($STORJ_READLINK_BIN -f -- "$storj_credential_path") \
        || storj_security_error "cannot canonicalize ${storj_credential_label} credential" \
        || return 1
    [ "$storj_credential_canonical" = "$storj_credential_path" ] \
        || storj_security_error "${storj_credential_label} credential path must already be canonical" \
        || return 1

    storj_credential_uid=$($STORJ_STAT_BIN -c %u -- "$storj_credential_path") || return 1
    storj_credential_mode=$($STORJ_STAT_BIN -c %a -- "$storj_credential_path") || return 1
    storj_credential_links=$($STORJ_STAT_BIN -c %h -- "$storj_credential_path") || return 1
    storj_credential_size=$($STORJ_STAT_BIN -c %s -- "$storj_credential_path") || return 1
    [ "$storj_credential_uid" = "$STORJ_OPERATOR_UID" ] \
        || storj_security_error "${storj_credential_label} credential is not owned by the current operator" \
        || return 1
    [ "$storj_credential_mode" = 600 ] \
        || storj_security_error "${storj_credential_label} credential mode must be exactly 0600" \
        || return 1
    [ "$storj_credential_links" = 1 ] \
        || storj_security_error "${storj_credential_label} credential must have exactly one hard link" \
        || return 1
    [ "$storj_credential_size" -gt 0 ] && [ "$storj_credential_size" -le 4096 ] \
        || storj_security_error "${storj_credential_label} credential size is invalid" \
        || return 1

    storj_credential_parent=${storj_credential_path%/*}
    [ -n "$storj_credential_parent" ] || storj_credential_parent=/
    storj_parent_uid=$($STORJ_STAT_BIN -c %u -- "$storj_credential_parent") || return 1
    storj_parent_mode=$($STORJ_STAT_BIN -c %a -- "$storj_credential_parent") || return 1
    case "$storj_parent_uid" in
        0|"$STORJ_OPERATOR_UID") ;;
        *) storj_security_error "${storj_credential_label} credential directory has an unexpected owner"; return 1 ;;
    esac
    ! storj_mode_is_group_or_other_writable "$storj_parent_mode" \
        || storj_security_error "${storj_credential_label} credential directory is group/other-writable" \
        || return 1
}

storj_read_single_line_secret() {
    storj_secret_path=$1
    storj_secret_label=$2
    storj_validate_credential_file "$storj_secret_path" "$storj_secret_label" || return 1
    storj_secret_value=
    storj_extra_value=
    exec 3< "$storj_secret_path" || return 1
    IFS= read -r storj_secret_value <&3 || [ -n "$storj_secret_value" ] || {
        exec 3<&-
        storj_security_error "${storj_secret_label} credential is empty"
        return 1
    }
    if IFS= read -r storj_extra_value <&3 || [ -n "$storj_extra_value" ]; then
        exec 3<&-
        storj_security_error "${storj_secret_label} credential must contain exactly one line"
        return 1
    fi
    exec 3<&-
    case "$storj_secret_value" in
        ''|*[[:space:]]*)
            storj_secret_value=
            storj_security_error "${storj_secret_label} credential must be one unpadded non-whitespace value"
            return 1
            ;;
    esac
    printf '%s' "$storj_secret_value"
    storj_secret_value=
}

storj_write_aws_credentials_file() {
    storj_access_source=$1
    storj_secret_source=$2
    storj_credentials_destination=$3
    storj_credentials_label=$4
    [ "$storj_access_source" != "$storj_secret_source" ] \
        || storj_security_error "${storj_credentials_label} access and secret credentials must use different files" \
        || return 1
    storj_access_value=$(storj_read_single_line_secret \
        "$storj_access_source" "${storj_credentials_label} access key") || return 1
    storj_secret_value=$(storj_read_single_line_secret \
        "$storj_secret_source" "${storj_credentials_label} secret key") || {
        storj_access_value=
        return 1
    }
    (umask 077; printf '%s\n' \
        '[default]' \
        "aws_access_key_id = ${storj_access_value}" \
        "aws_secret_access_key = ${storj_secret_value}" \
        > "$storj_credentials_destination") || {
        storj_access_value=
        storj_secret_value=
        return 1
    }
    storj_access_value=
    storj_secret_value=
    [ "$($STORJ_STAT_BIN -c %a -- "$storj_credentials_destination")" = 600 ] \
        || storj_security_error "temporary AWS credential file mode is not 0600"
}

storj_aws_error_code_from_file() {
    storj_error_file=$1
    storj_expected_operation=$2
    [ -f "$storj_error_file" ] && [ ! -L "$storj_error_file" ] || return 1

    ${STORJ_ENV_BIN:-/usr/bin/env} -i \
        PATH="$STORJ_TRUSTED_PATH" LC_ALL=C LANG=C \
        "${STORJ_AWK_BIN:-/usr/bin/awk}" -v operation="$storj_expected_operation" '
        /[^[:space:]]/ {
            nonempty += 1
            record = $0
        }
        END {
            if (nonempty != 1) exit 1
            prefix = "An error occurred ("
            marker = ") when calling the " operation " operation:"
            if (substr(record, 1, length(prefix)) != prefix) exit 1
            remainder = substr(record, length(prefix) + 1)
            marker_at = index(remainder, marker)
            if (marker_at <= 1) exit 1
            code = substr(remainder, 1, marker_at - 1)
            detail = substr(remainder, marker_at + length(marker))
            if (code !~ /^[A-Za-z0-9]+$/) exit 1
            if (detail !~ /^[[:space:]]+[^[:space:]]/) exit 1
            print code
        }
    ' "$storj_error_file"
}

storj_error_is_missing_bucket() {
    case "$1" in
        NoSuchBucket|NotFound|404) return 0 ;;
        *) return 1 ;;
    esac
}

storj_error_is_permission_denied() {
    case "$1" in
        AccessDenied|Forbidden|403) return 0 ;;
        *) return 1 ;;
    esac
}

storj_require_permission_denied_error() {
    storj_permission_error_file=$1
    storj_permission_operation=$2
    storj_permission_error_code=$(storj_aws_error_code_from_file \
        "$storj_permission_error_file" "$storj_permission_operation") \
        || return 1
    storj_error_is_permission_denied "$storj_permission_error_code"
}

storj_run_s3_client() {
    storj_credentials_file=$1
    shift
    [ "${1:-}" = s3api ] || {
        storj_security_error "only the fixed Storj s3api command surface is allowed"
        return 1
    }
    case "${2:-}" in
        abort-multipart-upload|create-bucket|create-multipart-upload|delete-object|\
        get-bucket-versioning|get-object|head-bucket|head-object|\
        list-multipart-uploads|list-objects-v2|put-bucket-versioning|put-object|\
        upload-part) ;;
        *)
            storj_security_error "Storj S3 operation is outside the fixed allowlist"
            return 1
            ;;
    esac
    "$STORJ_ENV_BIN" -i \
        PATH="$STORJ_TRUSTED_PATH" \
        HOME="$STORJ_OPERATOR_HOME" \
        LC_ALL=C LANG=C \
        AWS_CONFIG_FILE="$STORJ_AWS_CONFIG_FILE" \
        AWS_SHARED_CREDENTIALS_FILE="$storj_credentials_file" \
        AWS_DEFAULT_REGION="$STORJ_S3_REGION" \
        AWS_REGION="$STORJ_S3_REGION" \
        AWS_EC2_METADATA_DISABLED=true \
        PYTHONNOUSERSITE=1 \
        "$STORJ_PYTHON_BIN" -I -E -s \
        "$STORJ_S3_CLIENT_BIN" \
        --endpoint-url "$STORJ_S3_ENDPOINT" --region "$STORJ_S3_REGION" "$@"
}

storj_direct_anonymous_http_status() (
    [ "$#" -eq 1 ] || exit 2
    "$STORJ_ENV_BIN" -i \
        PATH="$STORJ_TRUSTED_PATH" \
        HOME="$STORJ_OPERATOR_HOME" \
        LC_ALL=C LANG=C \
        "$STORJ_CURL_BIN" -q \
        --proxy '' \
        --noproxy '*' \
        --silent \
        --show-error \
        --max-time 20 \
        --max-redirs 0 \
        --proto '=https' \
        --tlsv1.2 \
        --output /dev/null \
        --write-out '%{http_code}' \
        "$1"
)

storj_require_private_anonymous_get() {
    storj_anonymous_probe_url=$1
    storj_anonymous_probe_status=$(storj_direct_anonymous_http_status \
        "$storj_anonymous_probe_url") || return 1
    case "$storj_anonymous_probe_status" in
        403|404) return 0 ;;
        *)
            printf '%s\n' \
                "Storj anonymous privacy probe expected HTTP 403 or 404; received ${storj_anonymous_probe_status}" >&2
            return 1
            ;;
    esac
}
