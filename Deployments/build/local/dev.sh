#!/usr/bin/env bash
set -euo pipefail

command_name="${1:-}"
service_name="${2:-}"

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_template="${project_dir}/.op/local.env"

export DOCKER_HOST="${DOCKER_HOST:-unix:///run/user/$(id -u)/docker.sock}"

compose_with_secrets() {
    op run \
        --env-file="${env_template}" \
        -- docker compose --project-directory "${project_dir}" "$@"
}

case "${command_name}" in
    build)
        if [[ -n "${service_name}" ]]; then
            compose_with_secrets build "${service_name}"
        else
            compose_with_secrets build
        fi
        ;;

    up)
        compose_with_secrets up -d
        ;;

    start)
        if [[ -z "${service_name}" ]]; then
            echo "Usage: ./dev.sh start <service>"
            exit 1
        fi

        compose_with_secrets up -d "${service_name}"
        ;;

    down)
        docker compose \
            --project-directory "${project_dir}" \
            down
        ;;

    logs)
        if [[ -n "${service_name}" ]]; then
            docker compose \
                --project-directory "${project_dir}" \
                logs --follow "${service_name}"
        else
            docker compose \
                --project-directory "${project_dir}" \
                logs --follow
        fi
        ;;

    rebuild)
        if [[ -z "${service_name}" ]]; then
            echo "Usage: ./dev.sh rebuild <service>"
            exit 1
        fi

        compose_with_secrets build "${service_name}"
        compose_with_secrets up -d "${service_name}"
        ;;

    *)
        echo "Usage: ./dev.sh {build|up|start|down|logs|rebuild} [service]"
        exit 1
        ;;
esac