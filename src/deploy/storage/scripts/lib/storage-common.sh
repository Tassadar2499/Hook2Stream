#!/bin/sh

storage_fail() { printf '%s\n' "storage deploy: $*" >&2; exit 1; }

storage_require_command() {
    command -v "$1" >/dev/null 2>&1 || storage_fail "required command is unavailable: $1"
}

storage_validate_strict_env() {
    env_path=$1
    [ -f "$env_path" ] && [ ! -L "$env_path" ] || storage_fail "environment must be a regular non-symlink file"
    awk '
        /^[[:space:]]*$/ || /^#/ { next }
        !/^[A-Z][A-Z0-9_]*=[A-Za-z0-9_\.\/:@+-]+$/ { exit 1 }
        { key=$0; sub(/=.*/, "", key); if (seen[key]++) exit 1 }
    ' "$env_path" || storage_fail "environment contains unsafe syntax or duplicate keys"
}

storage_env_value() {
    env_path=$1
    env_key=$2
    count=$(awk -F= -v key="$env_key" '$1 == key { count++ } END { print count + 0 }' "$env_path")
    [ "$count" -eq 1 ] || storage_fail "$env_key must occur exactly once"
    awk -F= -v key="$env_key" '$1 == key { print substr($0, index($0, "=") + 1) }' "$env_path"
}

storage_read_secret_file() {
    storage_secret_path=$1
    [ -f "$storage_secret_path" ] && [ ! -L "$storage_secret_path" ] && [ -r "$storage_secret_path" ] \
        || storage_fail "secret is not a readable regular file: $storage_secret_path"
    storage_secret_value=
    storage_secret_extra=
    exec 9< "$storage_secret_path"
    IFS= read -r storage_secret_value <&9 \
        || [ -n "$storage_secret_value" ] \
        || { exec 9<&-; storage_fail "secret is empty: $storage_secret_path"; }
    if IFS= read -r storage_secret_extra <&9 || [ -n "$storage_secret_extra" ]; then
        exec 9<&-
        storage_fail "secret must contain exactly one line: $storage_secret_path"
    fi
    exec 9<&-
    case "$storage_secret_value" in
        ''|*[[:space:]]*) storage_fail "secret must be non-empty and contain no whitespace: $storage_secret_path" ;;
    esac
    printf '%s' "$storage_secret_value"
}

storage_validate_mc_host_credential() {
    [ "$#" -eq 2 ] || storage_fail "storage_validate_mc_host_credential requires a name and value"
    storage_credential_name=$1
    storage_credential_value=$2
    case "$storage_credential_value" in
        ''|*[!A-Za-z0-9._+-]*)
            storage_fail "$storage_credential_name must use only the MC_HOST-safe alphabet [A-Za-z0-9._+-]"
            ;;
    esac
}

storage_validate_managed_identity_inventory() {
    [ "$#" -eq 1 ] \
        || storage_fail "storage_validate_managed_identity_inventory requires a path"
    storage_inventory_path=$1
    [ -f "$storage_inventory_path" ] && [ ! -L "$storage_inventory_path" ] \
        && [ -r "$storage_inventory_path" ] \
        || storage_fail "managed identity inventory must be a readable regular non-symlink file"
    awk '
        NR == 1 { if ($0 != "HOOK2STREAM_STORAGE_MANAGED_IDENTITIES_V1") exit 1; next }
        NR == 2 { if ($0 !~ /^bootstrap=(-|[A-Za-z0-9._+-]{3,})$/) exit 1; values[1]=substr($0,11); next }
        NR == 3 { if ($0 !~ /^runtime=(-|[A-Za-z0-9._+-]{3,})$/) exit 1; values[2]=substr($0,9); next }
        NR == 4 { if ($0 !~ /^backup=(-|[A-Za-z0-9._+-]{3,})$/) exit 1; values[3]=substr($0,8); next }
        { exit 1 }
        END {
            if (NR != 4) exit 1
            for (left=1; left<=3; left++) {
                if (values[left] == "-") continue
                for (right=left+1; right<=3; right++)
                    if (values[left] == values[right]) exit 1
            }
        }
    ' "$storage_inventory_path" \
        || storage_fail "managed identity inventory schema is invalid"
}

storage_managed_identity_inventory_is_empty() {
    [ "$#" -eq 1 ] || storage_fail "storage_managed_identity_inventory_is_empty requires a path"
    awk '
        NR == 2 && $0 == "bootstrap=-" { bootstrap=1 }
        NR == 3 && $0 == "runtime=-" { runtime=1 }
        NR == 4 && $0 == "backup=-" { backup=1 }
        END { exit (NR == 4 && bootstrap && runtime && backup) ? 0 : 1 }
    ' "$1"
}

storage_validate_proc_visibility() {
    [ "$#" -eq 1 ] || storage_fail "storage_validate_proc_visibility requires mount options"
    storage_proc_options=$1
    if printf '%s\n' "$storage_proc_options" | tr ',' '\n' | grep -Eq '^gid='; then
        storage_fail "/proc must not grant process visibility through gid="
    fi
    printf '%s\n' "$storage_proc_options" | tr ',' '\n' | grep -Eq '^(hidepid=2|hidepid=invisible)$' \
        || storage_fail "/proc must use hidepid=2 (or hidepid=invisible)"
}

storage_validate_digest_image() {
    storage_image_name=$1
    storage_image_value=$2
    case "$storage_image_value" in *@sha256:*) ;; *) storage_fail "$storage_image_name must be an image@sha256 reference" ;; esac
    storage_image_digest=${storage_image_value##*@sha256:}
    [ "${#storage_image_digest}" -eq 64 ] || storage_fail "$storage_image_name must contain a full sha256 digest"
    case "$storage_image_digest" in *[!0-9a-f]*) storage_fail "$storage_image_name digest must be lowercase hexadecimal" ;; esac
    storage_image_repository=${storage_image_value%@sha256:*}
    case "$storage_image_repository" in *[!A-Za-z0-9._/:+-]*|'') storage_fail "$storage_image_name repository is invalid" ;; esac
}

storage_validate_minio_security_policy() {
    [ "$#" -eq 3 ] \
        || storage_fail "storage_validate_minio_security_policy requires a path, release, and source commit"
    storage_security_policy_path=$1
    storage_security_release=$2
    storage_security_commit=$3
    [ -f "$storage_security_policy_path" ] && [ ! -L "$storage_security_policy_path" ] \
        && [ -r "$storage_security_policy_path" ] \
        || storage_fail "MinIO security policy must be a readable regular non-symlink file"
    storage_require_command jq
    jq -e '
        def exactKeys($expected):
            type == "object" and (keys | sort) == ($expected | sort);
        exactKeys(["schemaVersion","kind","reviewedAt","approvedSourceReleases","blockingAdvisories"]) and
        .schemaVersion == 1 and
        .kind == "hook2stream-minio-security-policy" and
        (.reviewedAt | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")) and
        (.approvedSourceReleases | type == "array") and
        ([.approvedSourceReleases[] | (.release + ":" + .commit)] |
            length == (unique | length)) and
        ([.approvedSourceReleases[].securitySequence] |
            length == (unique | length)) and
        all(.approvedSourceReleases[];
            exactKeys(["release","commit","source","reviewedAt","securitySequence"]) and
            (.release | type == "string" and
                test("^RELEASE\\.[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}-[0-9]{2}-[0-9]{2}Z$")) and
            (.commit | type == "string" and test("^[0-9a-f]{40}$")) and
            .source == "https://github.com/minio/minio" and
            (.reviewedAt | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")) and
            (.securitySequence | type == "number" and . >= 1 and floor == .)
        ) and
        (.blockingAdvisories | type == "array" and length > 0) and
        ([.blockingAdvisories[].id] | length == (unique | length)) and
        all(.blockingAdvisories[];
            exactKeys(["id","severity","url","patchedOssRelease"]) and
            (.id | type == "string" and test("^CVE-[0-9]{4}-[0-9]{4,}$")) and
            (.severity == "high" or .severity == "critical") and
            (.url | type == "string" and
                test("^https://github\\.com/advisories/GHSA-[a-z0-9-]+$")) and
            .patchedOssRelease == null
        )
    ' "$storage_security_policy_path" >/dev/null \
        || storage_fail "MinIO security policy schema is invalid"
    storage_security_approval_count=$(jq -r \
        --arg release "$storage_security_release" --arg commit "$storage_security_commit" '
        [.approvedSourceReleases[] |
            select(.release == $release and .commit == $commit)] | length
    ' "$storage_security_policy_path")
    [ "$storage_security_approval_count" -eq 1 ] || {
        storage_security_blockers=$(jq -r \
            '[.blockingAdvisories[].id] | join(", ")' "$storage_security_policy_path")
        storage_fail "$storage_security_release@$storage_security_commit has no reviewed High/Critical-clean source approval; blockers: $storage_security_blockers"
    }
    jq -r --arg release "$storage_security_release" --arg commit "$storage_security_commit" '
        .approvedSourceReleases[] |
        select(.release == $release and .commit == $commit) |
        .securitySequence
    ' "$storage_security_policy_path"
}

storage_validate_minio_security_sequence() {
    [ "$#" -eq 2 ] \
        || storage_fail "storage_validate_minio_security_sequence requires candidate and minimum sequences"
    storage_candidate_security_sequence=$1
    storage_minimum_security_sequence=$2
    for storage_security_sequence in \
        "$storage_candidate_security_sequence" "$storage_minimum_security_sequence"; do
        case "$storage_security_sequence" in
            ''|*[!0-9]*|0) storage_fail "MinIO security sequence must be a positive integer" ;;
        esac
    done
    [ "$storage_candidate_security_sequence" -ge "$storage_minimum_security_sequence" ] \
        || storage_fail "MinIO security sequence downgrade is forbidden"
}

storage_validate_minio_security_transition() {
    [ "$#" -eq 6 ] \
        || storage_fail "storage_validate_minio_security_transition requires candidate/floor sequence and pins"
    storage_transition_candidate_sequence=$1
    storage_transition_candidate_release=$2
    storage_transition_candidate_commit=$3
    storage_transition_floor_sequence=$4
    storage_transition_floor_release=$5
    storage_transition_floor_commit=$6
    storage_validate_minio_security_sequence \
        "$storage_transition_candidate_sequence" "$storage_transition_floor_sequence"
    if [ "$storage_transition_candidate_sequence" -eq "$storage_transition_floor_sequence" ]; then
        [ "$storage_transition_candidate_release" = "$storage_transition_floor_release" ] \
            && [ "$storage_transition_candidate_commit" = "$storage_transition_floor_commit" ] \
            || storage_fail "equal MinIO security sequence cannot change the source pin"
    fi
}

storage_write_format_floor() {
    [ "$#" -eq 10 ] || storage_fail "storage_write_format_floor requires ten arguments"
    storage_floor_path=$1
    storage_floor_environment=$2
    storage_floor_protocol=$3
    storage_floor_format=$4
    storage_floor_security_sequence=$5
    storage_floor_object_format=$6
    storage_floor_minio_release=$7
    storage_floor_minio_source_commit=$8
    storage_floor_pending_sha=$9
    shift 9
    storage_floor_successful_sha=$1
    jq -cn \
        --arg environment "$storage_floor_environment" \
        --arg objectFormat "$storage_floor_object_format" \
        --arg minioRelease "$storage_floor_minio_release" \
        --arg minioSourceCommit "$storage_floor_minio_source_commit" \
        --arg pending "$storage_floor_pending_sha" \
        --arg successful "$storage_floor_successful_sha" \
        --argjson protocol "$storage_floor_protocol" \
        --argjson storageFormat "$storage_floor_format" \
        --argjson securitySequence "$storage_floor_security_sequence" \
        '{schemaVersion:1,kind:"hook2stream-storage-format-floor",environment:$environment,minimumProtocolVersion:$protocol,minimumStorageFormatVersion:$storageFormat,minimumMinioSecuritySequence:$securitySequence,objectFormat:$objectFormat,minioRelease:$minioRelease,minioSourceCommit:$minioSourceCommit,pendingReleaseSha:(if $pending == "" then null else $pending end),lastSuccessfulReleaseSha:(if $successful == "" then null else $successful end)}' \
        > "$storage_floor_path.tmp"
    chmod 0600 "$storage_floor_path.tmp"
    mv -f "$storage_floor_path.tmp" "$storage_floor_path"
}

storage_compose() {
    docker compose \
        --project-directory "$STORAGE_RELEASE_DIR/storage" \
        --env-file "$STORAGE_ACTIVE_ENV_FILE" \
        -p "hook2stream-storage-$DEPLOYMENT_ENVIRONMENT" \
        -f "$STORAGE_RELEASE_DIR/storage/compose.yaml" \
        "$@"
}
