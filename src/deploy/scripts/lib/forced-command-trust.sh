#!/bin/sh

hook2stream_trusted_directory() {
    [ "$#" -eq 3 ] || return 1
    trusted_path=$1
    trusted_owner_group=$2
    trusted_mode=$3
    [ -d "$trusted_path" ] && [ ! -L "$trusted_path" ] \
        && [ "$(stat -c '%u:%g:%a' "$trusted_path")" = \
            "$trusted_owner_group:$trusted_mode" ]
}

hook2stream_trusted_file() {
    [ "$#" -eq 3 ] || return 1
    trusted_path=$1
    trusted_owner_group=$2
    trusted_mode=$3
    [ -f "$trusted_path" ] && [ ! -L "$trusted_path" ] \
        && [ "$(stat -c '%u:%g:%a' "$trusted_path")" = \
            "$trusted_owner_group:$trusted_mode" ]
}
