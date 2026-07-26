#!/usr/bin/env bash
set -euo pipefail

output_root=${1:?"Usage: generate-e2e-media.sh OUTPUT_DIRECTORY"}
mkdir -p "$output_root"

ffmpeg \
  -y \
  -v error \
  -f lavfi \
  -i "sine=frequency=440:sample_rate=48000:duration=45" \
  -af "volume=0.15" \
  -c:a libmp3lame \
  -b:a 192k \
  -metadata artist="Playwright Artist" \
  -metadata title="End to End Signal" \
  "$output_root/fixture-master.mp3"

ffmpeg \
  -y \
  -v error \
  -f lavfi \
  -i "sine=frequency=523.25:sample_rate=48000:duration=45" \
  -af "volume=0.15" \
  -c:a pcm_s16le \
  "$output_root/fixture-master.wav"

sha256sum \
  "$output_root/fixture-master.mp3" \
  "$output_root/fixture-master.wav" \
  > "$output_root/media.sha256"

ffprobe \
  -v error \
  -show_entries format=duration,format_name:stream=codec_name,codec_type,sample_rate \
  -of json \
  "$output_root/fixture-master.mp3" \
  > "$output_root/fixture-master.mp3.ffprobe.json"

ffprobe \
  -v error \
  -show_entries format=duration,format_name:stream=codec_name,codec_type,sample_rate \
  -of json \
  "$output_root/fixture-master.wav" \
  > "$output_root/fixture-master.wav.ffprobe.json"
