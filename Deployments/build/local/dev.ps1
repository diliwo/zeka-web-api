param(
    [Parameter(Mandatory = $true)]
    [string]$Command,

    [string]$Service
)

# $env:GITHUB_TOKEN = op read "op://Diliwo Vault/Github Zeka Packages PAT/credential"

switch ($Command)
{
    "build" {
        op run --env-file .op/local.env -- docker compose build
    }

    "up" {
        #op run -- docker compose up
        op run -- docker compose up
    }

    "start" {
        op run -- docker compose up $Service
    }

    "down" {
        docker compose down -v
    }

    "logs" {
       docker compose logs -f $Services
    }

    "rebuild" {
        op run --env-file .op/local.env -- docker compose build $Service

        docker compose up -d $Service
    }

    default {
        Write-Host "Unknown command"
    }
}