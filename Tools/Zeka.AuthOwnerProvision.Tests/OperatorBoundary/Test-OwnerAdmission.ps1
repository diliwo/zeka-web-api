[CmdletBinding()]
param([string]$ReportFile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../../Deployments/local-stack/Invoke-OwnerFirstAdmin.ps1') -DefinitionsOnly
# Pure policy fixtures only: no Windows identity lookup, private files, native commands or Docker.
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$CaseName,[scriptblock]$Action,[string]$Expected='') {
    try {
        & $Action
        if($Expected){throw [InvalidOperationException]::new('EXPECTED_REFUSAL_MISSING')}
        $checks.Add([pscustomobject]@{name=$CaseName;passed=$true})
    } catch {
        if($Expected-and$_.Exception.Message-ceq$Expected){$checks.Add([pscustomobject]@{name=$CaseName;passed=$true})}
        else{throw [InvalidOperationException]::new('OWNER_ADMISSION_TEST_FAILED_'+$CaseName)}
    }
}
function Identity {return @{isWindows=$true;attested=$true;account='DILI_LP\herve';userProfile='C:\Users\herve';sid='S-1-5-21-1-2-3-1001'}}
function Progress {return @{initializationId='synthetic-init-0001';sourceCommit=('d'*40);containerId=('1'*64);networkId=('2'*64);clusterId='123456789';volumeCreated='2026-10-30T00:00:00Z'}}
function Profile {
    $p=Progress
    return @{schema='zeka-m1-owner-admission/v1';scope='LocalSyntheticM1OwnerProvision';ownerDecisionId='a7daf3ea-6370-4604-9e4b-e5eb206465c7';approvalCommentId='6529021741';
        ownerSid=(Identity).sid;ownerAccount='DILI_LP\herve';ownerProfile='C:\Users\herve';normalizedEmail='HERVE.MBILO@GMAIL.COM';project='zeka-m1';
        initializationId=$p.initializationId;databaseSourceCommit=$p.sourceCommit;containerId=$p.containerId;networkId=$p.networkId;clusterId=$p.clusterId;volumeCreated=$p.volumeCreated;
        subjectId='11111111-1111-1111-1111-111111111111';displayName='M1 synthetic organisation';idempotencyKey=($p.initializationId+':first-admin:v1')}
}
function Resource {
    $p=Progress
    $labels=@{'com.docker.compose.project'='zeka-m1';'com.diliwo.zeka.owner'='windows-codex-m1';'com.diliwo.zeka.initialization-id'=$p.initializationId}
    return @{containerId=$p.containerId;networkId=$p.networkId;volumeCreated=$p.volumeCreated;initializationId=$p.initializationId;context='default';
        engineId='0b62f5ec-15d9-4e4b-a450-817d52378eef';containerName='zeka-m1-postgres-1';volumeName='zeka-m1_postgres_data';networkName='zeka-m1_internal';
        internal=$true;driver='bridge';unpublished=$true;hostNetwork=$false;networkCount=1;attachedNetworkId=$p.networkId;dataVolumeName='zeka-m1_postgres_data';dataWritable=$true;postgresImage='postgres:17';
        containerLabels=$labels.Clone();networkLabels=$labels.Clone();volumeLabels=$labels.Clone()}
}
Check 'identity_accepts_pinned_owner' {Assert-OwnerIdentityFacts (Identity) (Identity).sid}
foreach($field in @('attested','isWindows','account','userProfile','sid')) {
    $name=$field
    Check ('identity_rejects_'+$name) { $f=Identity; $f[$name]=if($name-in@('attested','isWindows')){$false}else{'unexpected'};Assert-OwnerIdentityFacts $f } 'OWNER_IDENTITY_ADMISSION_REJECTED'
}
Check 'pinned_sid_mismatch' {Assert-OwnerIdentityFacts (Identity) 'S-1-5-21-1-2-3-2002'} 'OWNER_IDENTITY_ADMISSION_REJECTED'
Check 'profile_accepts_exact_owner_intent' {Assert-OwnerProfileFacts (Profile) (Identity) (Progress)}
foreach($field in @('normalizedEmail','ownerSid','ownerAccount','ownerProfile','initializationId','databaseSourceCommit','containerId','networkId','clusterId','volumeCreated','subjectId','idempotencyKey')) {
    $name=$field
    $code=if($name-eq'ownerSid'){'OWNER_IDENTITY_ADMISSION_REJECTED'}else{'OWNER_PROFILE_BINDING_REJECTED'}
    Check ('profile_rejects_'+$name) {$p=Profile;$p[$name]='unexpected';Assert-OwnerProfileFacts $p (Identity) (Progress)} $code
}
Check 'resource_accepts_exact_saved_graph' {Assert-OwnerResourceFacts (Resource) (Progress)}
foreach($field in @('containerId','networkId','volumeCreated','initializationId')) {
    $name=$field
    Check ('resource_rejects_'+$name) {$r=Resource;$r[$name]='unexpected';Assert-OwnerResourceFacts $r (Progress)} 'OWNER_RESOURCE_ID_DRIFT'
}
foreach($field in @('containerLabels','networkLabels','volumeLabels')) {
    $name=$field
    Check ('resource_rejects_'+$name) {$r=Resource;$r[$name]['com.diliwo.zeka.owner']='unexpected';Assert-OwnerResourceFacts $r (Progress)} 'OWNER_RESOURCE_LABEL_DRIFT'
}
foreach($field in @('internal','unpublished','hostNetwork','engineId','attachedNetworkId')) {
    $name=$field
    Check ('resource_configuration_rejects_'+$name) {
        $r=Resource;$r[$name]=if($name-eq'hostNetwork'){$true}elseif($name-in@('internal','unpublished')){$false}else{'unexpected'};Assert-OwnerResourceFacts $r (Progress)
    } 'OWNER_RESOURCE_CONFIGURATION_REJECTED'
}
Check 'new_preparation' {if((Get-OwnerPreparationState $false $false $false)-cne'NEW'){throw 'STATE'}}
Check 'saved_preparation' {if((Get-OwnerPreparationState $true $true $true)-cne'SAVED'){throw 'STATE'}}
Check 'orphan_credential_refused' {Get-OwnerPreparationState $false $true $false|Out-Null} 'OWNER_ORPHAN_CREDENTIAL_REJECTED'
Check 'saved_credential_missing_refused' {Get-OwnerPreparationState $true $false $true|Out-Null} 'OWNER_SAVED_CREDENTIAL_MISSING'
Check 'incomplete_preparation_refused' {Get-OwnerPreparationState $true $true $false|Out-Null} 'OWNER_PREPARATION_REQUIRES_INSPECTION'
Check 'source_binary_accept' {Assert-OwnerSourceFacts @{branch='codex/m1-c1-integration';clean=$true;actualSource=('a'*40);expectedSource=('a'*40);actualBinary=('b'*64);expectedBinary=('b'*64)}}
foreach($field in @('branch','clean','actualSource','expectedSource','actualBinary','expectedBinary')) {
    $name=$field
    Check ('source_binary_rejects_'+$name) {
        $f=@{branch='codex/m1-c1-integration';clean=$true;actualSource=('a'*40);expectedSource=('a'*40);actualBinary=('b'*64);expectedBinary=('b'*64)}
        $f[$name]=if($name-eq'clean'){$false}else{'unexpected'};Assert-OwnerSourceFacts $f
    } 'OWNER_SOURCE_OR_BINARY_REJECTED'
}
Check 'private_path_accept' {Assert-OwnerFileFacts @{reparse=$false;protected=$true;outsideSource=$true}}
foreach($field in @('reparse','protected','outsideSource')) {
    $name=$field
    Check ('private_path_rejects_'+$name) {$f=@{reparse=$false;protected=$true;outsideSource=$true};$f[$name]=($name-eq'reparse');Assert-OwnerFileFacts $f} 'OWNER_PRIVATE_PATH_REJECTED'
}
function Image {
    return @{Id=('sha256:'+('c'*64));Config=@{User='app';WorkingDir='/app';Entrypoint=@('dotnet','/app/zeka-auth-owner-provision.dll');Cmd=$null;
        Labels=@{'org.opencontainers.image.revision'=('a'*40);'com.diliwo.zeka.binary-sha256'=('b'*64);'com.diliwo.zeka.scope'='m1-owner-provision';'com.diliwo.zeka.owner'='windows-codex-m1';'com.docker.compose.project'='zeka-m1'}}}
}
Check 'image_accepts_pinned_tool' {Assert-OwnerImageFacts (Image) ('a'*40) ('b'*64) ('sha256:'+('c'*64))}
foreach($field in @('User','WorkingDir','Entrypoint','Cmd')) {
    $name=$field
    Check ('image_rejects_'+$name) {$i=Image;$i.Config[$name]=@('unexpected');Assert-OwnerImageFacts $i ('a'*40) ('b'*64) ('sha256:'+('c'*64))} 'OWNER_IMAGE_PROVENANCE_REJECTED'
}
function Receipt {
    return [ordered]@{schema='zeka-m1-owner-provision-result/v1';status='PASS';code='OWNER_PROVISIONED';stage='provisioning';exitCode=0;
        sourceCommit=('a'*40);binarySha256=('b'*64);subjectId='11111111-1111-1111-1111-111111111111';
        organisationId='22222222-2222-2222-2222-222222222222';membershipId='33333333-3333-3333-3333-333333333333';
        authorityKind='OwnerAttestedLocalSynthetic';auditMarker='operator-provisioned, no agreements'}
}
Check 'receipt_accepts_exact_producer' {$r=Read-OwnerToolReceipt ((Receipt)|ConvertTo-Json -Compress) 0 0 ('a'*40) ('b'*64) (Profile).subjectId;if($r.code-cne'OWNER_PROVISIONED'){throw 'RECEIPT'}}
foreach($field in @('organisationId','membershipId','stage','auditMarker','sourceCommit')) {
    $name=$field
    Check ('receipt_withholds_invalid_'+$name) {$r=Receipt;$r[$name]='synthetic-withheld-value';Read-OwnerToolReceipt ($r|ConvertTo-Json -Compress) 0 0 ('a'*40) ('b'*64) (Profile).subjectId|Out-Null} 'OWNER_TOOL_RECEIPT_REJECTED'
}
Check 'receipt_rejects_unknown_field' {$r=Receipt;$r.extra='synthetic-withheld-value';Read-OwnerToolReceipt ($r|ConvertTo-Json -Compress) 0 0 ('a'*40) ('b'*64) (Profile).subjectId|Out-Null} 'OWNER_TOOL_RECEIPT_REJECTED'
Check 'receipt_rejects_duplicate_field' {$j=(Receipt)|ConvertTo-Json -Compress;$j=$j.TrimEnd('}')+',"status":"PASS"}';Read-OwnerToolReceipt $j 0 0 ('a'*40) ('b'*64) (Profile).subjectId|Out-Null} 'OWNER_TOOL_RECEIPT_REJECTED'
Check 'receipt_withholds_nonzero_native_output' {Read-OwnerToolReceipt 'synthetic-withheld-value' 5 0 ('a'*40) ('b'*64) (Profile).subjectId|Out-Null} 'OWNER_TOOL_REJECTED_REQUIRES_INSPECTION'
Check 'receipt_withholds_stderr' {Read-OwnerToolReceipt ((Receipt)|ConvertTo-Json -Compress) 0 1 ('a'*40) ('b'*64) (Profile).subjectId|Out-Null} 'OWNER_TOOL_REJECTED_REQUIRES_INSPECTION'
# Signature-only native adapter seam. These local stubs never start a process.
function Assert-W3DockerArguments([string[]]$ArgumentList) {return @{Allowed=$true}}
function Invoke-W3Native([string]$Exe,[string[]]$ArgumentList,[string]$WorkingDirectory,[int]$TimeoutMs,[string]$Operation,[switch]$AllowFailure) {
    $expected=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    if([string]::IsNullOrWhiteSpace($WorkingDirectory)-or$WorkingDirectory-cne$expected-or$Exe-cne'docker'-or
        $ArgumentList.Count-ne4-or$ArgumentList[0]-cne'--context'-or$ArgumentList[1]-cne'default'-or
        $ArgumentList[2]-cne'context'-or$ArgumentList[3]-cne'show'){
        throw [InvalidOperationException]::new('OWNER_NATIVE_ADAPTER_SIGNATURE_REJECTED')
    }
    return [pscustomobject]@{Exit=0;Out='fixture-only';Error=''}
}
Check 'native_adapter_explicit_source_workdir' {$n=Invoke-OwnerDocker @('context','show');if($n.Out-cne'fixture-only'){throw 'STUB'}}

Check 'actual_helper_import_preserves_admitted_values' {
    # Execute the real definitions-only import in a child scope; no M1 path exists in this fixture.
    & {
        $PrivateDirectory='credential-free-private-directory-marker'
        $SourceCommit=('7'*40)
        $beforePrivate=$PrivateDirectory;$beforeSource=$SourceCommit
        $beforeEnvironment=$env:ZEKA_M1_PRIVATE_DIR
        function Get-Content {throw 'UNEXPECTED_PRIVATE_IO'}
        function Get-Item {throw 'UNEXPECTED_PRIVATE_IO'}
        function Get-Acl {throw 'UNEXPECTED_PRIVATE_IO'}
        function Set-Acl {throw 'UNEXPECTED_PRIVATE_IO'}
        function Test-Path {throw 'UNEXPECTED_PRIVATE_IO'}
        function Add-Type {throw 'UNEXPECTED_NATIVE_OPERATION'}
        function docker {throw 'UNEXPECTED_DOCKER_OPERATION'}
        function Invoke-W3Native {throw 'UNEXPECTED_NATIVE_OPERATION'}
        function Read-OwnerPublicCommand {throw 'UNEXPECTED_NATIVE_OPERATION'}
        . (Join-Path $PSScriptRoot '../../../Deployments/local-stack/Initialize-AuthHttpsConfiguration.ps1') -DefinitionsOnly -PrivateDirectory $PrivateDirectory -SourceCommit $SourceCommit
        if($PrivateDirectory-cne$beforePrivate-or$SourceCommit-cne$beforeSource-or$env:ZEKA_M1_PRIVATE_DIR-cne$beforeEnvironment){
            throw 'HELPER_IMPORT_CHANGED_ADMITTED_VALUES'
        }
    }
}

function IsolatedHelper {
    return [pscustomobject]@{HostConfig=[pscustomobject]@{ReadonlyRootfs=$true;Tmpfs=[pscustomobject]@{'/tmp'='rw,noexec,nosuid,nodev,size=16777216,mode=1777'};
        Binds=$null;Mounts=$null;Privileged=$false;NetworkMode='zeka-m1_internal';PortBindings=[pscustomobject]@{};CapDrop=@('ALL');CapAdd=$null;SecurityOpt=@('no-new-privileges')};Mounts=@()}
}
Check 'helper_isolation_accepts_readonly_tmpfs' {Assert-OwnerHelperIsolationFacts (IsolatedHelper)}
Check 'helper_isolation_accepts_tmpfs_mount_view' {$h=IsolatedHelper;$h.Mounts=@([pscustomobject]@{Type='tmpfs';Destination='/tmp';Source='';RW=$true});Assert-OwnerHelperIsolationFacts $h}
foreach($field in @('ReadonlyRootfs','Tmpfs','Binds','Mounts','Privileged','NetworkMode','PortBindings','CapDrop','CapAdd','SecurityOpt')) {
    $name=$field
    Check ('helper_isolation_rejects_'+$name) {
        $h=IsolatedHelper
        $h.HostConfig.$name=switch($name){
            'ReadonlyRootfs' {$false}
            'Tmpfs' {[pscustomobject]@{'/tmp'='rw,size=67108864'}}
            'Privileged' {$true}
            'NetworkMode' {'host'}
            'PortBindings' {[pscustomobject]@{'5432/tcp'=@([pscustomobject]@{HostPort='5432'})}}
            default {@('unexpected')}
        }
        Assert-OwnerHelperIsolationFacts $h
    } 'OWNER_HELPER_ISOLATION_REJECTED'
}
Check 'helper_isolation_rejects_bind_mount' {$h=IsolatedHelper;$h.Mounts=@([pscustomobject]@{Type='bind';Destination='/tmp';Source='synthetic';RW=$true});Assert-OwnerHelperIsolationFacts $h} 'OWNER_HELPER_ISOLATION_REJECTED'
Check 'helper_isolation_rejects_additional_mount' {$h=IsolatedHelper;$h.Mounts=@([pscustomobject]@{Type='tmpfs';Destination='/tmp';Source='';RW=$true},[pscustomobject]@{Type='tmpfs';Destination='/extra';Source='';RW=$true});Assert-OwnerHelperIsolationFacts $h} 'OWNER_HELPER_ISOLATION_REJECTED'

function ExpectedHelperNetwork {return @{containerId=('1'*64);name='zeka-m1-owner-fixture';image=('sha256:'+('c'*64));operationId='operation-fixture';initializationId='synthetic-init-0001';networkId=('2'*64)}}
function HelperNetwork([string]$State='created',[string]$NetworkId='') {
    $e=ExpectedHelperNetwork
    return [pscustomobject]@{Id=$e.containerId;Name=('/'+$e.name);Image=$e.image;State=[pscustomobject]@{Status=$State};
        HostConfig=[pscustomobject]@{NetworkMode='zeka-m1_internal'};
        Config=[pscustomobject]@{Labels=[pscustomobject]@{'com.diliwo.zeka.operation-id'=$e.operationId;'com.diliwo.zeka.initialization-id'=$e.initializationId;'com.diliwo.zeka.owner'='windows-codex-m1';'com.docker.compose.project'='zeka-m1'}};
        NetworkSettings=[pscustomobject]@{Networks=[pscustomobject]@{'zeka-m1_internal'=[pscustomobject]@{NetworkID=$NetworkId}}}}
}
Check 'created_endpoint_id_may_be_unassigned' {if((Get-OwnerHelperNetworkDecision (HelperNetwork) (ExpectedHelperNetwork) -BeforeStart)-cne'CREATED'){throw 'STATE'}}
Check 'created_endpoint_may_have_saved_id' {if((Get-OwnerHelperNetworkDecision (HelperNetwork 'created' ('2'*64)) (ExpectedHelperNetwork) -BeforeStart)-cne'CREATED'){throw 'STATE'}}
Check 'created_state_waits_without_secret_input' {if((Get-OwnerHelperNetworkDecision (HelperNetwork) (ExpectedHelperNetwork))-cne'WAIT'){throw 'STATE'}}
Check 'running_requires_saved_endpoint' {if((Get-OwnerHelperNetworkDecision (HelperNetwork 'running' ('2'*64)) (ExpectedHelperNetwork))-cne'RUNNING'){throw 'STATE'}}
Check 'created_wrong_endpoint_refused' {Get-OwnerHelperNetworkDecision (HelperNetwork 'created' ('3'*64)) (ExpectedHelperNetwork) -BeforeStart|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
Check 'running_empty_endpoint_refused' {Get-OwnerHelperNetworkDecision (HelperNetwork 'running') (ExpectedHelperNetwork)|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
Check 'running_wrong_endpoint_refused' {Get-OwnerHelperNetworkDecision (HelperNetwork 'running' ('3'*64)) (ExpectedHelperNetwork)|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
Check 'running_before_start_refused' {Get-OwnerHelperNetworkDecision (HelperNetwork 'running' ('2'*64)) (ExpectedHelperNetwork) -BeforeStart|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
foreach($stateName in @('exited','dead','paused','restarting','unknown')){
    $state=$stateName
    Check ('network_refuses_state_'+$stateName) {Get-OwnerHelperNetworkDecision (HelperNetwork $state ('2'*64)) (ExpectedHelperNetwork)|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
}
foreach($field in @('Id','Name','Image')){
    $name=$field
    Check ('network_refuses_changed_'+$name) {$h=HelperNetwork 'running' ('2'*64);$h.$name='unexpected';Get-OwnerHelperNetworkDecision $h (ExpectedHelperNetwork)|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
}
Check 'network_refuses_wrong_intent' {$h=HelperNetwork;$h.HostConfig.NetworkMode='host';Get-OwnerHelperNetworkDecision $h (ExpectedHelperNetwork) -BeforeStart|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
Check 'network_refuses_wrong_label' {$h=HelperNetwork;$h.Config.Labels.'com.diliwo.zeka.owner'='unexpected';Get-OwnerHelperNetworkDecision $h (ExpectedHelperNetwork) -BeforeStart|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
Check 'network_refuses_second_attachment' {$h=HelperNetwork;$h.NetworkSettings.Networks|Add-Member -NotePropertyName 'unexpected' -NotePropertyValue ([pscustomobject]@{NetworkID=('3'*64)});Get-OwnerHelperNetworkDecision $h (ExpectedHelperNetwork) -BeforeStart|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'
Check 'network_refuses_wrong_attachment_name' {$h=HelperNetwork;$h.NetworkSettings.Networks=[pscustomobject]@{'unexpected'=[pscustomobject]@{NetworkID=('2'*64)}};Get-OwnerHelperNetworkDecision $h (ExpectedHelperNetwork) -BeforeStart|Out-Null} 'OWNER_HELPER_NETWORK_REJECTED'

$result=[ordered]@{schema='zeka-m1-owner-admission-tests/v1';status='PASS';count=$checks.Count;checks=$checks.ToArray();runtimeOperationsExecuted=$false}
if($ReportFile){
    $full=[IO.Path]::GetFullPath($ReportFile)
    if(Test-Path -LiteralPath $full){throw 'REPORT_OVERWRITE_REJECTED'}
    $parent=Split-Path -Parent $full
    if(!(Test-Path -LiteralPath $parent -PathType Container)){throw 'REPORT_PARENT_MISSING'}
    [IO.File]::WriteAllText($full,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
$result|ConvertTo-Json -Depth 8 -Compress
