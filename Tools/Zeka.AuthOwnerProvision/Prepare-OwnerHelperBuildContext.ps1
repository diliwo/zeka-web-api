[CmdletBinding()]
param([Alias('PublishDirectory')][string]$OwnerHelperInputPublishDirectory,
    [Alias('ContextDirectory')][string]$OwnerHelperOutputContextDirectory,
    [Alias('SourceCommit')][string]$OwnerHelperRequestedSourceCommit,
    [Alias('BinarySha256')][string]$OwnerHelperExpectedBinarySha256,
    [Alias('DefinitionsOnly')][switch]$OwnerHelperDefinitionsOnly)
$script:OwnerHelperSourceDirectory=[IO.Path]::GetFullPath($PSScriptRoot)
function Stop-OwnerHelperContext([string]$Code){throw [InvalidOperationException]::new($Code)}
function Get-OwnerHelperBuildInventory {
    [pscustomobject]@{
        RuntimeFiles=@(
            'AuthManager.Application.dll','AuthManager.Core.dll','AuthManager.Infrastructure.dll',
            'FluentValidation.DependencyInjectionExtensions.dll','FluentValidation.dll',
            'Microsoft.AspNetCore.Authentication.JwtBearer.dll','Microsoft.AspNetCore.Cryptography.Internal.dll',
            'Microsoft.AspNetCore.Cryptography.KeyDerivation.dll','Microsoft.AspNetCore.Identity.EntityFrameworkCore.dll',
            'Microsoft.EntityFrameworkCore.Abstractions.dll','Microsoft.EntityFrameworkCore.dll','Microsoft.EntityFrameworkCore.Relational.dll',
            'Microsoft.Extensions.Identity.Core.dll','Microsoft.Extensions.Identity.Stores.dll','Microsoft.Extensions.Options.dll',
            'Microsoft.IdentityModel.Abstractions.dll','Microsoft.IdentityModel.JsonWebTokens.dll','Microsoft.IdentityModel.Logging.dll',
            'Microsoft.IdentityModel.Protocols.dll','Microsoft.IdentityModel.Protocols.OpenIdConnect.dll','Microsoft.IdentityModel.Tokens.dll',
            'NodaTime.dll','Npgsql.dll','Npgsql.EntityFrameworkCore.PostgreSQL.dll','System.IdentityModel.Tokens.Jwt.dll',
            'zeka-auth-owner-provision.deps.json','zeka-auth-owner-provision.dll','zeka-auth-owner-provision.runtimeconfig.json',
            'Zeka.Authentication.dll','Zeka.Extensions.MultiTenancy.Abstractions.dll','Zeka.Lifecycle.Contracts.dll'
        )
        PublishOnlyFiles=@('AuthManager.Application.pdb','AuthManager.Core.pdb','AuthManager.Infrastructure.pdb',
            'zeka-auth-owner-provision','zeka-auth-owner-provision.pdb','Zeka.Authentication.pdb','Zeka.Lifecycle.Contracts.pdb')
        ControlFiles=@('Dockerfile','.dockerignore')
    }
}
function Assert-OwnerHelperNames([string[]]$Names,[string[]]$Expected){
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($name in $Names){if(!$seen.Add($name)-or$name.Contains('/')-or$name.Contains('\')-or$name.Contains('..')-or
        ($Expected -cnotcontains $name)){Stop-OwnerHelperContext 'OWNER_HELPER_UNEXPECTED_FILE'}}
    if($seen.Count-ne$Expected.Count){Stop-OwnerHelperContext 'OWNER_HELPER_REQUIRED_FILE_MISSING'}
}
function Get-OwnerHelperDockerIgnore {
    $inventory=Get-OwnerHelperBuildInventory
    return "**`n"+((@($inventory.RuntimeFiles)+@($inventory.ControlFiles)|ForEach-Object {'!'+$_})-join"`n")+"`n"
}
function Assert-OwnerHelperDirectoryFiles([string]$Directory,[string[]]$Expected){
    $entries=@(Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop)
    foreach($entry in $entries){if($entry.PSIsContainer-or($entry.Attributes-band[IO.FileAttributes]::ReparsePoint)){Stop-OwnerHelperContext 'OWNER_HELPER_NONREGULAR_FILE'}}
    Assert-OwnerHelperNames @($entries.Name) $Expected
}
function Assert-OwnerHelperPathFacts($Facts){
    if($Facts.SourceInsideRepository-or$Facts.ContextInsideRepository-or$Facts.SourceInsidePrivate-or$Facts.ContextInsidePrivate-or
        $Facts.SourceContextOverlap-or$Facts.ReparseFound){Stop-OwnerHelperContext 'OWNER_HELPER_PATH_BOUNDARY_REJECTED'}
}
function Get-OwnerHelperAbsolutePath([string]$Path){
    $unc=([string][char]92)+[char]92
    if([string]::IsNullOrWhiteSpace($Path)-or![IO.Path]::IsPathFullyQualified($Path)-or
        ($Path.Split([char[]]@([char]47,[char]92)) -contains '..')-or$Path.StartsWith($unc)-or$Path.StartsWith('//')){
        Stop-OwnerHelperContext 'OWNER_HELPER_PATH_REJECTED'}
    $full=[IO.Path]::GetFullPath($Path);$trimmed=$full.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    if($trimmed-eq[IO.Path]::GetPathRoot($full).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)){
        Stop-OwnerHelperContext 'OWNER_HELPER_PATH_REJECTED'}
    return $trimmed
}
function Test-OwnerHelperInside([string]$Child,[string]$Parent){
    $boundary=$Parent.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    return [StringComparer]::OrdinalIgnoreCase.Equals($Child.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar),$boundary)-or
        $Child.StartsWith($boundary+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
}
function Assert-OwnerHelperNoReparse([string]$Path){
    $current=$Path
    while($current){if((Test-Path -LiteralPath $current)-and((Get-Item -LiteralPath $current -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){
        Stop-OwnerHelperContext 'OWNER_HELPER_PATH_BOUNDARY_REJECTED'}
        $parent=Split-Path -Parent $current;if($parent-eq$current){break};$current=$parent}
}
function Assert-OwnerHelperPreparedContext([string]$ContextDirectory,$Receipt){
    $context=Get-OwnerHelperAbsolutePath $ContextDirectory;$inventory=Get-OwnerHelperBuildInventory
    Assert-OwnerHelperNoReparse $context;Assert-OwnerHelperDirectoryFiles $context (@($inventory.RuntimeFiles)+@($inventory.ControlFiles))
    if($Receipt.schema-cne'zeka-m1-owner-helper-build-context/v1'-or$Receipt.status-cne'PREPARED'-or$Receipt.contextDirectory-cne$context){
        Stop-OwnerHelperContext 'OWNER_HELPER_RECEIPT_REJECTED'}
    Assert-OwnerHelperNames @($Receipt.runtimeFiles|ForEach-Object {$_.name}) $inventory.RuntimeFiles
    foreach($file in $Receipt.runtimeFiles){if((Get-FileHash -LiteralPath (Join-Path $context $file.name) -Algorithm SHA256).Hash.ToLowerInvariant()-cne$file.sha256){
        Stop-OwnerHelperContext 'OWNER_HELPER_FILE_CHANGED'}}
    $ignore=Join-Path $context '.dockerignore';$dockerfile=Join-Path $context 'Dockerfile'
    if([IO.File]::ReadAllText($ignore)-cne(Get-OwnerHelperDockerIgnore)-or
        (Get-FileHash -LiteralPath $ignore -Algorithm SHA256).Hash.ToLowerInvariant()-cne$Receipt.dockerignoreSha256-or
        (Get-FileHash -LiteralPath $dockerfile -Algorithm SHA256).Hash.ToLowerInvariant()-cne$Receipt.dockerfileSha256-or
        (Get-FileHash -LiteralPath $dockerfile -Algorithm SHA256).Hash-ne(Get-FileHash -LiteralPath (Join-Path $script:OwnerHelperSourceDirectory 'Dockerfile') -Algorithm SHA256).Hash){
        Stop-OwnerHelperContext 'OWNER_HELPER_CONTROL_FILE_CHANGED'}
}
function Prepare-OwnerHelperBuildContext {
    param([string]$PublishDirectory,[string]$ContextDirectory,[string]$SourceCommit,[string]$BinarySha256)
    $ErrorActionPreference='Stop'
    if($SourceCommit-cnotmatch'^[0-9a-f]{40}$'-or$BinarySha256-cnotmatch'^[0-9a-f]{64}$'){Stop-OwnerHelperContext 'OWNER_HELPER_PROVENANCE_REJECTED'}
    $source=Get-OwnerHelperAbsolutePath $PublishDirectory;$context=Get-OwnerHelperAbsolutePath $ContextDirectory
    $repository=[IO.Path]::GetFullPath((Join-Path $script:OwnerHelperSourceDirectory '../..'))
    $private=$env:ZEKA_M1_PRIVATE_DIR
    $facts=@{SourceInsideRepository=(Test-OwnerHelperInside $source $repository);ContextInsideRepository=(Test-OwnerHelperInside $context $repository);
        SourceInsidePrivate=($source-match'(?i)[\\/]Zeka[\\/]M1[\\/]private(?:[\\/]|$)');
        ContextInsidePrivate=($context-match'(?i)[\\/]Zeka[\\/]M1[\\/]private(?:[\\/]|$)');
        SourceContextOverlap=((Test-OwnerHelperInside $source $context)-or(Test-OwnerHelperInside $context $source));ReparseFound=$false}
    if($private-and[IO.Path]::IsPathFullyQualified($private)){$private=[IO.Path]::GetFullPath($private);
        $facts.SourceInsidePrivate=$facts.SourceInsidePrivate-or(Test-OwnerHelperInside $source $private)
        $facts.ContextInsidePrivate=$facts.ContextInsidePrivate-or(Test-OwnerHelperInside $context $private)}
    Assert-OwnerHelperPathFacts $facts;Assert-OwnerHelperNoReparse $source;Assert-OwnerHelperNoReparse $context
    if(!(Test-Path -LiteralPath $source -PathType Container)){Stop-OwnerHelperContext 'OWNER_HELPER_PUBLISH_DIRECTORY_REQUIRED'}
    if(Test-Path -LiteralPath $context){Stop-OwnerHelperContext 'OWNER_HELPER_CONTEXT_EXISTS'}
    $inventory=Get-OwnerHelperBuildInventory;$expected=@($inventory.RuntimeFiles)+@($inventory.PublishOnlyFiles)
    Assert-OwnerHelperDirectoryFiles $source $expected
    $binary=Join-Path $source 'zeka-auth-owner-provision.dll'
    if((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()-cne$BinarySha256){Stop-OwnerHelperContext 'OWNER_HELPER_BINARY_MISMATCH'}
    $hashes=@{};foreach($name in $inventory.RuntimeFiles){$hashes[$name]=(Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash.ToLowerInvariant()}
    [IO.Directory]::CreateDirectory($context)|Out-Null
    foreach($name in $inventory.RuntimeFiles){Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $context $name) -ErrorAction Stop}
    $dockerfile=Join-Path $script:OwnerHelperSourceDirectory 'Dockerfile';Copy-Item -LiteralPath $dockerfile -Destination (Join-Path $context 'Dockerfile') -ErrorAction Stop
    [IO.File]::WriteAllText((Join-Path $context '.dockerignore'),(Get-OwnerHelperDockerIgnore),[Text.UTF8Encoding]::new($false))
    Assert-OwnerHelperDirectoryFiles $source $expected
    Assert-OwnerHelperDirectoryFiles $context (@($inventory.RuntimeFiles)+@($inventory.ControlFiles))
    $files=@();foreach($name in $inventory.RuntimeFiles){
        if((Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash.ToLowerInvariant()-cne$hashes[$name]-or
            (Get-FileHash -LiteralPath (Join-Path $context $name) -Algorithm SHA256).Hash.ToLowerInvariant()-cne$hashes[$name]){Stop-OwnerHelperContext 'OWNER_HELPER_FILE_CHANGED'}
        $files+=@{name=$name;sha256=$hashes[$name]}}
    $receipt=[pscustomobject]@{schema='zeka-m1-owner-helper-build-context/v1';status='PREPARED';sourceCommit=$SourceCommit;binarySha256=$BinarySha256;
        contextDirectory=$context;runtimeFiles=$files;excludedPublishOnlyFiles=$inventory.PublishOnlyFiles;
        dockerfileSha256=(Get-FileHash -LiteralPath (Join-Path $context 'Dockerfile') -Algorithm SHA256).Hash.ToLowerInvariant();
        dockerignoreSha256=(Get-FileHash -LiteralPath (Join-Path $context '.dockerignore') -Algorithm SHA256).Hash.ToLowerInvariant()}
    Assert-OwnerHelperPreparedContext $context $receipt
    return $receipt
}
if(!$OwnerHelperDefinitionsOnly){try{Prepare-OwnerHelperBuildContext -PublishDirectory $OwnerHelperInputPublishDirectory -ContextDirectory $OwnerHelperOutputContextDirectory -SourceCommit $OwnerHelperRequestedSourceCommit -BinarySha256 $OwnerHelperExpectedBinarySha256|ConvertTo-Json -Depth 6 -Compress}
    catch{$code=if($_.Exception-is[InvalidOperationException]-and$_.Exception.Message-cmatch'^OWNER_HELPER_[A-Z_]+$'){$_.Exception.Message}else{'OWNER_HELPER_CONTEXT_FAILED'};
        [ordered]@{status='BLOCKED';code=$code}|ConvertTo-Json -Compress;exit 1}}
