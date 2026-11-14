#!/usr/bin/env bash
set -euo pipefail

version=8.0.129
install_dir=/opt/dotnet-$version
archive=$(mktemp)
trap 'rm -f "$archive"' EXIT

curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    --output "$archive" \
    "https://builds.dotnet.microsoft.com/dotnet/Sdk/$version/dotnet-sdk-$version-linux-x64.tar.gz"
# Microsoft .NET 8.0.29 release checksums for the Linux x64 SDK.
expected_sha512=29a9620b9450a7547148c9d5431fe25ac0b736c3f2003fcb2512b6733efbe1a66c5201c6acf47f8bb2361d7cff32939f10e991d2b8cbdd833f7b33ac99e345c7
printf '%s  %s\n' "$expected_sha512" "$archive" | sha512sum --check
mkdir "$install_dir"
tar --extract --gzip --file "$archive" --directory "$install_dir" --no-same-owner
test "$("$install_dir/dotnet" --version)" = "$version"
