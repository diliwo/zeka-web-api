[CmdletBinding()]
param(
    [switch]$OwnerAttestedIdentity, [switch]$PrepareOnly, [switch]$DefinitionsOnly,
    [string]$PrivateDirectory=$env:ZEKA_M1_PRIVATE_DIR,
    [string]$SourceCommit, [string]$BinarySha256, [string]$ToolBinaryPath, [string]$ToolImage,
    [Guid]$ExistingSubjectId=[Guid]::Empty,
    [string]$DisplayName='M1 synthetic first organisation'
)
function Stop-OwnerProvision([string]$Code) { throw [InvalidOperationException]::new($Code) }
function Assert-OwnerIdentityFacts($Facts, [string]$PinnedSid='') {
    if (!$Facts.isWindows -or !$Facts.attested -or
        ![StringComparer]::OrdinalIgnoreCase.Equals($Facts.account,'DILI_LP\herve') -or
        ![StringComparer]::OrdinalIgnoreCase.Equals($Facts.userProfile,'C:\Users\herve') -or
        $Facts.sid -cnotmatch '^S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+$' -or
        ($PinnedSid -and $Facts.sid -cne $PinnedSid)) { Stop-OwnerProvision 'OWNER_IDENTITY_ADMISSION_REJECTED' }
}
function Assert-OwnerSourceFacts($Facts, [bool]$RequireBinary=$true) {
    if ($Facts.branch -cne 'codex/m1-c1-integration' -or !$Facts.clean -or
        $Facts.actualSource -cnotmatch '^[0-9a-f]{40}$' -or $Facts.actualSource -cne $Facts.expectedSource -or
        ($RequireBinary -and ($Facts.actualBinary -cnotmatch '^[0-9a-f]{64}$' -or $Facts.actualBinary -cne $Facts.expectedBinary))) {
        Stop-OwnerProvision 'OWNER_SOURCE_OR_BINARY_REJECTED'
    }
}
function Assert-OwnerFileFacts($Facts) {
    if ($Facts.reparse -or !$Facts.protected -or !$Facts.outsideSource) { Stop-OwnerProvision 'OWNER_PRIVATE_PATH_REJECTED' }
}
function Get-OwnerPreparationState([bool]$ProfileExists,[bool]$PasswordExists,[bool]$CredentialSaved) {
    if (!$ProfileExists -and $PasswordExists) { Stop-OwnerProvision 'OWNER_ORPHAN_CREDENTIAL_REJECTED' }
    if (!$ProfileExists) { return 'NEW' }
    if ($CredentialSaved -and !$PasswordExists) { Stop-OwnerProvision 'OWNER_SAVED_CREDENTIAL_MISSING' }
    if (!$CredentialSaved -or !$PasswordExists) { Stop-OwnerProvision 'OWNER_PREPARATION_REQUIRES_INSPECTION' }
    return 'SAVED'
}
function Assert-OwnerProfileFacts($Profile,$Identity,$Progress) {
    Assert-OwnerIdentityFacts $Identity $Profile.ownerSid
    $subject=[Guid]::Empty
    if ($Profile.schema -cne 'zeka-m1-owner-admission/v1' -or $Profile.scope -cne 'LocalSyntheticM1OwnerProvision' -or
        $Profile.ownerDecisionId -cne 'a7daf3ea-6370-4604-9e4b-e5eb206465c7' -or $Profile.approvalCommentId -cne '6529021741' -or
        $Profile.ownerSid -cne $Identity.sid -or $Profile.ownerAccount -cne 'DILI_LP\herve' -or $Profile.ownerProfile -cne 'C:\Users\herve' -or
        $Profile.normalizedEmail -cne 'HERVE.MBILO@GMAIL.COM' -or $Profile.project -cne 'zeka-m1' -or
        [string]::IsNullOrWhiteSpace($Profile.displayName) -or $Profile.displayName.Length -gt 200 -or
        $Profile.initializationId -cne $Progress.initializationId -or $Profile.databaseSourceCommit -cne $Progress.sourceCommit -or
        $Profile.containerId -cne $Progress.containerId -or $Profile.networkId -cne $Progress.networkId -or
        $Profile.clusterId -cne $Progress.clusterId -or $Profile.volumeCreated -ne $Progress.volumeCreated -or
        $Profile.idempotencyKey -cne ($Progress.initializationId+':first-admin:v1') -or
        ![Guid]::TryParse([string]$Profile.subjectId,[ref]$subject) -or $subject -eq [Guid]::Empty) {
        Stop-OwnerProvision 'OWNER_PROFILE_BINDING_REJECTED'
    }
}
function Assert-OwnerResourceFacts($Facts,$Expected) {
    foreach($key in @('containerId','networkId','volumeCreated','initializationId')) {
        if ($Facts.$key -ne $Expected.$key) { Stop-OwnerProvision 'OWNER_RESOURCE_ID_DRIFT' }
    }
    if ($Facts.context -cne 'default' -or $Facts.engineId -cne '0b62f5ec-15d9-4e4b-a450-817d52378eef' -or
        $Facts.containerName -cne 'zeka-m1-postgres-1' -or $Facts.volumeName -cne 'zeka-m1_postgres_data' -or
        $Facts.networkName -cne 'zeka-m1_internal' -or !$Facts.internal -or $Facts.driver -cne 'bridge' -or
        !$Facts.unpublished -or $Facts.hostNetwork -or $Facts.networkCount -ne 1 -or
        $Facts.attachedNetworkId -cne $Expected.networkId -or $Facts.dataVolumeName -cne 'zeka-m1_postgres_data' -or
        !$Facts.dataWritable -or $Facts.postgresImage -cne 'postgres:17') { Stop-OwnerProvision 'OWNER_RESOURCE_CONFIGURATION_REJECTED' }
    foreach($labels in @($Facts.containerLabels,$Facts.networkLabels,$Facts.volumeLabels)) {
        if ($labels.'com.docker.compose.project' -cne 'zeka-m1' -or $labels.'com.diliwo.zeka.owner' -cne 'windows-codex-m1' -or
            $labels.'com.diliwo.zeka.initialization-id' -cne $Expected.initializationId) { Stop-OwnerProvision 'OWNER_RESOURCE_LABEL_DRIFT' }
    }
}
function Assert-OwnerImageFacts($Image,[string]$Source,[string]$Binary,[string]$ImageId) {
    $labels=$Image.Config.Labels
    if($Image.Id-cne$ImageId-or$Image.Config.User-cne'app'-or$Image.Config.WorkingDir-cne'/app'-or
        @($Image.Config.Entrypoint).Count-ne2-or$Image.Config.Entrypoint[0]-cne'dotnet'-or$Image.Config.Entrypoint[1]-cne'/app/zeka-auth-owner-provision.dll'-or
        ($null-ne$Image.Config.Cmd-and@($Image.Config.Cmd).Count-ne0)-or$labels.'org.opencontainers.image.revision'-cne$Source-or
        $labels.'com.diliwo.zeka.binary-sha256'-cne$Binary-or$labels.'com.diliwo.zeka.scope'-cne'm1-owner-provision'-or
        $labels.'com.diliwo.zeka.owner'-cne'windows-codex-m1'-or$labels.'com.docker.compose.project'-cne'zeka-m1'){
        Stop-OwnerProvision 'OWNER_IMAGE_PROVENANCE_REJECTED'
    }
}
function Assert-OwnerHelperIsolationFacts($Helper) {
    $temporary=@($Helper.HostConfig.Tmpfs.PSObject.Properties)
    if(!$Helper.HostConfig.ReadonlyRootfs-or$temporary.Count-ne1-or$temporary[0].Name-cne'/tmp'-or
        $temporary[0].Value-cne'rw,noexec,nosuid,nodev,size=16777216,mode=1777'-or
        ($null-ne$Helper.HostConfig.Binds-and@($Helper.HostConfig.Binds).Count-ne0)-or
        ($null-ne$Helper.HostConfig.Mounts-and@($Helper.HostConfig.Mounts).Count-ne0)-or
        $Helper.HostConfig.Privileged-or$Helper.HostConfig.NetworkMode-eq'host'-or
        @($Helper.HostConfig.PortBindings.PSObject.Properties).Count-ne0-or
        @($Helper.HostConfig.CapDrop).Count-ne1-or$Helper.HostConfig.CapDrop[0]-cne'ALL'-or
        ($null-ne$Helper.HostConfig.CapAdd-and@($Helper.HostConfig.CapAdd).Count-ne0)-or
        @($Helper.HostConfig.SecurityOpt).Count-ne1-or$Helper.HostConfig.SecurityOpt[0]-cne'no-new-privileges'){
        Stop-OwnerProvision 'OWNER_HELPER_ISOLATION_REJECTED'
    }
    # --tmpfs is represented in HostConfig.Tmpfs; engines may also report its mounted view.
    $mounts=@($Helper.Mounts)
    if($mounts.Count-gt1){Stop-OwnerProvision 'OWNER_HELPER_ISOLATION_REJECTED'}
    foreach($mount in $mounts){
        if($mount.Type-cne'tmpfs'-or$mount.Destination-cne'/tmp'-or$mount.Source-or!$mount.RW){
            Stop-OwnerProvision 'OWNER_HELPER_ISOLATION_REJECTED'
        }
    }
}

function Get-OwnerHelperNetworkDecision($Helper,$Expected,[switch]$BeforeStart) {
    $networks=if($null-eq$Helper.NetworkSettings.Networks){@()}else{@($Helper.NetworkSettings.Networks.PSObject.Properties)}
    if($Helper.Id-cne$Expected.containerId-or$Helper.Name.TrimStart('/')-cne$Expected.name-or$Helper.Image-cne$Expected.image-or
        $Helper.HostConfig.NetworkMode-cne'zeka-m1_internal'-or
        $Helper.Config.Labels.'com.diliwo.zeka.operation-id'-cne$Expected.operationId-or
        $Helper.Config.Labels.'com.diliwo.zeka.initialization-id'-cne$Expected.initializationId-or
        $Helper.Config.Labels.'com.diliwo.zeka.owner'-cne'windows-codex-m1'-or
        $Helper.Config.Labels.'com.docker.compose.project'-cne'zeka-m1'-or$networks.Count-gt1-or
        ($networks.Count-eq1-and($networks[0].Name-cne'zeka-m1_internal'-or
            ($networks[0].Value.NetworkID-and$networks[0].Value.NetworkID-cne$Expected.networkId)))){
        Stop-OwnerProvision 'OWNER_HELPER_NETWORK_REJECTED'
    }
    if($BeforeStart){
        if($Helper.State.Status-cne'created'){Stop-OwnerProvision 'OWNER_HELPER_NETWORK_REJECTED'}
        return 'CREATED'
    }
    if($Helper.State.Status-ceq'created'){return 'WAIT'}
    if($Helper.State.Status-cne'running'-or$networks.Count-ne1-or$networks[0].Value.NetworkID-cne$Expected.networkId){
        Stop-OwnerProvision 'OWNER_HELPER_NETWORK_REJECTED'
    }
    return 'RUNNING'
}
function Read-OwnerPreStdinHelper([string]$ContainerId,[int]$TimeoutMs) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $arguments=@('container','inspect','--format','{{json .}}',$ContainerId)
    $decision=Assert-W3DockerArguments $arguments
    if(!$decision.Allowed){Write-W3GuardRecord -Rule $decision.ReasonCode -Operation 'owner-provision.network-pre-stdin'|Out-Null;Stop-OwnerProvision 'OWNER_HELPER_NETWORK_REJECTED'}
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command docker -ErrorAction Stop).Source)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.WorkingDirectory=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @('--context','default')+$arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    $meta=@{operation='owner-provision.network-pre-stdin';containerId=$ContainerId;nativeStarted=$false;nativePid=$null;nativeExit=$null;stdoutWithheld=$true;stderrWithheld=$true}
    try {
        $null=$process.Start();$meta.nativeStarted=$true;$meta.nativePid=$process.Id
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $remaining=$TimeoutMs-[int]$clock.ElapsedMilliseconds
        if($remaining-le0-or!$process.WaitForExit($remaining)){Stop-OwnerProvision 'OWNER_HELPER_NETWORK_DEADLINE'}
        $meta.nativeExit=$process.ExitCode
        $remaining=$TimeoutMs-[int]$clock.ElapsedMilliseconds
        if($remaining-le0-or!([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout,$stderr))).Wait($remaining)){Stop-OwnerProvision 'OWNER_HELPER_NETWORK_DEADLINE'}
        $meta.stdoutCharacters=$stdout.Result.Length;$meta.stderrCharacters=$stderr.Result.Length
        if($process.ExitCode-ne0-or$stderr.Result.Length-ne0-or$stdout.Result.Length-gt65536){Stop-OwnerProvision 'OWNER_HELPER_NETWORK_REJECTED'}
        return $stdout.Result|ConvertFrom-Json
    } finally {
        if($meta.nativeStarted-and!$process.HasExited){try{$process.Kill($true)}catch{}}
        Write-W3DiagnosticRecord -Record $meta|Out-Null
        $process.Dispose()
    }
}

function Wait-OwnerHelperRunningNetwork($Process,$Expected) {
    $gate=[Diagnostics.Stopwatch]::StartNew()
    while($gate.ElapsedMilliseconds-lt5000){
        if($Process.HasExited){Stop-OwnerProvision 'OWNER_HELPER_EXITED_BEFORE_STDIN'}
        $remaining=5000-[int]$gate.ElapsedMilliseconds
        $helper=Read-OwnerPreStdinHelper $Expected.containerId ([Math]::Min(1000,$remaining))
        if($gate.ElapsedMilliseconds-ge5000){Stop-OwnerProvision 'OWNER_HELPER_NETWORK_DEADLINE'}
        Assert-OwnerHelperIsolationFacts $helper
        $decision=Get-OwnerHelperNetworkDecision $helper $Expected
        if($decision-ceq'RUNNING'){
            if($Process.HasExited){Stop-OwnerProvision 'OWNER_HELPER_EXITED_BEFORE_STDIN'}
            return
        }
        $remaining=5000-[int]$gate.ElapsedMilliseconds
        if($remaining-gt0){[Threading.Thread]::Sleep([Math]::Min(50,$remaining))}
    }
    Stop-OwnerProvision 'OWNER_HELPER_NETWORK_DEADLINE'
}

function Read-OwnerPublicCommand([string]$Exe,[string[]]$Arguments,[int]$TimeoutMs=15000) {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command $Exe -ErrorAction Stop).Source)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try {
        $null=$process.Start();$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(!$process.WaitForExit($TimeoutMs)){$process.Kill($true);$null=$process.WaitForExit(5000);Stop-OwnerProvision 'OWNER_PUBLIC_OPERATION_UNCERTAIN'}
        if(!([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout,$stderr))).Wait(5000)){Stop-OwnerProvision 'OWNER_PUBLIC_OPERATION_UNCERTAIN'}
        return [pscustomobject]@{exit=$process.ExitCode;out=$stdout.Result}
    } finally {$process.Dispose()}
}
function Invoke-OwnerDocker([string[]]$Arguments,[int]$TimeoutMs=15000,[bool]$AllowFailure=$false) {
    $decision=Assert-W3DockerArguments -ArgumentList $Arguments
    if(!$decision.Allowed){Write-W3GuardRecord -Rule $decision.ReasonCode -Operation 'owner-provision'|Out-Null;Stop-OwnerProvision 'OWNER_DOCKER_ARGUMENTS_REJECTED'}
    return Invoke-W3Native -Exe docker -ArgumentList (@('--context','default')+$Arguments) -WorkingDirectory ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) -TimeoutMs $TimeoutMs -Operation ('owner-provision.'+$Arguments[0]) -AllowFailure:$AllowFailure
}
function Read-OwnerResourceFacts {
    $context=(Invoke-OwnerDocker @('context','show')).Out.Trim()
    $engine=(Invoke-OwnerDocker @('info','--format','{{.ID}}')).Out.Trim()
    $cf='{"Id":{{json .Id}},"Name":{{json .Name}},"Image":{{json .Config.Image}},"Labels":{{json .Config.Labels}},"Ports":{{json .HostConfig.PortBindings}},"Mode":{{json .HostConfig.NetworkMode}},"Networks":{{json .NetworkSettings.Networks}},"Data":{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{json .}}{{end}}{{end}}}'
    $nf='{"Id":{{json .Id}},"Name":{{json .Name}},"Internal":{{json .Internal}},"Driver":{{json .Driver}},"Labels":{{json .Labels}}}'
    $vf='{"Name":{{json .Name}},"CreatedAt":{{json .CreatedAt}},"Labels":{{json .Labels}}}'
    $container=(Invoke-OwnerDocker @('container','inspect','--format',$cf,'zeka-m1-postgres-1')).Out|ConvertFrom-Json
    $network=(Invoke-OwnerDocker @('network','inspect','--format',$nf,'zeka-m1_internal')).Out|ConvertFrom-Json
    $volume=(Invoke-OwnerDocker @('volume','inspect','--format',$vf,'zeka-m1_postgres_data')).Out|ConvertFrom-Json
    $connections=@($container.Networks.PSObject.Properties)
    return [pscustomobject]@{
        context=$context;engineId=$engine;containerId=$container.Id;containerName=$container.Name.TrimStart('/')
        networkId=$network.Id;networkName=$network.Name;volumeName=$volume.Name;volumeCreated=$volume.CreatedAt
        initializationId=$container.Labels.'com.diliwo.zeka.initialization-id'
        containerLabels=$container.Labels;networkLabels=$network.Labels;volumeLabels=$volume.Labels
        internal=$network.Internal;driver=$network.Driver;unpublished=(@($container.Ports.PSObject.Properties).Count-eq0)
        hostNetwork=($container.Mode-eq'host');networkCount=$connections.Count
        attachedNetworkId=if($connections.Count-eq1){$connections[0].Value.NetworkID}else{''}
        dataVolumeName=$container.Data.Name;dataWritable=$container.Data.RW;postgresImage=$container.Image
    }
}
function Save-OwnerPrivateJson([string]$Path,$Value,[switch]$Replace) {
    Assert-AuthHttpsPrivateMetadata (Split-Path -Parent $Path)
    if((Test-Path -LiteralPath $Path)-and !$Replace){Stop-OwnerProvision 'OWNER_FILE_OVERWRITE_REJECTED'}
    if(Test-Path -LiteralPath $Path){Assert-AuthHttpsPrivateMetadata $Path}
    $temporary=Join-Path (Split-Path -Parent $Path) ('.owner-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 12 -Compress))
    $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    Complete-W3AtomicFileWrite -TemporaryPath $temporary -DestinationPath $Path -Replace:$Replace
}
function Invoke-OwnerPrivateTool([string]$ContainerId,[string]$Payload,[string]$OperationId,$ExpectedHelper) {
    if([Text.Encoding]::UTF8.GetByteCount($Payload)-gt65536){Stop-OwnerProvision 'OWNER_STDIN_LIMIT_REJECTED'}
    $arguments=@('start','-a','-i',$ContainerId)
    $decision=Assert-W3DockerArguments $arguments
    if(!$decision.Allowed){Write-W3GuardRecord -Rule $decision.ReasonCode -Operation 'owner-provision.start'|Out-Null;Stop-OwnerProvision 'OWNER_DOCKER_ARGUMENTS_REJECTED'}
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command docker -ErrorAction Stop).Source)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
    foreach($argument in @('--context','default')+$arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    $meta=[ordered]@{operation='owner-provision.start';operationId=$OperationId;containerId=$ContainerId;processStarted=$false;nativePid=$null;nativeExit=$null;requiresInspection=$true;stdoutWithheld=$true;stderrWithheld=$true}
    try {
        $null=$process.Start();$meta.processStarted=$true;$meta.nativePid=$process.Id
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        # No secret bytes enter stdin until the running endpoint is proven.
        Wait-OwnerHelperRunningNetwork $process $ExpectedHelper
        $write=$process.StandardInput.WriteAsync($Payload)
        if(!$write.Wait(5000)){Stop-OwnerProvision 'OWNER_STDIN_WRITE_UNCERTAIN'}
        $write.GetAwaiter().GetResult();$process.StandardInput.Close()
        $remaining=[Math]::Max(1,130000-[int]$watch.ElapsedMilliseconds)
        if(!$process.WaitForExit($remaining)){Stop-OwnerProvision 'OWNER_TOOL_DEADLINE_REQUIRES_INSPECTION'}
        $meta.nativeExit=$process.ExitCode
        $remaining=[Math]::Max(1,130000-[int]$watch.ElapsedMilliseconds)
        if(!([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout,$stderr))).Wait($remaining)){Stop-OwnerProvision 'OWNER_TOOL_OUTPUT_UNCERTAIN'}
        $meta.stdoutCharacters=$stdout.Result.Length;$meta.stderrCharacters=$stderr.Result.Length
        Write-W3DiagnosticRecord -Record $meta|Out-Null
        return [pscustomobject]@{exit=$process.ExitCode;out=$stdout.Result;errorLength=$stderr.Result.Length}
    } catch {
        if($meta.processStarted-and !$process.HasExited){$process.Kill($true);$null=$process.WaitForExit(5000)}
        Write-W3DiagnosticRecord -Record $meta|Out-Null
        throw
    } finally {$process.Dispose()}
}
function Read-OwnerToolReceipt([string]$Text,[int]$NativeExit,[int]$ErrorLength,[string]$Source,[string]$Binary,[string]$Subject) {
    if($NativeExit-ne0-or$ErrorLength-ne0){Stop-OwnerProvision 'OWNER_TOOL_REJECTED_REQUIRES_INSPECTION'}
    if($Text.Length-gt65536){Stop-OwnerProvision 'OWNER_TOOL_RECEIPT_REJECTED'}
    $document=$null
    try {
        $document=[Text.Json.JsonDocument]::Parse($Text)
        if($document.RootElement.ValueKind-ne[Text.Json.JsonValueKind]::Object){Stop-OwnerProvision 'OWNER_TOOL_RECEIPT_REJECTED'}
        $allowed=@('schema','status','code','stage','exitCode','sourceCommit','binarySha256','subjectId','organisationId','membershipId','authorityKind','auditMarker')
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($property in $document.RootElement.EnumerateObject()){
            if($property.Name-cnotin$allowed-or!$seen.Add($property.Name)){Stop-OwnerProvision 'OWNER_TOOL_RECEIPT_REJECTED'}
        }
        if($seen.Count-ne$allowed.Count){Stop-OwnerProvision 'OWNER_TOOL_RECEIPT_REJECTED'}
        $r=$Text|ConvertFrom-Json
        $organisation=[Guid]::Empty;$membership=[Guid]::Empty
        if($r.schema-cne'zeka-m1-owner-provision-result/v1'-or$r.status-cne'PASS'-or
            $r.code-cnotin@('OWNER_PROVISIONED','OWNER_PROVISIONING_REPLAY')-or$r.stage-cne'provisioning'-or$r.exitCode-ne0-or
            $r.sourceCommit-cne$Source-or$r.binarySha256-cne$Binary-or$r.subjectId-cne$Subject-or
            ![Guid]::TryParse([string]$r.organisationId,[ref]$organisation)-or$organisation-eq[Guid]::Empty-or
            ![Guid]::TryParse([string]$r.membershipId,[ref]$membership)-or$membership-eq[Guid]::Empty-or
            $r.authorityKind-cne'OwnerAttestedLocalSynthetic'-or$r.auditMarker-cne'operator-provisioned, no agreements'){
            Stop-OwnerProvision 'OWNER_TOOL_RECEIPT_REJECTED'
        }
        # Persist only constants, proven source identifiers and canonical UUIDs.
        return [ordered]@{schema='zeka-m1-owner-provision-result/v1';status='PASS';code=$r.code;stage='provisioning';exitCode=0;
            sourceCommit=$Source;binarySha256=$Binary;subjectId=$Subject;organisationId=$organisation.ToString('D');membershipId=$membership.ToString('D');
            authorityKind='OwnerAttestedLocalSynthetic';auditMarker='operator-provisioned, no agreements'}
    } catch {Stop-OwnerProvision 'OWNER_TOOL_RECEIPT_REJECTED'}
    finally {if($document){$document.Dispose()}}
}
function Invoke-OwnerFirstAdmin {
    param([switch]$OwnerAttestedIdentity,[switch]$PrepareOnly,[string]$PrivateDirectory,[string]$SourceCommit,
        [string]$BinarySha256,[string]$ToolBinaryPath,[string]$ToolImage,[Guid]$ExistingSubjectId,[string]$DisplayName)
    $ErrorActionPreference='Stop'
    $identity=@{isWindows=$IsWindows;attested=[bool]$OwnerAttestedIdentity;account=[Security.Principal.WindowsIdentity]::GetCurrent().Name;
        sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;userProfile=[Environment]::GetFolderPath('UserProfile')}
    Assert-OwnerIdentityFacts $identity
    $repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $head=Read-OwnerPublicCommand git @('-C',$repo,'rev-parse','HEAD')
    $branch=Read-OwnerPublicCommand git @('-C',$repo,'branch','--show-current')
    $dirty=Read-OwnerPublicCommand git @('-C',$repo,'status','--porcelain','--untracked-files=all')
    if($head.exit-ne0-or$branch.exit-ne0-or$dirty.exit-ne0){Stop-OwnerProvision 'OWNER_SOURCE_OR_BINARY_REJECTED'}
    if(!$SourceCommit-and$PrepareOnly){$SourceCommit=$head.out.Trim()}
    Assert-OwnerSourceFacts @{branch=$branch.out.Trim();clean=![bool]$dirty.out.Trim();actualSource=$head.out.Trim();expectedSource=$SourceCommit} $false
    # These are definitions only; the database bootstrap script is deliberately never dot-sourced.
    . (Join-Path $PSScriptRoot 'W3-NativeDiagnostics.ps1')
    . (Join-Path $PSScriptRoot 'Initialize-AuthHttpsConfiguration.ps1') -DefinitionsOnly -PrivateDirectory $PrivateDirectory -SourceCommit $SourceCommit
    $private=[IO.Path]::GetFullPath($PrivateDirectory)
    if(![StringComparer]::OrdinalIgnoreCase.Equals($private,'C:\Users\herve\AppData\Local\Zeka\M1\private')){Stop-OwnerProvision 'OWNER_PRIVATE_PATH_REJECTED'}
    Assert-AuthHttpsNoAncestorReparse $private;Assert-AuthHttpsPrivateMetadata $private $true
    $physical=Resolve-AuthHttpsPhysicalPath $private
    Assert-AuthHttpsOutsideSource $physical (Resolve-AuthHttpsPhysicalPath $repo)
    # Process-local only: preserve the admitted logical root used by the database launcher.
    $env:ZEKA_M1_PRIVATE_DIR=$private
    $owner=Join-Path $private 'owner'
    if(!(Test-Path -LiteralPath $owner)){[IO.Directory]::CreateDirectory($owner)|Out-Null;Set-AuthHttpsNewDirectoryAcl $owner}
    Assert-AuthHttpsNoAncestorReparse $owner;Assert-AuthHttpsPrivateMetadata $owner
    $admissionLock=[IO.FileStream]::new((Join-Path $owner 'admission.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
    $inspectionPath=Join-Path $owner 'requires-inspection.json'
    if(Test-Path -LiteralPath $inspectionPath){Stop-OwnerProvision 'OWNER_PREVIOUS_OPERATION_REQUIRES_INSPECTION'}
    $operationId=[Guid]::NewGuid().ToString('D')
    $diagnostics=Join-Path $owner ('operation-'+$operationId)
    Initialize-W3Diagnostics -Directory $diagnostics -FixtureId 'zeka-m1' -EngineId '0b62f5ec-15d9-4e4b-a450-817d52378eef'|Out-Null
    $progressPath=Join-Path $private 'database/setup-progress.json'
    Assert-AuthHttpsNoAncestorReparse $progressPath;Assert-AuthHttpsPrivateMetadata $progressPath
    if(Test-Path -LiteralPath (Join-Path $private 'database/reset-incomplete.json')){Stop-OwnerProvision 'OWNER_DATABASE_RESET_INCOMPLETE'}
    $progress=Get-Content -LiteralPath $progressPath -Raw|ConvertFrom-Json
    if($progress.schema-cne'zeka-m1-database-setup/v1'-or$progress.project-cne'zeka-m1'-or$progress.owner-cne'windows-codex-m1'-or
        $progress.engineId-cne'0b62f5ec-15d9-4e4b-a450-817d52378eef'-or$progress.initializationId-cnotmatch'^[A-Za-z0-9_-]{16,128}$'-or
        $progress.requiresInspection-or!$progress.services.auth.completed-or$progress.sourceCommit-cnotmatch'^[0-9a-f]{40}$'-or
        !$progress.clusterId-or!$progress.containerId-or!$progress.networkId-or!$progress.volumeCreated){Stop-OwnerProvision 'OWNER_DATABASE_PROGRESS_REJECTED'}
    Assert-OwnerResourceFacts (Read-OwnerResourceFacts) $progress
    $profilePath=Join-Path $owner 'admission.json';$passwordPath=Join-Path $owner 'password'
    $profileExists=Test-Path -LiteralPath $profilePath -PathType Leaf
    $passwordExists=Test-Path -LiteralPath $passwordPath -PathType Leaf
    $profile=$null
    if($profileExists){Assert-AuthHttpsPrivateMetadata $profilePath;$profile=Get-Content -LiteralPath $profilePath -Raw|ConvertFrom-Json}
    $state=Get-OwnerPreparationState $profileExists $passwordExists ([bool]$profile.credentialSaved)
    if($PrepareOnly) {
        if($state-eq'NEW'){
            $subject=if($ExistingSubjectId-ne[Guid]::Empty){$ExistingSubjectId}else{[Guid]::NewGuid()}
            $profile=[ordered]@{schema='zeka-m1-owner-admission/v1';scope='LocalSyntheticM1OwnerProvision';ownerDecisionId='a7daf3ea-6370-4604-9e4b-e5eb206465c7';approvalCommentId='6529021741';
                ownerSid=$identity.sid;ownerAccount=$identity.account;ownerProfile=$identity.userProfile;normalizedEmail='HERVE.MBILO@GMAIL.COM';project='zeka-m1';
                initializationId=$progress.initializationId;databaseSourceCommit=$progress.sourceCommit;clusterId=$progress.clusterId;containerId=$progress.containerId;networkId=$progress.networkId;volumeCreated=$progress.volumeCreated;
                subjectId=$subject.ToString('D');displayName=$DisplayName;idempotencyKey=($progress.initializationId+':first-admin:v1');createdSourceCommit=$SourceCommit;credentialSaved=$false}
            Save-OwnerPrivateJson $profilePath $profile
            $password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))+'Aa1!'
            $temporary=Join-Path $owner ('.password-'+[Guid]::NewGuid().ToString('N')+'.tmp')
            $bytes=[Text.UTF8Encoding]::new($false).GetBytes($password)
            $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
            try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
            Complete-W3AtomicFileWrite -TemporaryPath $temporary -DestinationPath $passwordPath
            $profile.credentialSaved=$true;Save-OwnerPrivateJson $profilePath $profile -Replace
            $password=$null
        }
        Assert-OwnerProfileFacts $profile $identity $progress
        return [pscustomobject]@{status='OWNER_PREPARED';operationId=$operationId;subjectId=$profile.subjectId;authorityKind='OwnerAttestedLocalSynthetic';databaseMutated=$false}
    }
    if($state-ne'SAVED'-or$ExistingSubjectId-ne[Guid]::Empty){Stop-OwnerProvision 'OWNER_PREPARATION_REQUIRED'}
    Assert-OwnerProfileFacts $profile $identity $progress
    if($ToolImage-cnotmatch'^sha256:[0-9a-f]{64}$'-or[string]::IsNullOrWhiteSpace($ToolBinaryPath)){Stop-OwnerProvision 'OWNER_IMAGE_PROVENANCE_REJECTED'}
    $binary=[IO.Path]::GetFullPath($ToolBinaryPath)
    Assert-AuthHttpsNoAncestorReparse $binary;Assert-AuthHttpsOutsideSource $binary (Resolve-AuthHttpsPhysicalPath $repo)
    if($binary.StartsWith($physical+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){Stop-OwnerProvision 'OWNER_IMAGE_PROVENANCE_REJECTED'}
    $actualBinary=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-OwnerSourceFacts @{branch=$branch.out.Trim();clean=$true;actualSource=$head.out.Trim();expectedSource=$SourceCommit;actualBinary=$actualBinary;expectedBinary=$BinarySha256}
    $imageFormat='{"Id":{{json .Id}},"Config":{"User":{{json .Config.User}},"WorkingDir":{{json .Config.WorkingDir}},"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}},"Labels":{{json .Config.Labels}}}}'
    $image=(Invoke-OwnerDocker @('image','inspect','--format',$imageFormat,$ToolImage)).Out|ConvertFrom-Json
    Assert-OwnerImageFacts $image $SourceCommit $BinarySha256 $ToolImage
    $configurationPath=Join-Path $private 'auth/database.LocalStack.json'
    Assert-AuthHttpsNoAncestorReparse $configurationPath;Assert-AuthHttpsPrivateMetadata $configurationPath;Assert-AuthHttpsPrivateMetadata $passwordPath
    $configuration=Get-Content -LiteralPath $configurationPath -Raw|ConvertFrom-Json
    $connection=[string]$configuration.ConnectionStrings.Default
    $password=[IO.File]::ReadAllText($passwordPath)
    Register-W3Secret $connection;Register-W3Secret $password
    $request=[ordered]@{schema='zeka-m1-owner-provision/v1';scope='LocalSyntheticM1OwnerProvision';project='zeka-m1';authorityKind='OwnerAttestedLocalSynthetic';
        ownerDecisionId=$profile.ownerDecisionId;approvalCommentId=$profile.approvalCommentId;ownerAccount=$identity.account;expectedOwnerSid=$profile.ownerSid;actualOwnerSid=$identity.sid;
        expectedOwnerProfile=$profile.ownerProfile;actualOwnerProfile=$identity.userProfile;normalizedEmail=$profile.normalizedEmail;sourceCommit=$SourceCommit;binarySha256=$BinarySha256;
        databaseSourceCommit=$progress.sourceCommit;databaseName='zeka_m1_auth';initializationId=$progress.initializationId;subjectId=$profile.subjectId;displayName=$profile.displayName;slug=$null;
        idempotencyKey=$profile.idempotencyKey;processingTimeoutSeconds=120;retentionSeconds=86400;authRuntimeConnectionString=$connection;ownerPassword=$password}
    $payload=$request|ConvertTo-Json -Depth 8 -Compress
    $name='zeka-m1-owner-first-admin-'+[Guid]::NewGuid().ToString('N')
    $absent=Invoke-OwnerDocker @('container','inspect',$name) 15000 $true
    if($absent.Exit-eq0-or$absent.Error-notmatch'(?i)(no such|not found)'){Stop-OwnerProvision 'OWNER_NEW_HELPER_ABSENCE_UNPROVEN'}
    Assert-OwnerResourceFacts (Read-OwnerResourceFacts) $progress
    Save-OwnerPrivateJson $inspectionPath @{schema='zeka-m1-owner-inspection/v1';operationId=$operationId;helperName=$name;sourceCommit=$SourceCommit;requiresInspection=$true}
    $created=Invoke-OwnerDocker @('create','--name',$name,'--interactive','--network','zeka-m1_internal','--log-driver','none',
        '--read-only','--tmpfs','/tmp:rw,noexec,nosuid,nodev,size=16777216,mode=1777',
        '--cap-drop','ALL','--security-opt','no-new-privileges',
        '--label','com.docker.compose.project=zeka-m1','--label','com.diliwo.zeka.owner=windows-codex-m1',
        '--label',('com.diliwo.zeka.initialization-id='+$progress.initializationId),'--label',('com.diliwo.zeka.operation-id='+$operationId),$ToolImage)
    $containerId=$created.Out.Trim()
    if($containerId-cnotmatch'^[0-9a-f]{64}$'){Stop-OwnerProvision 'OWNER_HELPER_CREATE_UNCERTAIN'}
    $helper=(Invoke-OwnerDocker @('container','inspect','--format','{{json .}}',$containerId)).Out|ConvertFrom-Json
    $expectedHelper=@{containerId=$containerId;name=$name;image=$ToolImage;operationId=$operationId;initializationId=$progress.initializationId;networkId=$progress.networkId}
    $null=Get-OwnerHelperNetworkDecision $helper $expectedHelper -BeforeStart

    Assert-OwnerHelperIsolationFacts $helper
    Assert-OwnerResourceFacts (Read-OwnerResourceFacts) $progress
    # Save an uncertainty marker before the first process that can commit Auth state.
    # Only a verified success removes it; failures require an independent inspection, never an automatic retry.
    Save-OwnerPrivateJson $inspectionPath @{schema='zeka-m1-owner-inspection/v1';operationId=$operationId;containerId=$containerId;sourceCommit=$SourceCommit;requiresInspection=$true} -Replace
    $native=Invoke-OwnerPrivateTool $containerId $payload $operationId $expectedHelper
    $payload=$null;$password=$null;$connection=$null;$request=$null
    $result=Read-OwnerToolReceipt $native.out $native.exit $native.errorLength $SourceCommit $BinarySha256 $profile.subjectId
    Save-OwnerPrivateJson (Join-Path $diagnostics 'result.json') $result
    [IO.File]::Delete($inspectionPath)
    return $result
    } finally {$admissionLock.Dispose()}
}
if(!$DefinitionsOnly) {
    try { Invoke-OwnerFirstAdmin -OwnerAttestedIdentity:$OwnerAttestedIdentity -PrepareOnly:$PrepareOnly -PrivateDirectory $PrivateDirectory -SourceCommit $SourceCommit -BinarySha256 $BinarySha256 -ToolBinaryPath $ToolBinaryPath -ToolImage $ToolImage -ExistingSubjectId $ExistingSubjectId -DisplayName $DisplayName|ConvertTo-Json -Depth 6 -Compress }
    catch {
        $code=if($_.Exception-is[InvalidOperationException]-and$_.Exception.Message-cmatch'^OWNER_[A-Z_]+$'){$_.Exception.Message}else{'OWNER_PROVISIONING_REQUIRES_INSPECTION'}
        [ordered]@{status='BLOCKED';code=$code;requiresInspection=$true;automaticRetry=$false}|ConvertTo-Json -Compress
        exit 1
    }
}