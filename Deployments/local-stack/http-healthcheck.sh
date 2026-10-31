#!/bin/bash
# No response body or configuration is written to Docker's health history.
set -euo pipefail
exec 3<>/dev/tcp/127.0.0.1/8080
printf 'GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
IFS= read -r -t 4 status <&3
[[ "$status" =~ ^HTTP/1\.[01]\ 200(\ |$) ]]
