#Requires -Version 7.4
param([string]$PrivateDirectory='C:\Users\herve\AppData\Local\Zeka\M1\private',[Guid]$OperationId,[switch]$Execute,[switch]$DefinitionsOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$script:WorkerProfiles=@('adminarea-retry','client-projection')
$script:WorkerOrigin='https://auth.zeka-m1.test:19443'
$script:WorkerHelperImage='sha256:ac6dbbb7024fa113d99e4eb99c22f504651610542e5aa6cfe50de34292b797df'
$script:WorkerSourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:WorkerExecutionStarted=$false
# Source-only by default. The protected bindings manifest is an independent
# operator admission, never generated automatically from Docker inspection.
# No login/refresh, worker startup, business mutation, sealed-volume update,
# global trust change or permissive TLS callback is provided here.
function Assert-WorkerCondition([bool]$Condition,[string]$Code) { if(!$Condition){throw [InvalidOperationException]::new($Code)} }
function Get-WorkerHash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Get-WorkerSafeCode($Record) { $v=[string]$Record.Exception.Message; if($v -cmatch '^[A-Z][A-Z0-9_]{2,95}$'){return $v}; return 'OPERATION_FAILED_DETAILS_WITHHELD' }
function ConvertFrom-WorkerJson {
    param([Parameter(ValueFromPipeline)][string]$Json)
    process {
        # PS7.4 has no ConvertFrom-Json -DateKind. JsonDocument preserves exact
        # timestamp strings and Int64 generations across both supported hosts.
        Assert-WorkerCondition ($Json.Length -le 1048576) 'JSON_SIZE_REJECTED'
        $options=[Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=32
        $document=[Text.Json.JsonDocument]::Parse($Json,$options)
        try { Convert-WorkerJsonValue $document.RootElement }
        finally { $document.Dispose() }
    }
}
function Convert-WorkerJsonValue([Text.Json.JsonElement]$Element) {
    switch($Element.ValueKind.ToString()) {
        'Object' {
            $map=[hashtable]::new([StringComparer]::OrdinalIgnoreCase)
            foreach($property in $Element.EnumerateObject()) {
                Assert-WorkerCondition (!$map.ContainsKey($property.Name)) 'JSON_DUPLICATE_PROPERTY_REJECTED'
                $map[$property.Name]=Convert-WorkerJsonValue $property.Value
            }
            return $map
        }
        'Array' {
            $items=[Collections.Generic.List[object]]::new()
            foreach($item in $Element.EnumerateArray()) {$items.Add((Convert-WorkerJsonValue $item))}
            return ,$items.ToArray()
        }
        'String' { return $Element.GetString() }
        'Number' {
            [long]$integer=0
            if($Element.TryGetInt64([ref]$integer)) { return $integer }
            return $Element.GetDecimal()
        }
        'True' { return $true }
        'False' { return $false }
        'Null' { return $null }
        default { throw 'JSON_KIND_REJECTED' }
    }
}
function Write-WorkerJournal([string]$Path,[System.Collections.IDictionary]$Journal) {
    # Serialize only caller-built safe metadata, never a private API response.
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Journal|ConvertTo-Json -Depth 14 -Compress))
    $tmp="$Path.$([Guid]::NewGuid().ToString('N')).pending"
    $f=[IO.FileStream]::new($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try{$f.Write($bytes);$f.Flush($true)}finally{$f.Dispose()}
    $acl=Get-Acl -LiteralPath $tmp;$acl.SetAccessRuleProtection($true,$true);Set-Acl -LiteralPath $tmp -AclObject $acl
    [IO.File]::Move($tmp,$Path,$true)
}
function New-WorkerJournal([Guid]$Id,[Guid]$OrganisationId,[string]$SourceCommit) {
    Assert-WorkerCondition ($Id -ne [Guid]::Empty -and $OrganisationId -ne [Guid]::Empty) 'OPERATION_ID_REQUIRED'
    # Audit receipt only: no replay, cutover or recovery state machine.
    @{schema='zeka.m1.worker-rotation-run/v2';operationId=$Id.ToString('D');organisationId=$OrganisationId.ToString('D');sourceCommit=$SourceCommit;state='NEW';publications=@{};stages=@{};history=@();failure=$null}
}
function Add-WorkerHistory($Journal,[string]$State) {
    $Journal.state=$State;$Journal.history=@($Journal.history)+@(@{state=$State;utc=[DateTimeOffset]::UtcNow.ToString('O')})
}
function Invoke-WorkerRotationFlow {
    param([System.Collections.IDictionary]$Journal,[hashtable]$Ports)
    # One forward attempt. Each later explicit invocation gets a fresh pair.
    try {
        Assert-WorkerCondition ($Journal.state -eq 'NEW') 'FRESH_OPERATION_REQUIRED'
        Add-WorkerHistory $Journal 'ISSUANCE_SUBMITTED';& ($Ports.Save) $Journal|Out-Null
        $issued=& ($Ports.Prepare) $Journal
        Assert-WorkerCondition ($issued.state -eq 'ISSUED') 'FRESH_PAIR_NOT_RETURNED'
        $Journal.stages=& ($Ports.Stage) $Journal $issued;$issued=$null
        Assert-WorkerCondition ($Journal.stages.Count -eq 2) 'PAIR_STAGE_INCOMPLETE'
        Add-WorkerHistory $Journal 'BOTH_STAGED';& ($Ports.Save) $Journal|Out-Null
        foreach($profile in $script:WorkerProfiles){
            $receipt=& ($Ports.Publish) $Journal $profile
            Assert-WorkerDelivery $receipt $Journal.stages[$profile] $profile $Journal.operationId
            $Journal.publications[$profile]=$receipt
            Add-WorkerHistory $Journal 'DELIVERING';& ($Ports.Save) $Journal|Out-Null
        }
        if(@($Journal.publications.Values|Where-Object {!$_.readOnlyMount}).Count -ne 0){
            Add-WorkerHistory $Journal 'ISSUED_DELIVERED_MOUNT_SCOPE_PENDING'
            $Journal.failure='ACTUAL_CONSUMER_MOUNT_SCOPE_NOT_ADMITTED'
            & ($Ports.Save) $Journal|Out-Null;return $Journal
        }
        # Both exact native-file deliveries and actual consumer RO mount scopes
        # precede revocation. Usage/adoption is independent subsequent evidence.
        Add-WorkerHistory $Journal 'REVOKE_OTHERS_SUBMITTED';& ($Ports.Save) $Journal|Out-Null
        $revoked=& ($Ports.RevokeOthers) $Journal
        Assert-WorkerCondition ($revoked.state -eq 'REVOKED_OTHERS') 'REVOCATION_NOT_CONFIRMED'
        Add-WorkerHistory $Journal 'DELIVERED_AND_OTHERS_REVOKED';& ($Ports.Save) $Journal|Out-Null
    }catch{
        $Journal.failure=Get-WorkerSafeCode $_
        $outcome=if($Journal.state -eq 'REVOKE_OTHERS_SUBMITTED'){'REVOCATION_FAILED_OR_UNCERTAIN'}else{'FAILED_WITHOUT_REVOCATION'}
        Add-WorkerHistory $Journal $outcome;& ($Ports.Save) $Journal|Out-Null
    }
    return $Journal
}
function Assert-WorkerDelivery($Receipt,$Stage,[string]$Profile,[string]$Operation) {
    $service=if($Profile -ceq 'adminarea-retry'){'adminarea'}else{'client'}
    Assert-WorkerCondition ($Receipt.verified -eq $true -and $Receipt.profile -ceq $Profile -and
        $Receipt.operationId -ceq $Operation -and [long]$Receipt.generation -eq [long]$Stage.generation -and
        $Receipt.subjectId -eq $Stage.subjectId -and $Receipt.sessionId -eq $Stage.sessionId -and
        $Receipt.payloadSha256 -ceq $Stage.payloadSha256 -and $Receipt.publicationSha256 -ceq $Stage.sha256 -and
        $Receipt.publicationSha256 -cmatch '^[a-f0-9]{64}$' -and $Receipt.payloadSha256 -cmatch '^[a-f0-9]{64}$' -and
        $Receipt.volumeName -cmatch "^zeka-m1-worker-$service-rotation-[a-f0-9]{12}$" -and
        $Receipt.destinationPath -ceq '/run/zeka-worker-credential/current.json' -and
        $Receipt.ownerUid -eq 1654 -and $Receipt.ownerGid -eq 1654 -and
        $Receipt.directoryMode -ceq '0500' -and $Receipt.mode -ceq '0400' -and
        $Receipt.consumerService -ceq $service) 'DELIVERY_PROOF_REJECTED'
}
function Assert-WorkerProtectedPath([string]$Path,[string]$Root,[switch]$Directory) {
    $full=[IO.Path]::GetFullPath($Path);$base=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    Assert-WorkerCondition ($full.Equals($base,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)) 'PRIVATE_PATH_OUTSIDE_APPROVED_ROOT'
    $cursor=$full
    while($cursor.Length -ge $base.Length){
        $item=Get-Item -LiteralPath $cursor -Force
        Assert-WorkerCondition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'PRIVATE_REPARSE_POINT_REJECTED'
        if($cursor.Equals($base,[StringComparison]::OrdinalIgnoreCase)){break};$cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    $item=Get-Item -LiteralPath $full -Force
    Assert-WorkerCondition ($item.PSIsContainer -eq [bool]$Directory) 'PRIVATE_FILE_TYPE_REJECTED'
    $acl=Get-Acl -LiteralPath $full
    Assert-WorkerCondition $acl.AreAccessRulesProtected 'PRIVATE_ACL_INHERITANCE_REJECTED'
    $allowed=@($script:WorkerContext.Bindings.ownerSid,'S-1-5-18','S-1-5-32-544')
    foreach($entry in $acl.Access){
        $sid=$entry.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        Assert-WorkerCondition ($entry.AccessControlType -eq 'Allow' -and $sid -in $allowed) 'PRIVATE_ACL_PRINCIPAL_REJECTED'
    }
    $owner=([Security.Principal.NTAccount]::new($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    Assert-WorkerCondition ($owner -in $allowed) 'PRIVATE_ACL_OWNER_REJECTED'
    return $full
}
function New-WorkerProtectedDirectory([string]$Path) {
    Assert-WorkerCondition (!(Test-Path -LiteralPath $Path)) 'OPERATION_DIRECTORY_ALREADY_EXISTS'
    [IO.Directory]::CreateDirectory($Path)|Out-Null
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($script:WorkerContext.Bindings.ownerSid))
    foreach($value in @($script:WorkerContext.Bindings.ownerSid,'S-1-5-18','S-1-5-32-544')){
        $rule=[Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($value),'FullControl','ContainerInherit,ObjectInherit','None','Allow');$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-WorkerProtectedPath $Path $script:WorkerContext.Private -Directory|Out-Null
}
function Write-WorkerProtectedFile([string]$Path,[byte[]]$Bytes) {
    $f=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try{$f.Write($Bytes);$f.Flush($true)}finally{$f.Dispose()}
    $acl=Get-Acl -LiteralPath $Path;$acl.SetAccessRuleProtection($true,$true);Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-WorkerProtectedPath $Path $script:WorkerContext.Private|Out-Null
}
function Invoke-WorkerNative([string[]]$Arguments,[ValidateRange(0.001,120)][double]$TimeoutSeconds=30,[switch]$AllowFailure) {
    # Native output stays in memory; inspect/log output may contain unrelated secrets.
    $start=[Diagnostics.ProcessStartInfo]::new('docker');$start.UseShellExecute=$false
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.ArgumentList.Add('--context');$start.ArgumentList.Add('default')
    foreach($value in $Arguments){$start.ArgumentList.Add($value)}
    $p=[Diagnostics.Process]::new();$p.StartInfo=$start
    try{
        Assert-WorkerCondition ($p.Start()) 'DOCKER_NATIVE_START_FAILED'
        $stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
        if(!$p.WaitForExit([int][Math]::Ceiling($TimeoutSeconds*1000))){try{$p.Kill()}catch{};throw 'DOCKER_NATIVE_TIMEOUT_UNCERTAIN'}
        $out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult()
        if(!$AllowFailure -and $p.ExitCode -ne 0){throw 'DOCKER_NATIVE_FAILED_DETAILS_WITHHELD'}
        return @{exitCode=$p.ExitCode;stdout=$out;stderr=$err}
    }finally{$p.Dispose()}
}
function Get-WorkerDockerJson([string[]]$Arguments,[double]$TimeoutSeconds=30) {
    $r=Invoke-WorkerNative $Arguments -TimeoutSeconds $TimeoutSeconds
    try{return ($r.stdout|ConvertFrom-WorkerJson)}catch{throw 'DOCKER_JSON_REJECTED'}
}
function Assert-WorkerEngine {
    $i=Get-WorkerDockerJson @('info','--format','{{json .}}')
    Assert-WorkerCondition ($i.ID -ceq $script:WorkerContext.Bindings.dockerEngineId -and $i.OSType -ceq 'linux') 'DOCKER_ENGINE_ID_REJECTED'
}
function New-WorkerHttpClient([string]$CaPath,[string]$ExpectedHash,[string]$Token) {
    $bytes=[IO.File]::ReadAllBytes($CaPath)
    Assert-WorkerCondition ((Get-WorkerHash $bytes) -ceq $ExpectedHash) 'AUTH_CA_HASH_REJECTED'
    $certs=[Security.Cryptography.X509Certificates.X509Certificate2Collection]::new();$certs.ImportFromPem([Text.Encoding]::UTF8.GetString($bytes))
    Assert-WorkerCondition ($certs.Count -gt 0) 'AUTH_CA_MISSING'
    foreach($cert in $certs){Assert-WorkerCondition (!$cert.HasPrivateKey -and $cert.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow) 'AUTH_CA_REJECTED'}
    $h=[Net.Http.SocketsHttpHandler]::new();$h.AllowAutoRedirect=$false;$h.UseCookies=$false;$h.ConnectTimeout=[TimeSpan]::FromSeconds(10)
    $policy=[Security.Cryptography.X509Certificates.X509ChainPolicy]::new()
    $policy.TrustMode=[Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust;$policy.CustomTrustStore.AddRange($certs)
    $policy.DisableCertificateDownloads=$true;$policy.RevocationMode=[Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    $policy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))|Out-Null
    $h.SslOptions.CertificateChainPolicy=$policy
    # No certificate callback: platform hostname validation remains enabled.
    $c=[Net.Http.HttpClient]::new($h);$c.Timeout=[TimeSpan]::FromSeconds(30);$c.MaxResponseContentBufferSize=262144
    $c.DefaultRequestHeaders.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$Token)
    $c.DefaultRequestHeaders.CacheControl=[Net.Http.Headers.CacheControlHeaderValue]::new();$c.DefaultRequestHeaders.CacheControl.NoStore=$true
    return $c
}
function Invoke-WorkerPhase([string]$Phase,$Journal) {
    Assert-WorkerCondition ($Phase -in @('prepare','revoke-others')) 'AUTH_PHASE_REJECTED'
    $payload=@{operationId=$Journal.operationId;organisationId=$Journal.organisationId}
    if($Phase -eq 'revoke-others'){
        $payload.deliveries=@(foreach($profile in $script:WorkerProfiles){
            $r=$Journal.publications[$profile]
            Assert-WorkerDelivery $r $Journal.stages[$profile] $profile $Journal.operationId
            Assert-WorkerCondition $r.readOnlyMount 'ACTUAL_CONSUMER_RO_SCOPE_REQUIRED'
            $delivery=@{}
            foreach($key in @('profile','operationId','generation','subjectId','sessionId','payloadSha256','publicationSha256','volumeName','destinationPath','ownerUid','ownerGid','directoryMode','mode','consumerService','readOnlyMount')){$delivery[$key]=$r[$key]}
            $delivery
        })
    }
    $req=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"$script:WorkerOrigin/internal/m1/worker-rotation/$Phase")
    $req.Content=[Net.Http.StringContent]::new(($payload|ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8,'application/json')
    try{
        $script:WorkerExecutionStarted=$true
        # Complete bounded response, including body, stays inside 30s deadline.
        $res=$script:WorkerContext.Client.Send($req,[Net.Http.HttpCompletionOption]::ResponseContentRead)
        try{
            Assert-WorkerCondition $res.IsSuccessStatusCode 'AUTH_PHASE_REJECTED_OR_UNCERTAIN'
            $body=$res.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-WorkerJson
            Assert-WorkerCondition ($body.operationId -eq $Journal.operationId -and $body.organisationId -eq $Journal.organisationId) 'AUTH_RESPONSE_IDENTITY_REJECTED'
            return $body
        }finally{$res.Dispose()}
    }catch{throw (Get-WorkerSafeCode $_)}finally{$req.Dispose()}
}
function Save-WorkerStages($Journal,$Prepared) {
    Assert-WorkerCondition ($Prepared.workers.Count -eq 2) 'PREPARE_PAIR_REJECTED'
    $result=@{}
    foreach($w in $Prepared.workers){
        $profile=[string]$w.profile
        Assert-WorkerCondition ($profile -cin $script:WorkerProfiles -and !$result.ContainsKey($profile)) 'PREPARE_PROFILE_REJECTED'
        Assert-WorkerCondition ([Guid]$w.subjectId -ne [Guid]$script:WorkerContext.Bindings.ownerSubjectId) 'WORKER_OWNER_IDENTITY_REJECTED'
        $p=[Zeka.WorkerCredentials.WorkerCredentialEncoding]::Create([Guid]$Journal.operationId,[long]$w.generation,[Guid]$w.subjectId,[Guid]$w.sessionId,$profile,[Guid[]]@([Guid]$Journal.organisationId),[DateTimeOffset]$w.tokenIssuedAtUtc,[DateTimeOffset]$w.accessExpiresAtUtc,[DateTimeOffset]$w.sessionCreatedAtUtc,[DateTimeOffset]$w.sessionExpiresAtUtc,[string]$w.token)
        $bytes=[Zeka.WorkerCredentials.WorkerCredentialEncoding]::Serialize($p)
        try{
            Assert-WorkerCondition ($p.PayloadSha256 -ceq $w.payloadSha256 -and (Get-WorkerHash $bytes) -ceq $w.publicationSha256) 'AUTH_STAGE_HASH_BINDING_REJECTED'
            $path=Join-Path $script:WorkerContext.OperationDirectory "$profile.json";Write-WorkerProtectedFile $path $bytes
            $result[$profile]=@{profile=$profile;path=$path;sha256=(Get-WorkerHash $bytes);subjectId=([Guid]$w.subjectId).ToString('D');sessionId=([Guid]$w.sessionId).ToString('D');generation=[long]$w.generation;payloadSha256=$p.PayloadSha256;tokenIssuedAtUtc=$p.TokenIssuedAtUtc.ToString('O');accessExpiresAtUtc=$p.AccessExpiresAtUtc.ToString('O');sessionCreatedAtUtc=$p.SessionCreatedAtUtc.ToString('O');sessionExpiresAtUtc=$p.SessionExpiresAtUtc.ToString('O')}
        }finally{[Array]::Clear($bytes,0,$bytes.Length);$w.token=$null}
    }
    Assert-WorkerCondition ($result['adminarea-retry'].subjectId -ne $result['client-projection'].subjectId -and $result['adminarea-retry'].sessionId -ne $result['client-projection'].sessionId) 'WORKER_IDENTITIES_NOT_DISTINCT'
    return $result
}
function Test-WorkerStages($Journal) {
    Assert-WorkerCondition ($Journal.stages.Count -eq 2) 'STAGED_PAIR_RECOVERY_UNAVAILABLE'
    foreach($profile in $script:WorkerProfiles){
        $s=$Journal.stages[$profile];Assert-WorkerProtectedPath $s.path $script:WorkerContext.Private|Out-Null
        $bytes=[IO.File]::ReadAllBytes($s.path)
        try{
            Assert-WorkerCondition ((Get-WorkerHash $bytes) -ceq $s.sha256) 'STAGED_FILE_HASH_REJECTED'
            $p=[Zeka.WorkerCredentials.WorkerCredentialEncoding]::Parse($bytes)
            Assert-WorkerCondition ($p.OperationId -eq [Guid]$Journal.operationId -and $p.Profile -ceq $profile -and $p.Generation -eq $s.generation -and $p.SubjectId -eq [Guid]$s.subjectId -and $p.SessionId -eq [Guid]$s.sessionId -and $p.OrganisationIds.Count -eq 1 -and $p.OrganisationIds[0] -eq [Guid]$Journal.organisationId -and $p.PayloadSha256 -ceq $s.payloadSha256 -and $p.AccessExpiresAtUtc -gt [DateTimeOffset]::UtcNow -and $p.SessionExpiresAtUtc -gt [DateTimeOffset]::UtcNow) 'STAGED_FILE_IDENTITY_OR_EXPIRY_REJECTED'
        }finally{[Array]::Clear($bytes,0,$bytes.Length)}
    }
}
function Get-WorkerResourceLabels([string]$Profile) {
    @{'com.docker.compose.project'='zeka-m1';'com.zeka.scope'='m1-worker-rotation';'com.zeka.owner.sid'=$script:WorkerContext.Bindings.ownerSid;'com.zeka.worker.profile'=$Profile;'com.zeka.worker.registry'=$script:WorkerContext.Bindings.registryId}
}
function Assert-WorkerLabels($Actual,$Expected) {
    foreach($key in $Expected.Keys){Assert-WorkerCondition ($Actual[$key] -ceq $Expected[$key]) 'DOCKER_RESOURCE_LABEL_REJECTED'}
}
function Assert-WorkerConsumerMount($Binding,[double]$TimeoutSeconds=30) {
    if($null -eq $Binding.consumer){return $null};$c=$Binding.consumer
    $items=@(Get-WorkerDockerJson @('inspect',$c.containerId) -TimeoutSeconds $TimeoutSeconds);Assert-WorkerCondition ($items.Count -eq 1) 'CONSUMER_INSPECTION_REJECTED';$i=$items[0]
    # File delivery requires the actual admitted service mount, not a running
    # worker. Unchanged PID/start and real use are later adoption evidence.
    Assert-WorkerCondition ($i.Id -ceq $c.containerId -and $i.Image -ceq $c.imageId) 'CONSUMER_CONTAINER_IMAGE_CHANGED'
    Assert-WorkerLabels $i.Config.Labels $c.labels
    Assert-WorkerCondition ($i.Config.User -cin @('1654','1654:1654') -and @($i.HostConfig.CapDrop) -contains 'ALL' -and @($i.HostConfig.CapAdd|Where-Object {$null -ne $_}).Count -eq 0 -and @($i.HostConfig.SecurityOpt) -contains 'no-new-privileges=true') 'CONSUMER_SANDBOX_REJECTED'
    Assert-WorkerCondition ($c.mounts.Count -eq @($i.Mounts).Count) 'CONSUMER_MOUNT_ALLOWLIST_COUNT_REJECTED'
    foreach($mount in $i.Mounts){
        Assert-WorkerCondition ($mount.Destination -cnotmatch '(?i)docker\.sock|containerd\.sock' -and $mount.Source -cnotmatch '(?i)docker\.sock|containerd\.sock') 'CONSUMER_ENGINE_SOCKET_REJECTED'
        $allowed=@($c.mounts|Where-Object {$_.type -ceq $mount.Type -and $_.destination -ceq $mount.Destination -and $_.source -ceq $mount.Source -and $_.name -ceq $mount['Name'] -and [bool]$_.readOnly -eq !$mount.RW})
        Assert-WorkerCondition ($allowed.Count -eq 1) 'CONSUMER_MOUNT_ALLOWLIST_REJECTED'
    }
    $mounts=@($i.Mounts|Where-Object {$_.Destination -ceq '/run/zeka-worker-credential'})
    Assert-WorkerCondition ($mounts.Count -eq 1 -and $mounts[0].Type -ceq 'volume' -and $mounts[0].Name -ceq $Binding.volumeName -and $mounts[0].RW -eq $false -and $mounts[0].Source.EndsWith('/private',[StringComparison]::Ordinal)) 'CONSUMER_DIRECTORY_MOUNT_REJECTED'
    return @{containerId=$i.Id;imageId=$i.Image;mountsSha256=(Get-WorkerHash ([Text.Encoding]::UTF8.GetBytes(($c.mounts|ConvertTo-Json -Depth 8 -Compress))))}
}
function Invoke-WorkerOwnedHelper($Journal,[string]$Profile,[string]$Volume,[string]$Command,[switch]$Initialize) {
    $suffix=if($Initialize){'init'}else{'publish'};$name="zeka-m1-worker-$($Journal.operationId.Replace('-','').Substring(0,12))-$Profile-$suffix"
    $labels=Get-WorkerResourceLabels $Profile;$labels['com.zeka.operation']=$Journal.operationId;$labels['com.zeka.source']=$Journal.sourceCommit
    $labels['com.zeka.command.sha256']=Get-WorkerHash ([Text.Encoding]::UTF8.GetBytes($Command))
    $old=Invoke-WorkerNative @('ps','-aq','--filter',"name=^/$name$")
    Assert-WorkerCondition ([string]::IsNullOrWhiteSpace($old.stdout)) 'RETAINED_HELPER_REQUIRES_INSPECTION'
    $arguments=@('create','--name',$name,'--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges=true','--log-driver','none')
    foreach($key in $labels.Keys){$arguments+=@('--label',"$key=$($labels[$key])")}
    if($Initialize){$arguments+=@('--user','0:0','--cap-add','CHOWN')}else{$arguments+=@('--user','1654:1654')}
    $arguments+=@('--mount',"type=volume,source=$Volume,target=/target,volume-nocopy")
    if(!$Initialize){
        $relative=[IO.Path]::GetRelativePath($script:WorkerContext.Private,$Journal.stages[$Profile].path).Replace('\','/')
        Assert-WorkerCondition (!$relative.StartsWith('../')) 'DOCKER_STAGE_PATH_REJECTED'
        $engineSource=$script:WorkerContext.Bindings.dockerPrivateRoot.TrimEnd('/')+'/'+$relative
        $arguments+=@('--mount',"type=bind,source=$engineSource,target=/input/current.json,readonly")
    }
    $arguments+=@($script:WorkerHelperImage,'sh','-euc',$Command)
    $created=Invoke-WorkerNative $arguments;$id=$created.stdout.Trim()
    Assert-WorkerCondition ($id -cmatch '^[a-f0-9]{64}$') 'HELPER_ID_REJECTED'
    $Journal.retainedHelperId=$id;Write-WorkerJournal $script:WorkerContext.JournalPath $Journal
    $items=@(Get-WorkerDockerJson @('inspect',$id));$i=$items[0]
    Assert-WorkerCondition ($items.Count -eq 1 -and $i.Id -ceq $id -and $i.Image -ceq $script:WorkerHelperImage -and $i.HostConfig.NetworkMode -ceq 'none' -and $i.HostConfig.ReadonlyRootfs -eq $true -and @($i.HostConfig.CapDrop) -contains 'ALL' -and @($i.HostConfig.SecurityOpt) -contains 'no-new-privileges=true') 'HELPER_ADMISSION_REJECTED'
    Assert-WorkerLabels $i.Config.Labels $labels
    Assert-WorkerCondition ($i.Config.User -ceq $(if($Initialize){'0:0'}else{'1654:1654'})) 'HELPER_USER_REJECTED'
    Assert-WorkerCondition (@($i.Config.Cmd).Count -eq 3 -and $i.Config.Cmd[0] -ceq 'sh' -and $i.Config.Cmd[1] -ceq '-euc' -and $i.Config.Cmd[2] -ceq $Command) 'HELPER_COMMAND_REJECTED'
    Assert-WorkerCondition (@($i.HostConfig.CapAdd|Where-Object {$null -ne $_}).Count -eq $(if($Initialize){1}else{0}) -and (!$Initialize -or $i.HostConfig.CapAdd[0] -ceq 'CHOWN')) 'HELPER_CAPABILITY_REJECTED'
    $mounts=@($i.Mounts);Assert-WorkerCondition ($mounts.Count -eq $(if($Initialize){1}else{2})) 'HELPER_MOUNT_COUNT_REJECTED'
    $target=@($mounts|Where-Object {$_.Destination -ceq '/target'})
    Assert-WorkerCondition ($target.Count -eq 1 -and $target[0].Type -ceq 'volume' -and $target[0].Name -ceq $Volume -and $target[0].RW -eq $true) 'HELPER_TARGET_MOUNT_REJECTED'
    if(!$Initialize){
        $inputMount=@($mounts|Where-Object {$_.Destination -ceq '/input/current.json'})
        Assert-WorkerCondition ($inputMount.Count -eq 1 -and $inputMount[0].Type -ceq 'bind' -and $inputMount[0].Source -ceq $engineSource -and $inputMount[0].RW -eq $false) 'HELPER_SECRET_MOUNT_REJECTED'
    }
    Invoke-WorkerNative @('start',$id)|Out-Null;$wait=Invoke-WorkerNative @('wait',$id) -TimeoutSeconds 45
    Assert-WorkerCondition ($wait.stdout.Trim() -ceq '0') 'HELPER_FAILED_PRESERVED'
    $after=@(Get-WorkerDockerJson @('inspect',$id))[0]
    Assert-WorkerCondition ($after.Id -ceq $id -and $after.State.Running -eq $false -and $after.State.ExitCode -eq 0) 'HELPER_EXIT_UNCERTAIN'
    # Only this exact successful owned helper is removed, before its receipt.
    # Failed/timed-out helpers and every volume are preserved for inspection.
    Invoke-WorkerNative @('rm',$id)|Out-Null;$Journal.retainedHelperId=$null
    Write-WorkerJournal $script:WorkerContext.JournalPath $Journal
}
function Get-WorkerPublishCommand([string]$Expected,[long]$Generation,[string]$Operation) {
    Assert-WorkerCondition ($Expected -cmatch '^[a-f0-9]{64}$' -and $Generation -gt 0 -and $Operation -cmatch '^[a-f0-9]{32}$') 'PUBLISH_COMMAND_INPUT_REJECTED'
    # Shared Serialize has a fixed canonical prefix. Full file SHA binds the
    # native destination to the Auth-computed staged operation/gen/sub/sid.
    # The checksum is integrity metadata; real Auth validates the bearer JWT.
    $command=@'
umask 077
test -f /input/current.json
test ! -L /input/current.json
test "$(stat -c %s /input/current.json)" -le 32768
test "$(sha256sum /input/current.json | cut -d ' ' -f 1)" = "__EXPECTED__"
incoming="$(sed -nE 's/^\{"schema":"zeka.m1.worker-credential\/v1","operationId":"[a-f0-9-]{36}","generation":([1-9][0-9]{0,18}),"subjectId":.*/\1/p' /input/current.json)"
test "$incoming" = "__GENERATION__"
test ! -L /target/private
if test ! -d /target/private; then mkdir /target/private; chmod 0500 /target/private; fi
test "$(stat -c %u:%g /target/private)" = "1654:1654"
test "$(stat -c %a /target/private)" = "500"
test ! -L /target/private/current.json
old=""
if test -e /target/private/current.json; then
  test -f /target/private/current.json
  test "$(stat -c %u:%g /target/private/current.json)" = "1654:1654"
  test "$(stat -c %a /target/private/current.json)" = "400"
  test "$(stat -c %s /target/private/current.json)" -le 32768
  old="$(sha256sum /target/private/current.json | cut -d ' ' -f 1)"
  current="$(sed -nE 's/^\{"schema":"zeka.m1.worker-credential\/v1","operationId":"[a-f0-9-]{36}","generation":([1-9][0-9]{0,18}),"subjectId":.*/\1/p' /target/private/current.json)"
  test -n "$current"
  if test "$old" = "__EXPECTED__"; then test "$current" = "__GENERATION__"; else test "$current" -lt "__GENERATION__"; fi
fi
if test "$old" != "__EXPECTED__"; then
  chmod 0700 /target/private
  trap 'chmod 0500 /target/private' EXIT HUP INT TERM
  test ! -e /target/private/.pending-__OP__
  set -C
  cat /input/current.json > /target/private/.pending-__OP__
  chmod 0400 /target/private/.pending-__OP__
  test "$(sha256sum /target/private/.pending-__OP__ | cut -d ' ' -f 1)" = "__EXPECTED__"
  sync -f /target/private/.pending-__OP__
  mv -T /target/private/.pending-__OP__ /target/private/current.json
  sync -f /target/private
  chmod 0500 /target/private
  trap - EXIT HUP INT TERM
fi
test "$(stat -c %u:%g /target/private)" = "1654:1654"
test "$(stat -c %a /target/private)" = "500"
test "$(stat -c %u:%g /target/private/current.json)" = "1654:1654"
test "$(stat -c %a /target/private/current.json)" = "400"
test "$(sha256sum /target/private/current.json | cut -d ' ' -f 1)" = "__EXPECTED__"
actual="$(sed -nE 's/^\{"schema":"zeka.m1.worker-credential\/v1","operationId":"[a-f0-9-]{36}","generation":([1-9][0-9]{0,18}),"subjectId":.*/\1/p' /target/private/current.json)"
test "$actual" = "__GENERATION__"
'@
    return $command.Replace('__EXPECTED__',$Expected).Replace('__GENERATION__',$Generation.ToString([Globalization.CultureInfo]::InvariantCulture)).Replace('__OP__',$Operation)
}

function Assert-WorkerPriorHelper($Item,[string]$Profile,[string]$Volume,[string]$IncomingSource,[bool]$SourceIsAncestor) {
    $expected=Get-WorkerResourceLabels $Profile
    Assert-WorkerLabels $Item.Config.Labels $expected
    $labels=$Item.Config.Labels
    Assert-WorkerCondition ($Item.Id -cmatch '^[a-f0-9]{64}$' -and $Item.Image -ceq $script:WorkerHelperImage -and
        $labels['com.zeka.source'] -cmatch '^[a-f0-9]{40}$' -and $SourceIsAncestor -and
        $labels['com.zeka.operation'] -cmatch '^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$' -and
        $Item.HostConfig.NetworkMode -ceq 'none' -and $Item.HostConfig.ReadonlyRootfs -eq $true -and
        $Item.HostConfig.Privileged -eq $false -and @($Item.HostConfig.CapDrop) -contains 'ALL' -and
        @($Item.HostConfig.SecurityOpt) -contains 'no-new-privileges=true' -and
        @($Item.Config.Cmd).Count -eq 3 -and $Item.Config.Cmd[0] -ceq 'sh' -and $Item.Config.Cmd[1] -ceq '-euc') 'PRIOR_HELPER_IDENTITY_REJECTED'
    $commandHash=Get-WorkerHash ([Text.Encoding]::UTF8.GetBytes([string]$Item.Config.Cmd[2]))
    Assert-WorkerCondition ($commandHash -ceq $labels['com.zeka.command.sha256']) 'PRIOR_HELPER_COMMAND_BINDING_REJECTED'
    $mounts=@($Item.Mounts);$target=@($mounts|Where-Object {$_.Destination -ceq '/target'})
    Assert-WorkerCondition ($target.Count -eq 1 -and $target[0].Type -ceq 'volume' -and $target[0].Name -ceq $Volume -and $target[0].RW -eq $true) 'PRIOR_HELPER_VOLUME_REJECTED'
    $caps=@($Item.HostConfig.CapAdd|Where-Object {$null -ne $_})
    if($Item.Config.User -ceq '0:0'){
        Assert-WorkerCondition ($mounts.Count -eq 1 -and $caps.Count -eq 1 -and $caps[0] -ceq 'CHOWN' -and
            $Item.Config.Cmd[2] -ceq 'test "$(find /target -mindepth 1 -maxdepth 1 | wc -l)" -eq 0; chown 1654:1654 /target') 'PRIOR_INITIALIZER_SCOPE_REJECTED'
    }else{
        Assert-WorkerCondition ($Item.Config.User -ceq '1654:1654' -and $caps.Count -eq 0 -and $mounts.Count -eq 2) 'PRIOR_PUBLISHER_SANDBOX_REJECTED'
        $input=@($mounts|Where-Object {$_.Destination -ceq '/input/current.json'})
        $source=$script:WorkerContext.Bindings.dockerPrivateRoot.TrimEnd('/')+'/workers/operations/'+$labels['com.zeka.operation']+'/'+$Profile+'.json'
        Assert-WorkerCondition ($input.Count -eq 1 -and $input[0].Type -ceq 'bind' -and $input[0].RW -eq $false -and $input[0].Source -ceq $source) 'PRIOR_PUBLISHER_SECRET_SCOPE_REJECTED'
    }
}
function Assert-WorkerVolumeUsers($Journal,$Binding) {
    $users=Invoke-WorkerNative @('ps','-aq','--no-trunc','--filter',"volume=$($Binding.volumeName)")
    foreach($id in @($users.stdout -split '\r?\n'|Where-Object {$_})){
        Assert-WorkerCondition ($id -cmatch '^[a-f0-9]{64}$') 'VOLUME_USER_ID_REJECTED'
        if($null -ne $Binding.consumer -and $Binding.consumer.containerId -ceq $id){continue}
        $items=@(Get-WorkerDockerJson @('inspect',$id));Assert-WorkerCondition ($items.Count -eq 1 -and $items[0].Id -ceq $id) 'VOLUME_USER_INSPECTION_REJECTED'
        $item=$items[0]
        $oldSource=[string]$item.Config.Labels['com.zeka.source']
        Assert-WorkerCondition ($oldSource -cmatch '^[a-f0-9]{40}$') 'PRIOR_HELPER_SOURCE_REJECTED'
        & git -C $script:WorkerSourceRoot merge-base --is-ancestor $oldSource $Journal.sourceCommit
        $ancestor=($LASTEXITCODE -eq 0)
        Assert-WorkerPriorHelper $item $Binding.profile $Binding.volumeName $Journal.sourceCommit $ancestor
        if($item.State.Running){
            # Only exact-owned rotation helpers may be bounded-stopped. Preserve
            # container and logs; never stop/remove historical W3 resources.
            Invoke-WorkerNative @('stop','--time','5',$id) -TimeoutSeconds 10|Out-Null
            $stopped=@(Get-WorkerDockerJson @('inspect',$id))[0]
            Assert-WorkerPriorHelper $stopped $Binding.profile $Binding.volumeName $Journal.sourceCommit $ancestor
            Assert-WorkerCondition ($stopped.Id -ceq $id -and !$stopped.State.Running -and [long]$stopped.State.Pid -eq 0) 'PRIOR_OWNED_HELPER_STOP_UNCERTAIN'
        }
    }
}
function Publish-WorkerStage($Journal,[string]$Profile) {
    Assert-WorkerEngine;Test-WorkerStages $Journal
    $binding=@($script:WorkerContext.Bindings.profiles|Where-Object {$_.profile -ceq $Profile})[0];$volume=[string]$binding.volumeName
    Assert-WorkerCondition ($volume -cmatch '^zeka-m1-worker-(adminarea|client)-rotation-[a-f0-9]{12}$') 'ROTATION_VOLUME_NAME_REJECTED'
    $labels=Get-WorkerResourceLabels $Profile
    $listed=Invoke-WorkerNative @('volume','ls','-q','--filter',"name=^$volume$");$new=[string]::IsNullOrWhiteSpace($listed.stdout)
    if($new){$arguments=@('volume','create');foreach($key in $labels.Keys){$arguments+=@('--label',"$key=$($labels[$key])")};$arguments+=$volume;Invoke-WorkerNative $arguments|Out-Null}
    $volumes=@(Get-WorkerDockerJson @('volume','inspect',$volume));$v=$volumes[0]
    Assert-WorkerCondition ($volumes.Count -eq 1 -and $v.Name -ceq $volume -and $v.Driver -ceq 'local' -and ($null -eq $v.Options -or $v.Options.Count -eq 0)) 'ROTATION_VOLUME_INSPECTION_REJECTED'
    Assert-WorkerLabels $v.Labels $labels
    Assert-WorkerVolumeUsers $Journal $binding
    $before=Assert-WorkerConsumerMount $binding
    if($new){Invoke-WorkerOwnedHelper $Journal $Profile $volume 'test "$(find /target -mindepth 1 -maxdepth 1 | wc -l)" -eq 0; chown 1654:1654 /target' -Initialize}
    $stage=$Journal.stages[$Profile]
    $command=Get-WorkerPublishCommand $stage.sha256 ([long]$stage.generation) $Journal.operationId.Replace('-','')
    Invoke-WorkerOwnedHelper $Journal $Profile $volume $command
    $after=Assert-WorkerConsumerMount $binding
    if($null -ne $before){Assert-WorkerCondition (($before|ConvertTo-Json -Compress) -ceq ($after|ConvertTo-Json -Compress)) 'CONSUMER_CHANGED_DURING_PUBLICATION'}
    $service=if($Profile -ceq 'adminarea-retry'){'adminarea'}else{'client'}
    # Exit0 is admitted only after the helper's actual native destination
    # SHA/generation/UID/GID/mode predicates. SHA identity binds all staged bytes.
    $receipt=@{profile=$Profile;verified=$true;operationId=$Journal.operationId;generation=$stage.generation;
        subjectId=$stage.subjectId;sessionId=$stage.sessionId;payloadSha256=$stage.payloadSha256;publicationSha256=$stage.sha256;
        volumeName=$volume;destinationPath='/run/zeka-worker-credential/current.json';ownerUid=1654;ownerGid=1654;directoryMode='0500';mode='0400';
        consumerService=$service;readOnlyMount=($null -ne $after);runtime=$after;sourceCommit=$Journal.sourceCommit;
        operatorSha256=$script:WorkerContext.Bindings.operatorSha256;utc=[DateTimeOffset]::UtcNow.ToString('O')}
    $script:WorkerContext.Registry.publications[$Profile]=$receipt
    Write-WorkerJournal $script:WorkerContext.RegistryPath $script:WorkerContext.Registry
    return $receipt
}



function Enter-WorkerPairLock([string]$Path) {
    try{return [IO.FileStream]::new($Path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
    catch [IO.IOException]{throw 'OWNER_COMMAND_ALREADY_RUNNING'}
}
function Invoke-WorkerOperator {
    Assert-WorkerCondition $IsWindows 'OWNER_WINDOWS_REQUIRED'
    Assert-WorkerCondition ($env:USERNAME -ceq 'herve' -and $env:USERDOMAIN -ceq 'DILI_LP' -and $env:USERPROFILE -ieq 'C:\Users\herve') 'OWNER_WINDOWS_IDENTITY_REJECTED'
    Assert-WorkerCondition ([IO.Path]::GetFullPath($PrivateDirectory).TrimEnd('\') -ieq 'C:\Users\herve\AppData\Local\Zeka\M1\private') 'M1_PRIVATE_LOGICAL_ROOT_REJECTED'
    $currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $script:WorkerContext=@{Private=$PrivateDirectory;Bindings=@{ownerSid=$currentSid}}
    Assert-WorkerProtectedPath $PrivateDirectory $PrivateDirectory -Directory|Out-Null
    $bindingsPath=Join-Path $PrivateDirectory 'workers\rotation-bindings.json'
    Assert-WorkerProtectedPath $bindingsPath $PrivateDirectory|Out-Null
    $b=[IO.File]::ReadAllText($bindingsPath)|ConvertFrom-WorkerJson
    $script:WorkerContext.Bindings=$b
    Assert-WorkerCondition ($b.schema -ceq 'zeka.m1.worker-rotation-bindings/v1' -and $b.ownerSid -ceq $currentSid -and $b.sourceCommit -cmatch '^[a-f0-9]{40}$' -and $b.publisherImageId -ceq $script:WorkerHelperImage -and $b.dockerPrivateRoot -cmatch '^/mnt/[a-z]/[^,]+$' -and $b.registryId -cmatch '^[a-f0-9]{32}$') 'OPERATOR_BINDING_REJECTED'
    Assert-WorkerCondition ([Guid]$b.ownerSubjectId -ne [Guid]::Empty -and [Guid]$b.organisationId -ne [Guid]::Empty) 'OWNER_ORGANISATION_BINDING_REJECTED'
    $head=(& git -C $script:WorkerSourceRoot rev-parse HEAD).Trim()
    Assert-WorkerCondition ($LASTEXITCODE -eq 0 -and $head -ceq $b.sourceCommit) 'SOURCE_COMMIT_REJECTED'
    $dirty=@(& git -C $script:WorkerSourceRoot status --porcelain --untracked-files=normal)
    Assert-WorkerCondition ($LASTEXITCODE -eq 0 -and $dirty.Count -eq 0) 'SOURCE_WORKTREE_NOT_CLEAN'
    $selfScan=& sonar analyze secrets $PSCommandPath 2>&1
    Assert-WorkerCondition ($LASTEXITCODE -eq 0 -and ($selfScan -join [Environment]::NewLine) -match 'No secrets found' -and ($selfScan -join [Environment]::NewLine) -notmatch 'warning') 'OPERATOR_SOURCE_SECRET_SCAN_REJECTED'
    Assert-WorkerCondition ((Get-WorkerHash ([IO.File]::ReadAllBytes($PSCommandPath))) -ceq $b.operatorSha256) 'OPERATOR_BINARY_SOURCE_HASH_REJECTED'
    $contract=Join-Path $script:WorkerSourceRoot 'Shared\WorkerCredentials\WorkerCredentialPackage.cs'
    $scan=& sonar analyze secrets $contract 2>&1
    Assert-WorkerCondition ($LASTEXITCODE -eq 0 -and ($scan -join [Environment]::NewLine) -match 'No secrets found' -and ($scan -join [Environment]::NewLine) -notmatch 'warning') 'SHARED_CONTRACT_SECRET_SCAN_REJECTED'
    Assert-WorkerCondition ((Get-WorkerHash ([IO.File]::ReadAllBytes($contract))) -ceq $b.contractSha256) 'SHARED_CONTRACT_HASH_REJECTED'
    Add-Type -Path $contract
    Assert-WorkerCondition ($b.profiles.Count -eq 2 -and @($b.profiles.profile|Sort-Object -Unique).Count -eq 2 -and @($b.profiles.profile|Where-Object {$_ -cnotin $script:WorkerProfiles}).Count -eq 0) 'ROTATION_PAIR_BINDING_REJECTED'
    Assert-WorkerCondition (@($b.profiles.volumeName|Sort-Object -Unique).Count -eq 2) 'ROTATION_VOLUMES_NOT_DISTINCT'
    foreach($profile in $b.profiles){
        $service=if($profile.profile -ceq 'adminarea-retry'){'adminarea'}else{'client'}
        Assert-WorkerCondition ($profile.volumeName -ceq "zeka-m1-worker-$service-rotation-$($b.registryId.Substring(0,12))") 'ROTATION_VOLUME_PAIR_BINDING_REJECTED'
        if($null -ne $profile.consumer){
            $c=$profile.consumer
            Assert-WorkerCondition ($c.containerId -cmatch '^[a-f0-9]{64}$' -and $c.imageId -cmatch '^sha256:[a-f0-9]{64}$') 'CONSUMER_ADMISSION_BINDING_REJECTED'
            Assert-WorkerCondition ($c.labels['com.docker.compose.project'] -ceq 'zeka-m1' -and $c.labels['com.docker.compose.service'] -ceq $service) 'CONSUMER_PROJECT_SERVICE_REJECTED'
        }
    }
    Assert-WorkerEngine
    $workers=Join-Path $PrivateDirectory 'workers';Assert-WorkerProtectedPath $workers $PrivateDirectory -Directory|Out-Null
    $lockPath=Join-Path $workers 'rotation.lock'
    if(Test-Path -LiteralPath $lockPath){Assert-WorkerProtectedPath $lockPath $PrivateDirectory|Out-Null}
    $lock=Enter-WorkerPairLock $lockPath
    try{
        $acl=Get-Acl -LiteralPath $lockPath;$acl.SetAccessRuleProtection($true,$true);Set-Acl -LiteralPath $lockPath -AclObject $acl
        Assert-WorkerProtectedPath $lockPath $PrivateDirectory|Out-Null
        $tokenPath=Join-Path $workers 'owner-access-token';$caPath=Join-Path $PrivateDirectory 'trust\ca-bundle.pem'
        Assert-WorkerProtectedPath $tokenPath $PrivateDirectory|Out-Null;Assert-WorkerProtectedPath $caPath $PrivateDirectory|Out-Null
        Assert-WorkerCondition ((Get-Item -LiteralPath $tokenPath).Length -gt 0 -and (Get-Item -LiteralPath $tokenPath).Length -le 32768) 'OWNER_TOKEN_FILE_SIZE_REJECTED'
        $token=[IO.File]::ReadAllText($tokenPath).Trim()
        Assert-WorkerCondition ($token -cmatch '^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$') 'GENUINE_OWNER_TOKEN_REQUIRED'
        $script:WorkerContext.Client=New-WorkerHttpClient $caPath $b.authCaSha256 $token;$token=$null
        $registryPath=Join-Path $workers 'rotation-registry.json';$script:WorkerContext.RegistryPath=$registryPath
        if(Test-Path -LiteralPath $registryPath){
            Assert-WorkerProtectedPath $registryPath $PrivateDirectory|Out-Null
            $registry=[IO.File]::ReadAllText($registryPath)|ConvertFrom-WorkerJson
            Assert-WorkerCondition ($registry.schema -ceq 'zeka.m1.worker-rotation-registry/v1' -and $registry.registryId -ceq $b.registryId) 'ROTATION_REGISTRY_REJECTED'
        }else{
            $registry=@{schema='zeka.m1.worker-rotation-registry/v1';registryId=$b.registryId;publications=@{}}
            Write-WorkerProtectedFile $registryPath ([Text.Encoding]::UTF8.GetBytes(($registry|ConvertTo-Json -Depth 8)))
        }
        # The operator never reads or resumes prior secret staging. Legacy
        # activeOperationId metadata is ignored, not a gate on a fresh run.
        $script:WorkerContext.Registry=$registry
        $id=if($OperationId -eq [Guid]::Empty){[Guid]::NewGuid()}else{$OperationId}
        $directory=Join-Path $workers "operations\$($id.ToString('D'))"
        Assert-WorkerCondition (!(Test-Path -LiteralPath $directory)) 'OPERATION_ALREADY_USED_RUN_NEW_COMMAND'
        Assert-WorkerProtectedPath (Split-Path $directory -Parent) $PrivateDirectory -Directory|Out-Null
        New-WorkerProtectedDirectory $directory
        $script:WorkerContext.OperationDirectory=$directory
        $script:WorkerContext.JournalPath=Join-Path $directory 'journal.json'
        $j=New-WorkerJournal $id ([Guid]$b.organisationId) $b.sourceCommit
        $ports=@{
            Save={param($j) Write-WorkerJournal $script:WorkerContext.JournalPath $j}
            Prepare={param($j) Invoke-WorkerPhase 'prepare' $j}
            Stage={param($j,$p) Save-WorkerStages $j $p}
            Publish={param($j,$p) Publish-WorkerStage $j $p}
            RevokeOthers={param($j) Test-WorkerStages $j;Invoke-WorkerPhase 'revoke-others' $j}
        }
        $result=Invoke-WorkerRotationFlow $j $ports
        return @{operationId=$result.operationId;state=$result.state;failure=$result.failure;
            profiles=@($script:WorkerProfiles|ForEach-Object {@{profile=$_;delivered=$result.publications.ContainsKey($_);otherSessionsRevoked=($result.state -eq 'DELIVERED_AND_OTHERS_REVOKED')}})
            runtimeAdoption='NOT_RUN';sourceCommit=$b.sourceCommit}
    }finally{
        if($script:WorkerContext.ContainsKey('Client')){$script:WorkerContext.Client.Dispose()};$lock.Dispose()
    }
}
if(!$DefinitionsOnly){
    if(!$Execute){@{state='NOT_RUN';reason='EXPLICIT_EXECUTE_AND_PROTECTED_OWNER_SOURCE_RUNTIME_ADMISSION_REQUIRED'}|ConvertTo-Json -Compress}
    else{try{Invoke-WorkerOperator|ConvertTo-Json -Depth 6 -Compress}catch{@{state='BLOCKED';failure=(Get-WorkerSafeCode $_);runtimeAcceptance=$(if($script:WorkerExecutionStarted){'INCOMPLETE_UNCERTAIN'}else{'NOT_RUN'})}|ConvertTo-Json -Compress;exit 1}}
}
