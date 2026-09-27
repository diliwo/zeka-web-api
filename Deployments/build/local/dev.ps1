param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("build", "up", "start", "down", "logs", "rebuild")]
    [string]$Command,

    [string]$Service
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$envTemplate = Join-Path $PSScriptRoot ".op/local.env"

function Invoke-Compose {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$WithSecrets
    )

    if ($WithSecrets) {
        & op run "--env-file=$envTemplate" -- docker compose --project-directory $repositoryRoot @Arguments
    }
    else {
        & docker compose --project-directory $repositoryRoot @Arguments
    }

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

switch ($Command) {
    "build" {
        $arguments = @("build")
        if (-not [string]::IsNullOrWhiteSpace($Service)) {
            $arguments += $Service
        }

        Invoke-Compose -WithSecrets -Arguments $arguments
    }

    "up" {
        Invoke-Compose -WithSecrets -Arguments @("up", "-d")
    }

    "start" {
        if ([string]::IsNullOrWhiteSpace($Service)) {
            Write-Error "Usage: .\dev.ps1 start <service>"
            exit 1
        }

        Invoke-Compose -WithSecrets -Arguments @("up", "-d", $Service)
    }

    "down" {
        Invoke-Compose -Arguments @("down")
    }

    "logs" {
        $arguments = @("logs", "--follow")
        if (-not [string]::IsNullOrWhiteSpace($Service)) {
            $arguments += $Service
        }

        Invoke-Compose -Arguments $arguments
    }

    "rebuild" {
        if ([string]::IsNullOrWhiteSpace($Service)) {
            Write-Error "Usage: .\dev.ps1 rebuild <service>"
            exit 1
        }

        Invoke-Compose -WithSecrets -Arguments @("build", $Service)
        Invoke-Compose -WithSecrets -Arguments @("up", "-d", $Service)
    }
}
