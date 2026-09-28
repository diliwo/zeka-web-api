param(
    [Parameter(Mandatory = $true)]
    [string]$Command,

    [string]$Service
)

$ScriptDirectory = (Resolve-Path $PSScriptRoot).Path
$RepositoryRoot = (Resolve-Path (Join-Path $ScriptDirectory "../../..")).Path
$EnvironmentFile = (Resolve-Path (Join-Path $ScriptDirectory ".op/local.env")).Path

# $env:GITHUB_TOKEN = op read "op://Diliwo Vault/Github Zeka Packages PAT/credential"

switch ($Command)
{
    "build" {
        $ComposeArguments = @(
            "compose"
            "--project-directory"
            $RepositoryRoot
            "build"
        )

        if (-not [string]::IsNullOrWhiteSpace($Service)) {
            $ComposeArguments += $Service
        }

        & op run --env-file $EnvironmentFile -- docker @ComposeArguments
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
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