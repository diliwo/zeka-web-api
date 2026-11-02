#Requires -Version 7.4
param([string]$PrivateDirectory='C:\Users\herve\AppData\Local\Zeka\M1\private',[Guid]$OperationId,[switch]$Resume,[switch]$AbortPrepared,[switch]$Execute,[switch]$DefinitionsOnly,[ValidateRange(1,120)][int]$AdoptionTimeoutSeconds=30)
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
    @{schema='zeka.m1.worker-rotation-journal/v1';operationId=$Id.ToString('D');organisationId=$OrganisationId.ToString('D');sourceCommit=$SourceCommit;state='NEW';cutoverStarted=$false;publications=@{};witnesses=@{};stages=@{};history=@();failure=$null}
}
function Add-WorkerHistory($Journal,[string]$State) {
    $Journal.state=$State;$Journal.history=@($Journal.history)+@(@{state=$State;utc=[DateTimeOffset]::UtcNow.ToString('O')})
}
function Invoke-WorkerRotationFlow {
    # Pure state orchestration. Public execution supplies only fixed production
    # adapters after admission; test ports never reach the public command line.
    param([System.Collections.IDictionary]$Journal,[hashtable]$Ports,[switch]$ResumeOperation,[switch]$Abort)
    try {
        if($Journal.state -in @('FINALIZED','ABORTED')){return $Journal}
        if($Abort){
            Assert-WorkerCondition (!$Journal.cutoverStarted -and $Journal.publications.Count -eq 0) 'ABORT_FORBIDDEN_AFTER_CUTOVER'
            $i=& ($Ports.Inspect) $Journal
            Assert-WorkerCondition ($i.state -eq 'PREPARED') 'ABORT_REQUIRES_CONFIRMED_PREPARED'
            & ($Ports.Abort) $Journal|Out-Null
            Add-WorkerHistory $Journal 'ABORTED';& ($Ports.Save) $Journal|Out-Null;return $Journal
        }
        if($Journal.state -eq 'NEW'){
            Assert-WorkerCondition (!$ResumeOperation) 'RESUME_WITHOUT_PREPARED_OPERATION'
            Add-WorkerHistory $Journal 'PREPARE_SUBMITTED';& ($Ports.Save) $Journal|Out-Null
            $p=& ($Ports.Prepare) $Journal
            Assert-WorkerCondition ($p.state -eq 'PREPARED') 'PREPARE_RESPONSE_RECOVERY_REQUIRED'
            $s=& ($Ports.Stage) $Journal $p
            Assert-WorkerCondition ($s.Count -eq 2) 'PAIR_STAGE_INCOMPLETE'
            $Journal.stages=$s;$p=$null
            Add-WorkerHistory $Journal 'PREPARED';& ($Ports.Save) $Journal|Out-Null
        }elseif(!$ResumeOperation){throw 'EXISTING_OPERATION_REQUIRES_EXPLICIT_RESUME'}else{
            $i=& ($Ports.Inspect) $Journal
            if($i.state -eq 'FINALIZED'){Add-WorkerHistory $Journal 'FINALIZED';& ($Ports.Save) $Journal|Out-Null;return $Journal}
            Assert-WorkerCondition ($i.state -in @('PREPARED','CUTOVER_STARTED')) 'RECOVERY_REQUIRES_SERVER_INSPECTION'
            if($i.state -eq 'CUTOVER_STARTED'){$Journal.cutoverStarted=$true}
            # Never Prepare again. Missing stage material requires recovery.
            & ($Ports.VerifyStage) $Journal|Out-Null
        }
        if(!$Journal.cutoverStarted){
            Add-WorkerHistory $Journal 'BEGIN_CUTOVER_SUBMITTED';& ($Ports.Save) $Journal|Out-Null
            $b=& ($Ports.BeginCutover) $Journal
            Assert-WorkerCondition ($b.state -eq 'CUTOVER_STARTED') 'BEGIN_CUTOVER_NOT_CONFIRMED'
            $Journal.cutoverStarted=$true;Add-WorkerHistory $Journal 'CUTOVER_STARTED';& ($Ports.Save) $Journal|Out-Null
        }
        foreach($profile in $script:WorkerProfiles){
            $receipt=& ($Ports.Publish) $Journal $profile
            Assert-WorkerCondition ($receipt.profile -eq $profile -and $receipt.verified -eq $true) 'PUBLICATION_NOT_VERIFIED'
            $Journal.publications[$profile]=$receipt
            Add-WorkerHistory $Journal 'PARTIALLY_DELIVERED';& ($Ports.Save) $Journal|Out-Null
        }
        $observed=& ($Ports.Observe) $Journal;$Journal.witnesses=$observed.witnesses
        if($observed.ContainsKey('reason')){$Journal.failure=$observed.reason}
        if($observed.state -ne 'ADOPTED' -or $observed.witnesses.Count -ne 2){
            Add-WorkerHistory $Journal $observed.state;& ($Ports.Save) $Journal|Out-Null;return $Journal
        }
        Add-WorkerHistory $Journal 'FINALIZE_SUBMITTED';& ($Ports.Save) $Journal|Out-Null
        $f=& ($Ports.Finalize) $Journal
        Assert-WorkerCondition ($f.state -eq 'FINALIZED') 'FINALIZE_NOT_CONFIRMED'
        Add-WorkerHistory $Journal 'FINALIZED';& ($Ports.Save) $Journal|Out-Null
    }catch{
        $Journal.failure=Get-WorkerSafeCode $_
        $state=if($Journal.state -eq 'FINALIZE_SUBMITTED'){'FINALIZE_UNCERTAIN'}elseif($Journal.cutoverStarted -or $Journal.state -eq 'BEGIN_CUTOVER_SUBMITTED'){'DEGRADED_RECOVERY_REQUIRED'}else{'RECOVERY_REQUIRED'}
        Add-WorkerHistory $Journal $state;& ($Ports.Save) $Journal|Out-Null
    }
    return $Journal
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
    try{return ($r.stdout|ConvertFrom-Json -AsHashtable -DateKind String)}catch{throw 'DOCKER_JSON_REJECTED'}
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
function Invoke-WorkerPhase([string]$Phase,$Journal,[array]$Witnesses=@()) {
    Assert-WorkerCondition ($Phase -in @('prepare','begin-cutover','finalize','abort','inspect')) 'AUTH_PHASE_REJECTED'
    $payload=@{operationId=$Journal.operationId;organisationId=$Journal.organisationId};if($Phase -eq 'finalize'){$payload.witnesses=$Witnesses}
    $req=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"$script:WorkerOrigin/internal/m1/worker-rotation/$Phase")
    $req.Content=[Net.Http.StringContent]::new(($payload|ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8,'application/json')
    try{
        $script:WorkerExecutionStarted=$true
        # Buffer the bounded complete body inside HttpClient's request deadline.
        # ResponseHeadersRead followed by sync Read would leave a stalled body
        # outside HttpClient.Timeout.
        $res=$script:WorkerContext.Client.Send($req,[Net.Http.HttpCompletionOption]::ResponseContentRead)
        try{
            Assert-WorkerCondition $res.IsSuccessStatusCode 'AUTH_PHASE_REJECTED_OR_UNCERTAIN'
            Assert-WorkerCondition ($res.Content.Headers.ContentLength -le 262144) 'AUTH_RESPONSE_TOO_LARGE'
            $s=$res.Content.ReadAsStream();$m=[IO.MemoryStream]::new()
            try{
                $b=[byte[]]::new(4096)
                while(($n=$s.Read($b,0,$b.Length)) -gt 0){Assert-WorkerCondition ($m.Length+$n -le 262144) 'AUTH_RESPONSE_TOO_LARGE';$m.Write($b,0,$n)}
                $body=[Text.Encoding]::UTF8.GetString($m.ToArray())|ConvertFrom-Json -AsHashtable -DateKind String
            }finally{$s.Dispose();$m.Dispose()}
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
            Assert-WorkerCondition ($p.OperationId -eq [Guid]$Journal.operationId -and $p.Profile -ceq $profile -and $p.Generation -eq $s.generation -and $p.SessionId -eq [Guid]$s.sessionId) 'STAGED_FILE_IDENTITY_REJECTED'
        }finally{[Array]::Clear($bytes,0,$bytes.Length)}
    }
}
function Get-WorkerResourceLabels([string]$Profile) {
    @{'com.docker.compose.project'='zeka-m1';'com.zeka.scope'='m1-worker-rotation';'com.zeka.owner.sid'=$script:WorkerContext.Bindings.ownerSid;'com.zeka.worker.profile'=$Profile;'com.zeka.worker.registry'=$script:WorkerContext.Bindings.registryId}
}
function Assert-WorkerLabels($Actual,$Expected) {
    foreach($key in $Expected.Keys){Assert-WorkerCondition ($Actual[$key] -ceq $Expected[$key]) 'DOCKER_RESOURCE_LABEL_REJECTED'}
}
function Assert-WorkerConsumer($Binding,[double]$TimeoutSeconds=30) {
    if($null -eq $Binding.consumer){return $null};$c=$Binding.consumer
    $items=@(Get-WorkerDockerJson @('inspect',$c.containerId) -TimeoutSeconds $TimeoutSeconds);Assert-WorkerCondition ($items.Count -eq 1) 'CONSUMER_INSPECTION_REJECTED';$i=$items[0]
    # Docker Pid is a HOST namespace identity; never compare with log processId.
    Assert-WorkerCondition ($i.Id -ceq $c.containerId -and $i.Image -ceq $c.imageId -and $i.State.Running -eq $true -and [long]$i.State.Pid -eq [long]$c.hostPid -and $i.State.StartedAt -ceq $c.startedAt) 'CONSUMER_RUNTIME_IDENTITY_CHANGED'
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
    return @{containerId=$i.Id;hostPid=[long]$i.State.Pid;startedAt=$i.State.StartedAt;imageId=$i.Image;loggingDriver=$i.HostConfig.LogConfig.Type}
}
function Invoke-WorkerOwnedHelper($Journal,[string]$Profile,[string]$Volume,[string]$Command,[switch]$Initialize) {
    $suffix=if($Initialize){'init'}else{'publish'};$name="zeka-m1-worker-$($Journal.operationId.Replace('-','').Substring(0,12))-$Profile-$suffix"
    $labels=Get-WorkerResourceLabels $Profile;$labels['com.zeka.operation']=$Journal.operationId;$labels['com.zeka.source']=$Journal.sourceCommit
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
function Get-WorkerPublishCommand([string]$Expected,[string]$Previous,[string]$Operation) {
    Assert-WorkerCondition ($Expected -cmatch '^[a-f0-9]{64}$' -and ($Previous -ceq '' -or $Previous -cmatch '^[a-f0-9]{64}$') -and $Operation -cmatch '^[a-f0-9]{32}$') 'PUBLISH_COMMAND_INPUT_REJECTED'
    $command=@'
umask 077
test -f /input/current.json
test ! -L /input/current.json
test "$(sha256sum /input/current.json | cut -d ' ' -f 1)" = "__EXPECTED__"
test ! -L /target/private
if test ! -d /target/private; then mkdir /target/private; chmod 0500 /target/private; fi
test "$(stat -c %u:%g /target/private)" = "1654:1654"
test "$(stat -c %a /target/private)" = "500"
test ! -L /target/private/current.json
old=""
if test -e /target/private/current.json; then
  test -f /target/private/current.json
  test "$(stat -c %a /target/private/current.json)" = "400"
  old="$(sha256sum /target/private/current.json | cut -d ' ' -f 1)"
fi
test "$old" = "__PREVIOUS__" -o "$old" = "__EXPECTED__"
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
test "$(stat -c %a /target/private)" = "500"
test "$(stat -c %a /target/private/current.json)" = "400"
test "$(sha256sum /target/private/current.json | cut -d ' ' -f 1)" = "__EXPECTED__"
'@
    return $command.Replace('__EXPECTED__',$Expected).Replace('__PREVIOUS__',$Previous).Replace('__OP__',$Operation)
}
function Get-WorkerPublicationLowerBound($Journal,[string]$Profile,[scriptblock]$Save) {
    $stage=$Journal.stages[$Profile]
    if(!$stage.ContainsKey('publicationNotBeforeUtc')){
        $stage.publicationNotBeforeUtc=[DateTimeOffset]::UtcNow.ToString('O')
        # Durable before any create/start/write, including uncertain failures.
        & $Save $Journal|Out-Null
    }
    return [string]$stage.publicationNotBeforeUtc
}
function Publish-WorkerStage($Journal,[string]$Profile) {
    $publicationStartedAt=Get-WorkerPublicationLowerBound $Journal $Profile {param($j) Write-WorkerJournal $script:WorkerContext.JournalPath $j}
    Assert-WorkerEngine;Test-WorkerStages $Journal
    $binding=@($script:WorkerContext.Bindings.profiles|Where-Object {$_.profile -ceq $Profile})[0];$volume=[string]$binding.volumeName
    # Separate rotation volumes only. This strict pattern cannot name any of
    # the five SEALED TLS/config volumes or future static appsettings volumes.
    Assert-WorkerCondition ($volume -cmatch '^zeka-m1-worker-(adminarea|client)-rotation-[a-f0-9]{12}$') 'ROTATION_VOLUME_NAME_REJECTED'
    $labels=Get-WorkerResourceLabels $Profile
    $listed=Invoke-WorkerNative @('volume','ls','-q','--filter',"name=^$volume$");$new=[string]::IsNullOrWhiteSpace($listed.stdout)
    if($new){$arguments=@('volume','create');foreach($key in $labels.Keys){$arguments+=@('--label',"$key=$($labels[$key])")};$arguments+=$volume;Invoke-WorkerNative $arguments|Out-Null}
    $volumes=@(Get-WorkerDockerJson @('volume','inspect',$volume));$v=$volumes[0]
    Assert-WorkerCondition ($volumes.Count -eq 1 -and $v.Name -ceq $volume -and $v.Driver -ceq 'local' -and ($null -eq $v.Options -or $v.Options.Count -eq 0)) 'ROTATION_VOLUME_INSPECTION_REJECTED'
    Assert-WorkerLabels $v.Labels $labels
    $users=Invoke-WorkerNative @('ps','-aq','--filter',"volume=$volume")
    foreach($id in @($users.stdout -split '\r?\n'|Where-Object {$_})){
        Assert-WorkerCondition ($null -ne $binding.consumer -and $binding.consumer.containerId.StartsWith($id,[StringComparison]::Ordinal)) 'ROTATION_VOLUME_FOREIGN_OR_RETAINED_USER'
    }
    $before=Assert-WorkerConsumer $binding
    if($new){Invoke-WorkerOwnedHelper $Journal $Profile $volume 'test "$(find /target -mindepth 1 -maxdepth 1 | wc -l)" -eq 0; chown 1654:1654 /target' -Initialize}
    $previous='';if($script:WorkerContext.Registry.publications.ContainsKey($Profile)){$previous=$script:WorkerContext.Registry.publications[$Profile].sha256}
    $command=Get-WorkerPublishCommand $Journal.stages[$Profile].sha256 $previous $Journal.operationId.Replace('-','')
    Invoke-WorkerOwnedHelper $Journal $Profile $volume $command
    $after=Assert-WorkerConsumer $binding
    if($null -ne $before){Assert-WorkerCondition (($before|ConvertTo-Json -Compress) -ceq ($after|ConvertTo-Json -Compress)) 'CONSUMER_CHANGED_DURING_PUBLICATION'}
    $receipt=@{profile=$Profile;verified=$true;sha256=$Journal.stages[$Profile].sha256;volume=$volume;operationId=$Journal.operationId;generation=$Journal.stages[$Profile].generation;sessionId=$Journal.stages[$Profile].sessionId;sourceCommit=$Journal.sourceCommit;operatorSha256=$script:WorkerContext.Bindings.operatorSha256;runtime=$after;notBeforeUtc=$publicationStartedAt;utc=[DateTimeOffset]::UtcNow.ToString('O')}
    $script:WorkerContext.Registry.publications[$Profile]=$receipt
    Write-WorkerJournal $script:WorkerContext.RegistryPath $script:WorkerContext.Registry
    return $receipt
}
function ConvertFrom-WorkerAdoptionEvent([string]$Line,$Expected) {
    # Complete JSON or the JSON console logger's Message only. Never substring
    # extract, persist or print arbitrary unmatched application logs.
    try{
        $e=$Line|ConvertFrom-Json -AsHashtable -DateKind String
        if($e.ContainsKey('Message') -and $e.Message -is [string]){$e=$e.Message|ConvertFrom-Json -AsHashtable -DateKind String}
        $keys=@('event','profile','operationId','generation','processId','processInstanceId','utc')
        if(@($e.Keys|Where-Object {$_ -cnotin $keys}).Count -ne 0 -or $e.Keys.Count -ne $keys.Count){return $null}
        if($e.event -cne 'zeka-worker-credential-adopted' -or $e.profile -cne $Expected.profile -or [Guid]$e.operationId -ne [Guid]$Expected.operationId -or [long]$e.generation -ne [long]$Expected.generation -or [int]$e.processId -ne [int]$Expected.processId -or [Guid]$e.processInstanceId -ne [Guid]$Expected.processInstanceId){return $null}
        $utc=[DateTimeOffset]$e.utc;if($utc -lt [DateTimeOffset]$Expected.notBeforeUtc -or $utc -gt [DateTimeOffset]::UtcNow){return $null}
        return @{profile=[string]$e.profile;operationId=([Guid]$e.operationId).ToString('D');generation=[long]$e.generation;processId=[int]$e.processId;processInstanceId=([Guid]$e.processInstanceId).ToString('D');utc=$utc.ToString('O')}
    }catch{return $null}
}
function Wait-WorkerAdoption($Journal) {
    $witnesses=@{}
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($AdoptionTimeoutSeconds)
    try{
    foreach($b in $script:WorkerContext.Bindings.profiles){
        if($null -eq $b.consumer){return @{state='ISSUED_DELIVERED_PENDING_FIRST_USE';witnesses=$witnesses;reason='CONSUMER_NOT_ADMITTED_OR_STARTED'}}
        $snapshot=Assert-WorkerConsumer $b -TimeoutSeconds (Get-WorkerRemainingBudget $deadline)
        if($snapshot.loggingDriver -eq 'none'){return @{state='DELIVERED_PENDING_ADOPTION';witnesses=$witnesses;reason='PRESERVED_LOGGING_DRIVER_NONE_WITNESS_UNAVAILABLE'}}
    }
    do{
        foreach($b in $script:WorkerContext.Bindings.profiles){
            $profile=$b.profile;if($witnesses.ContainsKey($profile)){continue};$before=Assert-WorkerConsumer $b -TimeoutSeconds (Get-WorkerRemainingBudget $deadline)
            $r=$Journal.publications[$profile]
            $expected=@{profile=$profile;operationId=$Journal.operationId;generation=$Journal.stages[$profile].generation;processId=$b.consumer.processId;processInstanceId=$b.consumer.processInstanceId;notBeforeUtc=$r.notBeforeUtc}
            $logs=Invoke-WorkerNative @('logs','--since',$r.notBeforeUtc,'--tail','256',$b.consumer.containerId) -TimeoutSeconds (Get-WorkerRemainingBudget $deadline) -AllowFailure
            if($logs.exitCode -ne 0){continue}
            foreach($line in (($logs.stdout+[Environment]::NewLine+$logs.stderr) -split '\r?\n')){
                $e=ConvertFrom-WorkerAdoptionEvent $line $expected;if($null -eq $e){continue}
                $after=Assert-WorkerConsumer $b -TimeoutSeconds (Get-WorkerRemainingBudget $deadline)
                Assert-WorkerCondition (($before|ConvertTo-Json -Compress) -ceq ($after|ConvertTo-Json -Compress)) 'CONSUMER_CHANGED_DURING_ADOPTION'
                $digest=Get-WorkerHash ([Text.Encoding]::UTF8.GetBytes(($e|ConvertTo-Json -Compress)))
                $witnesses[$profile]=@{profile=$profile;generation=$e.generation;sessionId=$Journal.stages[$profile].sessionId;publicationSha256=$r.sha256;successfulUseEvidenceRef="worker-adoption/$digest";processId=$e.processId;containerId=$b.consumer.containerId;processInstanceId=$e.processInstanceId};break
            }
            $logs=$null
        }
        if($witnesses.Count -eq 2){return @{state='ADOPTED';witnesses=$witnesses}}
        Start-Sleep -Milliseconds ([int][Math]::Min(250,(Get-WorkerRemainingBudget $deadline)*1000))
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    }catch{
        $code=Get-WorkerSafeCode $_
        if($code -notin @('ADOPTION_DEADLINE_REACHED','DOCKER_NATIVE_TIMEOUT_UNCERTAIN')){throw}
    }
    return @{state='DELIVERED_PENDING_ADOPTION';witnesses=$witnesses;reason='BOUNDED_ACTUAL_USE_WITNESS_TIMEOUT'}
}
function Get-WorkerRemainingBudget([DateTimeOffset]$Deadline) {
    $remaining=($Deadline-[DateTimeOffset]::UtcNow).TotalSeconds
    Assert-WorkerCondition ($remaining -gt 0) 'ADOPTION_DEADLINE_REACHED'
    return [Math]::Min(120,[Math]::Max(0.001,$remaining))
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
    $b=[IO.File]::ReadAllText($bindingsPath)|ConvertFrom-Json -AsHashtable -DateKind String
    $script:WorkerContext.Bindings=$b
    # Exact independently admitted bindings: schema/source and binary hashes,
    # fixed Owner/organisation, actual engine, protected physical mount mapping,
    # pair registry identity and exact current consumer identities (or null).
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
    $volumes=@($b.profiles.volumeName|Sort-Object -Unique)
    Assert-WorkerCondition ($volumes.Count -eq 2) 'ROTATION_VOLUMES_NOT_DISTINCT'
    foreach($profile in $b.profiles){
        $service=if($profile.profile -ceq 'adminarea-retry'){'adminarea'}else{'client'}
        Assert-WorkerCondition ($profile.volumeName -ceq "zeka-m1-worker-$service-rotation-$($b.registryId.Substring(0,12))") 'ROTATION_VOLUME_PAIR_BINDING_REJECTED'
        if($null -ne $profile.consumer){
            $c=$profile.consumer
            Assert-WorkerCondition ($c.containerId -cmatch '^[a-f0-9]{64}$' -and $c.imageId -cmatch '^sha256:[a-f0-9]{64}$' -and [long]$c.hostPid -gt 0 -and [int]$c.processId -gt 0 -and [Guid]$c.processInstanceId -ne [Guid]::Empty) 'CONSUMER_ADMISSION_BINDING_REJECTED'
            Assert-WorkerCondition ($c.labels['com.docker.compose.project'] -ceq 'zeka-m1' -and $c.labels['com.docker.compose.service'] -ceq $service) 'CONSUMER_PROJECT_SERVICE_REJECTED'
        }
    }
    Assert-WorkerEngine
    $workers=Join-Path $PrivateDirectory 'workers';Assert-WorkerProtectedPath $workers $PrivateDirectory -Directory|Out-Null
    $lockPath=Join-Path $workers 'rotation.lock'
    # A single local publisher holds the pair lock through all phase, volume,
    # observation and registry writes. Auth independently serializes DB phases.
    $lock=[IO.FileStream]::new($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
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
            $registry=[IO.File]::ReadAllText($registryPath)|ConvertFrom-Json -AsHashtable -DateKind String
            Assert-WorkerCondition ($registry.schema -ceq 'zeka.m1.worker-rotation-registry/v1' -and $registry.registryId -ceq $b.registryId) 'ROTATION_REGISTRY_REJECTED'
        }else{
            Assert-WorkerCondition (!$Resume -and !$AbortPrepared) 'ROTATION_REGISTRY_MISSING'
            $registry=@{schema='zeka.m1.worker-rotation-registry/v1';registryId=$b.registryId;publications=@{};activeOperationId=$null}
            Write-WorkerProtectedFile $registryPath ([Text.Encoding]::UTF8.GetBytes(($registry|ConvertTo-Json -Depth 8)))
        }
        Assert-WorkerCondition ($OperationId -ne [Guid]::Empty) 'EXPLICIT_OPERATION_ID_REQUIRED'
        Assert-WorkerCondition ([string]::IsNullOrEmpty($registry.activeOperationId) -or $registry.activeOperationId -eq $OperationId.ToString('D')) 'UNRESOLVED_PREVIOUS_PAIR_OPERATION'
        $script:WorkerContext.Registry=$registry
        $directory=Join-Path $workers "operations\$($OperationId.ToString('D'))";$script:WorkerContext.OperationDirectory=$directory
        $journalPath=Join-Path $directory 'journal.json';$script:WorkerContext.JournalPath=$journalPath
        if($Resume -or $AbortPrepared){
            Assert-WorkerProtectedPath $directory $PrivateDirectory -Directory|Out-Null;Assert-WorkerProtectedPath $journalPath $PrivateDirectory|Out-Null
            $j=[IO.File]::ReadAllText($journalPath)|ConvertFrom-Json -AsHashtable -DateKind String
            Assert-WorkerCondition ($j.schema -ceq 'zeka.m1.worker-rotation-journal/v1' -and $j.operationId -eq $OperationId.ToString('D') -and $j.organisationId -eq $b.organisationId -and $j.sourceCommit -eq $b.sourceCommit) 'RESUME_JOURNAL_IDENTITY_REJECTED'
        }else{
            Assert-WorkerProtectedPath (Split-Path $directory -Parent) $PrivateDirectory -Directory|Out-Null
            New-WorkerProtectedDirectory $directory;$j=New-WorkerJournal $OperationId ([Guid]$b.organisationId) $b.sourceCommit
        }
        $registry.activeOperationId=$OperationId.ToString('D');Write-WorkerJournal $registryPath $registry
        $ports=@{
            Save={param($j) Write-WorkerJournal $script:WorkerContext.JournalPath $j}
            Prepare={param($j) Invoke-WorkerPhase 'prepare' $j}
            Stage={param($j,$p) Save-WorkerStages $j $p}
            VerifyStage={param($j) Test-WorkerStages $j}
            BeginCutover={param($j) Invoke-WorkerPhase 'begin-cutover' $j}
            Publish={param($j,$p) Publish-WorkerStage $j $p}
            Observe={param($j) Wait-WorkerAdoption $j}
            Finalize={param($j) Invoke-WorkerPhase 'finalize' $j @($j.witnesses.Values)}
            Inspect={param($j) Invoke-WorkerPhase 'inspect' $j}
            Abort={param($j) Invoke-WorkerPhase 'abort' $j}
        }
        $result=Invoke-WorkerRotationFlow $j $ports -ResumeOperation:$Resume -Abort:$AbortPrepared
        if($result.state -in @('FINALIZED','ABORTED')){$registry.activeOperationId=$null;Write-WorkerJournal $registryPath $registry}
        return @{operationId=$result.operationId;state=$result.state;failure=$result.failure;profiles=@($script:WorkerProfiles|ForEach-Object {@{profile=$_;delivered=$result.publications.ContainsKey($_);adopted=$result.witnesses.ContainsKey($_)}});runtimeAcceptance='NOT_IMPLIED';sourceCommit=$b.sourceCommit}
    }finally{
        if($script:WorkerContext.ContainsKey('Client')){$script:WorkerContext.Client.Dispose()};$lock.Dispose()
    }
}
if(!$DefinitionsOnly){
    if(!$Execute){@{state='NOT_RUN';reason='EXPLICIT_EXECUTE_AND_PROTECTED_OWNER_SOURCE_RUNTIME_ADMISSION_REQUIRED'}|ConvertTo-Json -Compress}
    else{try{Invoke-WorkerOperator|ConvertTo-Json -Depth 6 -Compress}catch{@{state='BLOCKED';failure=(Get-WorkerSafeCode $_);runtimeAcceptance=$(if($script:WorkerExecutionStarted){'INCOMPLETE_UNCERTAIN'}else{'NOT_RUN'})}|ConvertTo-Json -Compress;exit 1}}
}
