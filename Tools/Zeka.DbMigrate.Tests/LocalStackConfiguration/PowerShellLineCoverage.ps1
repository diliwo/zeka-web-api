# Test infrastructure: records only actual source-file/line metadata, never commands, execution context or values.
# https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/set-psbreakpoint
function Invoke-PowerShellLineCoverage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string[]]$SourceFile,
        [Parameter(Mandatory)][string]$ReportFile,
        [Parameter(Mandatory)][scriptblock]$TestAction
    )
    $root = [IO.Path]::GetFullPath($SourceRoot)
    $report = [IO.Path]::GetFullPath($ReportFile)
    if (Test-Path -LiteralPath $report) { throw 'COVERAGE_REPORT_ALREADY_EXISTS' }
    if (@(Get-PSBreakpoint).Count -ne 0) { throw 'COVERAGE_EXISTING_BREAKPOINTS_REJECTED' }
    $owned = [Collections.Generic.List[object]]::new()
    $sources = [Collections.Generic.List[object]]::new()
    try {
        foreach ($file in $SourceFile) {
            $full = [IO.Path]::GetFullPath((Join-Path $root $file))
            $relative = [IO.Path]::GetRelativePath($root, $full).Replace('\', '/')
            if ($relative.StartsWith('../') -or [IO.Path]::IsPathRooted($relative) -or
                [IO.Path]::GetExtension($full) -ne '.ps1' -or !(Test-Path -LiteralPath $full -PathType Leaf)) {
                throw 'COVERAGE_SOURCE_PATH_REJECTED'
            }
            $tokens = $null
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile($full, [ref]$tokens, [ref]$errors)
            if (@($errors).Count -ne 0) { throw 'COVERAGE_SOURCE_PARSE_REJECTED' }
            # Omitted continuation lines are never invented as hits; statement starts are conservative candidates.
            $lines = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.StatementAst] -and
                $node -isnot [Management.Automation.Language.FunctionDefinitionAst] -and
                $node -isnot [Management.Automation.Language.TypeDefinitionAst]
            }, $true) | ForEach-Object { $_.Extent.StartLineNumber } | Sort-Object -Unique)
            $hitLines = [Collections.Generic.HashSet[int]]::new()
            $missingLocation = [pscustomobject]@{ Value=$false }
            # Breakpoint.Line is only the requested line and can bind to another sequence point.
            # Get-PSCallStack exposes the actual CurrentPosition through these two scalar properties.
            $action = {
                try {
                    $expectedFile = $_.Script
                    $found = $false
                    foreach ($frame in Get-PSCallStack) {
                        if ([StringComparer]::OrdinalIgnoreCase.Equals($frame.ScriptName, $expectedFile)) {
                            $lineNumber = $frame.ScriptLineNumber
                            if ($lineNumber -gt 0) { $null = $hitLines.Add($lineNumber); $found = $true }
                            break
                        }
                    }
                    if (!$found) { $missingLocation.Value = $true }
                } catch { $missingLocation.Value = $true }
                continue
            }.GetNewClosure()
            $breakpoints = @(Set-PSBreakpoint -Script $full -Line $lines -Action $action)
            foreach ($breakpoint in $breakpoints) { $owned.Add($breakpoint) }
            $sources.Add([pscustomobject]@{
                Path=$relative; FullPath=$full; Hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
                Lines=$lines; HitLines=$hitLines; MissingLocation=$missingLocation
            })
        }
        & $TestAction | Out-Null
    } finally {
        try {
            $settings = [Xml.XmlWriterSettings]::new()
            $settings.Indent = $true
            $settings.Encoding = [Text.UTF8Encoding]::new($false)
            $writer = [Xml.XmlWriter]::Create($report, $settings)
            try {
                $writer.WriteStartDocument()
                $writer.WriteStartElement('coverage')
                $writer.WriteAttributeString('version', '1')
                foreach ($source in $sources) {
                    if ((Get-FileHash -LiteralPath $source.FullPath -Algorithm SHA256).Hash -ne $source.Hash) {
                        throw 'COVERAGE_SOURCE_CHANGED_DURING_EXECUTION'
                    }
                    $writer.WriteStartElement('file')
                    $writer.WriteAttributeString('path', $source.Path)
                    if ($source.MissingLocation.Value) { throw 'COVERAGE_ACTUAL_EXECUTED_LOCATION_UNAVAILABLE' }
                    foreach ($lineNumber in @($source.Lines; $source.HitLines) | Sort-Object -Unique) {
                        $writer.WriteStartElement('lineToCover')
                        $writer.WriteAttributeString('lineNumber', $lineNumber.ToString([Globalization.CultureInfo]::InvariantCulture))
                        $writer.WriteAttributeString('covered', $source.HitLines.Contains([int]$lineNumber).ToString().ToLowerInvariant())
                        $writer.WriteEndElement()
                    }
                    $writer.WriteEndElement()
                }
                $writer.WriteEndElement()
                $writer.WriteEndDocument()
            } finally { $writer.Dispose() }
        } finally {
            foreach ($breakpoint in $owned) { Remove-PSBreakpoint -Id $breakpoint.Id }
        }
    }
    return @($sources | ForEach-Object {
        [pscustomobject]@{ path=$_.Path; sha256=$_.Hash.ToLowerInvariant() }
    })
}
