param(
    [Parameter(Mandatory = $true)]
    [string]$Command,

    [string]$Service
)

$env:GITHUB_TOKEN = op read "op://Diliwo Vault/Github Zeka Packages PAT/credential"

switch ($Command)
{
    "build" {
        op run -- docker compose build
    }

    "up" {
        op run -- docker compose up
    }

    "down" {
        docker compose down
    }

    "logs" {
       docker compose logs -f $Services
    }

    "rebuild" {
        op run -- docker compose build $Service

        docker compose up -d $Service
    }

    default {
        Write-Host "Unknown command"
    }
}