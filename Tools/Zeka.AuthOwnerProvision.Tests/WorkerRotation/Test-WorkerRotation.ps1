#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$operator=Join-Path $root 'Deployments\local-stack\Invoke-WorkerCredentialRotation.ps1'
$contract=Join-Path $root 'Shared\WorkerCredentials\WorkerCredentialPackage.cs'
$overlay=Join-Path $root 'Deployments\local-stack\compose.worker-rotation.yaml'
function Confirm-Source([string]$Path){
    $scan=& sonar analyze secrets $Path 2>&1
    if($LASTEXITCODE -ne 0 -or ($scan -join [Environment]::NewLine) -notmatch 'No secrets found' -or ($scan -join [Environment]::NewLine) -match 'warning'){throw 'SOURCE_SCAN_REJECTED'}
}
Confirm-Source $operator
. $operator -DefinitionsOnly
Confirm-Source $contract
Add-Type -Path $contract
$script:checks=0
function Check([bool]$Value,[string]$Name){$script:checks++;if(!$Value){throw "PURE_ASSERTION_FAILED_$Name"}}
$jsonFixture='{"timestamp":"2026-11-02T15:00:00.1234567Z","generation":9223372036854775807,"array":[null,[],[1]],"scope":{"organisationIds":["83ea213f-5e68-4f85-bede-e49099863321"]}}'
$jsonParsed=ConvertFrom-WorkerJson $jsonFixture
Check ($jsonParsed.timestamp -is [string] -and $jsonParsed.timestamp -ceq '2026-11-02T15:00:00.1234567Z') 'JSON_TIMESTAMP_EXACT_STRING_TICKS'
Check ($jsonParsed.generation -is [long] -and $jsonParsed.generation -eq [long]::MaxValue) 'JSON_INT64_GENERATION_EXACT'
Check ($jsonParsed.array.Count -eq 3 -and $null -eq $jsonParsed.array[0] -and $jsonParsed.array[1].Count -eq 0 -and $jsonParsed.array[2][0] -eq 1) 'JSON_NESTED_ARRAY_NULL_PRESERVED'
Check ($jsonParsed.scope.organisationIds.Count -eq 1 -and $jsonParsed.scope.organisationIds[0] -ceq '83ea213f-5e68-4f85-bede-e49099863321') 'JSON_SCOPE_ARRAY_PRESERVED'
$duplicateRejected=$false;try{ConvertFrom-WorkerJson '{"scope":1,"Scope":2}'|Out-Null}catch{$duplicateRejected=$_.Exception.Message -eq 'JSON_DUPLICATE_PROPERTY_REJECTED'}
Check $duplicateRejected 'JSON_DUPLICATE_SECURITY_FIELD_REJECTED'
function New-Scenario([string]$Failure=''){
    $state=@{calls=[Collections.Generic.List[string]]::new();failure=$Failure;generation=0;mounts=$true;badProof='';ownerToken=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))}
    $ports=@{
        Save={param($j) $state.calls.Add('save')}.GetNewClosure()
        Prepare={param($j)
            $state.calls.Add('prepare');if($state.failure -eq 'prepare'){throw 'SECOND_IDENTITY_ISSUE_FAILED'}
            $state.generation++;@{state=$(if($state.failure -eq 'replay'){'ISSUED_REPLAY_NO_PRIVATE_VALUES'}else{'ISSUED'})}
        }.GetNewClosure()
        Stage={param($j,$p)
            $state.calls.Add('stage');if($state.failure -eq 'stage'){throw 'SECOND_STAGE_FAILED'}
            $stages=@{};foreach($profile in @('adminarea-retry','client-projection')){
                $stages[$profile]=@{subjectId=[Guid]::NewGuid().ToString('D');sessionId=[Guid]::NewGuid().ToString('D');generation=$state.generation;sha256=('a'*64);payloadSha256=('b'*64);accessExpiresAtUtc=[DateTimeOffset]::UtcNow.AddHours(1).ToString('O')}
            };$stages
        }.GetNewClosure()
        Publish={param($j,$profile)
            $state.calls.Add("publish/$profile");if($state.failure -eq "publish/$profile"){throw 'CONTROLLED_PUBLISH_FAILED'}
            $s=$j.stages[$profile];$service=if($profile -eq 'adminarea-retry'){'adminarea'}else{'client'}
            $r=@{verified=$true;profile=$profile;operationId=$j.operationId;generation=$s.generation;subjectId=$s.subjectId;sessionId=$s.sessionId;payloadSha256=$s.payloadSha256;publicationSha256=$s.sha256;volumeName="zeka-m1-worker-$service-rotation-aaaaaaaaaaaa";destinationPath='/run/zeka-worker-credential/current.json';ownerUid=1654;ownerGid=1654;directoryMode='0500';mode='0400';consumerService=$service;readOnlyMount=$state.mounts}
            switch($state.badProof){
                'generation' {$r.generation++}
                'fileHash' {$r.publicationSha256='c'*64}
                'payloadHash' {$r.payloadSha256='c'*64}
                'subject' {$r.subjectId=[Guid]::NewGuid().ToString('D')}
                'session' {$r.sessionId=[Guid]::NewGuid().ToString('D')}
                'uid' {$r.ownerUid=0}
                'gid' {$r.ownerGid=0}
                'mode' {$r.mode='0644'}
                'directoryMode' {$r.directoryMode='0700'}
                'volume' {$r.volumeName='zeka-m1-auth-config'}
                'service' {$r.consumerService='auth'}
                'destination' {$r.destinationPath='/tmp/current.json'}
                'verified' {$r.verified=$false}
            };$r
        }.GetNewClosure()
        RevokeOthers={param($j) $state.calls.Add('revoke');if($state.failure -eq 'revoke'){throw 'CONTROLLED_REVOCATION_FAILED'};@{state='REVOKED_OTHERS'}}.GetNewClosure()
    }
    @{state=$state;ports=$ports;journal=(New-WorkerJournal ([Guid]::NewGuid()) ([Guid]::NewGuid()) ('a'*40))}
}
$s=New-Scenario;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
Check ($r.state -eq 'DELIVERED_AND_OTHERS_REVOKED') 'FRESH_PAIR_FORWARD_SUCCESS'
Check ($s.state.calls.IndexOf('stage') -lt $s.state.calls.IndexOf('publish/adminarea-retry')) 'BOTH_STAGED_BEFORE_PUBLISH'
Check ($s.state.calls.IndexOf('publish/client-projection') -lt $s.state.calls.IndexOf('revoke')) 'BOTH_DELIVERIES_BEFORE_REVOKE'
Check (@($s.state.calls|Where-Object {$_ -eq 'revoke'}).Count -eq 1) 'REVOKE_EXACTLY_ONCE'
Check (!(($r|ConvertTo-Json -Depth 12 -Compress).Contains($s.state.ownerToken))) 'OWNER_CREDENTIAL_NOT_OUTPUT'
foreach($failure in @('prepare','stage','replay')){
    $s=New-Scenario $failure;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
    Check ($r.state -eq 'FAILED_WITHOUT_REVOCATION') "$($failure)_FAIL_CLOSED"
    Check (!$s.state.calls.Contains('publish/adminarea-retry') -and !$s.state.calls.Contains('revoke')) "$($failure)_NO_PUBLISH_OR_REVOKE"
}
foreach($failure in @('publish/adminarea-retry','publish/client-projection')){
    $s=New-Scenario $failure;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
    Check ($r.state -eq 'FAILED_WITHOUT_REVOCATION' -and !$s.state.calls.Contains('revoke')) "$($failure)_OLD_SESSIONS_UNTOUCHED"
    if($failure -eq 'publish/client-projection'){Check ($r.publications.Count -eq 1) 'FIRST_DELIVERY_PRESERVED_AFTER_SECOND_FAILURE'}
}
$s=New-Scenario 'revoke';$r=Invoke-WorkerRotationFlow $s.journal $s.ports
Check ($r.state -eq 'REVOCATION_FAILED_OR_UNCERTAIN' -and $r.publications.Count -eq 2) 'REVOCATION_FAILURE_NOT_SUCCESS'
Check (@($s.state.calls|Where-Object {$_ -eq 'prepare'}).Count -eq 1) 'NO_AUTOMATIC_REISSUE'
$oldOperation=$r.operationId
foreach($staged in $r.stages.Values){$staged.accessExpiresAtUtc=[DateTimeOffset]::UtcNow.AddHours(-2).ToString('O')}
$s.state.failure=''
$new=New-WorkerJournal ([Guid]::NewGuid()) ([Guid]$r.organisationId) ('a'*40)
$newResult=Invoke-WorkerRotationFlow $new $s.ports
Check ($newResult.state -eq 'DELIVERED_AND_OTHERS_REVOKED' -and $newResult.operationId -ne $oldOperation) 'FRESH_RUN_AFTER_EXPIRED_STAGING'
Check ($newResult.stages['adminarea-retry'].generation -eq 2) 'NEW_AUTH_GENERATION'
$s=New-Scenario;$s.state.mounts=$false;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
Check ($r.state -eq 'ISSUED_DELIVERED_MOUNT_SCOPE_PENDING' -and $r.publications.Count -eq 2) 'ACTUAL_MOUNT_SCOPE_PENDING'
Check (!$s.state.calls.Contains('revoke')) 'NO_MOUNT_SCOPE_NO_REVOKE'
foreach($defect in @('generation','fileHash','payloadHash','subject','session','uid','gid','mode','directoryMode','volume','service','destination','verified')){
    $s=New-Scenario;$s.state.badProof=$defect;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
    Check ($r.state -eq 'FAILED_WITHOUT_REVOCATION' -and !$s.state.calls.Contains('revoke')) "FILE_PROOF_$($defect)_REJECTED"
}
# Actual exclusive OS lock, only a disposable public temporary fixture.
$lockPath=Join-Path ([IO.Path]::GetTempPath()) "zeka-worker-pure-lock-$([Guid]::NewGuid().ToString('N'))"
try{
    $held=Enter-WorkerPairLock $lockPath
    try{
        $refused=$false;try{$other=Enter-WorkerPairLock $lockPath;$other.Dispose()}catch{$refused=$_.Exception.Message -eq 'OWNER_COMMAND_ALREADY_RUNNING'}
        Check $refused 'CONCURRENT_COMMAND_REFUSED'
    }finally{$held.Dispose()}
    $next=Enter-WorkerPairLock $lockPath;$next.Dispose();Check $true 'NEXT_EXPLICIT_COMMAND_ALLOWED'
}finally{if(Test-Path -LiteralPath $lockPath){[IO.File]::Delete($lockPath)}}

# No native command is invoked: this port exposes only a synthetic Docker
# inspection with deliberately different HOST and container process identities.
$consumer=@{containerId=('c'*64);imageId=('sha256:'+('d'*64));hostPid=54321;startedAt='2026-11-02T12:00:00Z';processId=1;processInstanceId=[Guid]::NewGuid();labels=@{'com.docker.compose.project'='zeka-m1';'com.docker.compose.service'='adminarea'};mounts=@(@{type='volume';name='synthetic-rotation';source='/var/lib/docker/volumes/synthetic/_data/private';destination='/run/zeka-worker-credential';readOnly=$true})}
$binding=@{consumer=$consumer;volumeName='synthetic-rotation'}
$script:fakeInspection=@{Id=$consumer.containerId;Image=$consumer.imageId;State=@{Running=$true;Pid=54321;StartedAt=$consumer.startedAt};Config=@{User='1654:1654';Labels=$consumer.labels};HostConfig=@{CapDrop=@('ALL');CapAdd=@();SecurityOpt=@('no-new-privileges=true');LogConfig=@{Type='none'}};Mounts=@(@{Type='volume';Name='synthetic-rotation';Source='/var/lib/docker/volumes/synthetic/_data/private';Destination='/run/zeka-worker-credential';RW=$false})}
function Get-WorkerDockerJson([string[]]$Arguments,[double]$TimeoutSeconds=30){return $script:fakeInspection}
Check ((Assert-WorkerConsumerMount $binding).containerId -eq $consumer.containerId) 'ACTUAL_CONTAINER_MOUNT_SCOPE'
$script:fakeInspection.State.Running=$false;$script:fakeInspection.State.Pid=0
Check ($null -ne (Assert-WorkerConsumerMount $binding)) 'STOPPED_CONTAINER_RO_SCOPE_NO_ADOPTION_GATE'
foreach($defect in @('uid','caps','nnp','extra-mount')){
    switch($defect){
        'uid' {$script:fakeInspection.Config.User='0:0'}
        'caps' {$script:fakeInspection.HostConfig.CapDrop=@()}
        'nnp' {$script:fakeInspection.HostConfig.SecurityOpt=@()}
        'extra-mount' {$script:fakeInspection.Mounts+=@(@{Type='bind';Source='/var/run/docker.sock';Destination='/var/run/docker.sock';RW=$false})}
    }
    $refused=$false;try{Assert-WorkerConsumerMount $binding|Out-Null}catch{$refused=$true}
    Check $refused "CONSUMER_${defect}_REFUSED"
    $script:fakeInspection.Config.User='1654:1654';$script:fakeInspection.HostConfig.CapDrop=@('ALL');$script:fakeInspection.HostConfig.SecurityOpt=@('no-new-privileges=true');$script:fakeInspection.State.Pid=54321
    $script:fakeInspection.Mounts=@($script:fakeInspection.Mounts|Where-Object {$_.Type -eq 'volume'})
}
$synthetic=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$iat=[DateTimeOffset]::FromUnixTimeSeconds([DateTimeOffset]::UtcNow.ToUnixTimeSeconds());$created=$iat.AddMilliseconds(-125)
$package=[Zeka.WorkerCredentials.WorkerCredentialEncoding]::Create([Guid]::NewGuid(),1,[Guid]::NewGuid(),[Guid]::NewGuid(),'adminarea-retry',[Guid[]]@([Guid]::NewGuid()),$iat,$iat.AddHours(1),$created,$created.AddHours(1),$synthetic)
$bytes=[Zeka.WorkerCredentials.WorkerCredentialEncoding]::Serialize($package);$parsed=[Zeka.WorkerCredentials.WorkerCredentialEncoding]::Parse($bytes)
Check ($parsed.Token -ceq $synthetic -and $parsed.SessionCreatedAtUtc -eq $created) 'SHARED_ENCODING_EXACT_FAMILY_AND_TOKEN_INTERVALS'
Check (!(($s.journal|ConvertTo-Json -Depth 12 -Compress).Contains($synthetic))) 'REDACTED_JOURNAL_NEVER_SERIALIZES_TOKEN'
# Disposable test CA remains unrelated to M1 issuance and never enters trust
# stores. Only its PUBLIC certificate is written; its key stays in memory.
$key=[Security.Cryptography.RSA]::Create(2048)
$temporaryPublicCa=Join-Path ([IO.Path]::GetTempPath()) "zeka-worker-pure-ca-$([Guid]::NewGuid().ToString('N')).pem"
try{
    $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Disposable Worker Operator Pure Test',$key,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $certificate=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddMinutes(5))
    try{
        $publicBytes=[Text.Encoding]::ASCII.GetBytes($certificate.ExportCertificatePem())
        [IO.File]::WriteAllBytes($temporaryPublicCa,$publicBytes)
        $clients=@(New-WorkerHttpClient $temporaryPublicCa (Get-WorkerHash $publicBytes) $synthetic)
        Check ($clients.Count -eq 1 -and $clients[0] -is [Net.Http.HttpClient]) 'HTTP_CONSTRUCTOR_RETURNS_ONLY_CLIENT'
        $client=$clients[0]
        try{
            $field=[Net.Http.HttpMessageInvoker].GetField('_handler',[Reflection.BindingFlags]'NonPublic,Instance')
            $handler=$field.GetValue($client)
            Check ($handler -is [Net.Http.SocketsHttpHandler] -and $null -eq $handler.SslOptions.RemoteCertificateValidationCallback) 'PLATFORM_HOSTNAME_VALIDATION_NO_CALLBACK'
            Check ($handler.SslOptions.CertificateChainPolicy.TrustMode -eq [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust -and $handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Count -eq 1) 'ONLY_DISPOSABLE_APPROVED_PUBLIC_CA_TRUSTED'
            Check ($client.Timeout.TotalSeconds -eq 30 -and $client.MaxResponseContentBufferSize -eq 262144 -and !$handler.AllowAutoRedirect) 'ACTUAL_CLIENT_BOUNDED_BODY_AND_REDIRECT_REFUSAL'
        }finally{$client.Dispose()}
    }finally{$certificate.Dispose()}
}finally{
    $key.Dispose()
    # Delete only the exact public disposable file this test created.
    if(Test-Path -LiteralPath $temporaryPublicCa){[IO.File]::Delete($temporaryPublicCa)}
}
# A source-only prior-helper fixture proves bounded-stop admission refuses
# identity, provenance, privilege and mount mismatches before any native stop.
$script:WorkerContext=@{Bindings=@{ownerSid='S-1-5-21-100-200-300-1001';registryId=('a'*32);dockerPrivateRoot='/mnt/c/synthetic-worker-private'}}
$helperLabels=Get-WorkerResourceLabels 'adminarea-retry'
$helperLabels['com.zeka.source']='a'*40;$helperLabels['com.zeka.operation']=[Guid]::NewGuid().ToString('D')
$helperCommand='test -f /input/current.json'
$helperLabels['com.zeka.command.sha256']=Get-WorkerHash ([Text.Encoding]::UTF8.GetBytes($helperCommand))
$priorHelper=@{Id=('e'*64);Image=$script:WorkerHelperImage;Config=@{Labels=$helperLabels;User='1654:1654';Cmd=@('sh','-euc',$helperCommand)};HostConfig=@{NetworkMode='none';ReadonlyRootfs=$true;Privileged=$false;CapDrop=@('ALL');CapAdd=@();SecurityOpt=@('no-new-privileges=true')};Mounts=@(@{Type='volume';Name='synthetic-rotation';Destination='/target';RW=$true},@{Type='bind';Source=('/mnt/c/synthetic-worker-private/workers/operations/'+$helperLabels['com.zeka.operation']+'/adminarea-retry.json');Destination='/input/current.json';RW=$false})}
Assert-WorkerPriorHelper $priorHelper 'adminarea-retry' 'synthetic-rotation' ('b'*40) $true
Check $true 'EXACT_OWNED_PRIOR_HELPER_ADMITTED'
foreach($defect in @('foreign-owner','foreign-profile','foreign-registry','image','network','caps','secret-rw','extra-mount','command','unrelated-source')){
    $bad=$priorHelper|ConvertTo-Json -Depth 10 -Compress|ConvertFrom-WorkerJson
    $ancestor=$true
    switch($defect){
        'foreign-owner' {$bad.Config.Labels['com.zeka.owner.sid']='S-1-5-18'}
        'foreign-profile' {$bad.Config.Labels['com.zeka.worker.profile']='client-projection'}
        'foreign-registry' {$bad.Config.Labels['com.zeka.worker.registry']='b'*32}
        'image' {$bad.Image='sha256:'+('f'*64)}
        'network' {$bad.HostConfig.NetworkMode='host'}
        'caps' {$bad.HostConfig.CapAdd=@('SYS_ADMIN')}
        'secret-rw' {$bad.Mounts[1].RW=$true}
        'extra-mount' {$bad.Mounts+=@(@{Type='volume';Name='zeka-m1-auth-config';Destination='/extra';RW=$true})}
        'command' {$bad.Config.Cmd[2]='changed-command'}
        'unrelated-source' {$ancestor=$false}
    }
    $refused=$false;try{Assert-WorkerPriorHelper $bad 'adminarea-retry' 'synthetic-rotation' ('b'*40) $ancestor}catch{$refused=$true}
    Check $refused "PRIOR_HELPER_$($defect)_REFUSED"
}
$command=Get-WorkerPublishCommand ('a'*64) 8 ([Guid]::NewGuid().ToString('N'))
Check ($command.Contains('mv -T') -and $command.Contains('sync -f /target/private') -and $command.Contains('chmod 0500 /target/private')) 'ATOMIC_CHILD_FSYNC_RESEAL_PLAN'
Check ($command.Contains('test "$current" -lt "8"') -and $command.Contains('test "$actual" = "8"') -and $command.Contains('test "$(stat -c %s /target/private/current.json)" -le 32768')) 'NATIVE_GENERATION_ROLLBACK_AND_SIZE_FENCE_PLAN'
Check ($command.Contains('test "$(stat -c %u:%g /target/private/current.json)" = "1654:1654"') -and $command.Contains('test "$(stat -c %a /target/private/current.json)" = "400"')) 'ACTUAL_DESTINATION_OWNER_MODE_PLAN'
try{Get-WorkerPublishCommand ('a'*64+';') 8 ([Guid]::NewGuid().ToString('N'))|Out-Null;throw 'UNEXPECTED_ACCEPT'}catch{Check ($_.Exception.Message -eq 'PUBLISH_COMMAND_INPUT_REJECTED') 'SHELL_INJECTION_REFUSED'}
try{Get-WorkerPublishCommand ('a'*64) 0 ([Guid]::NewGuid().ToString('N'))|Out-Null;throw 'UNEXPECTED_ACCEPT'}catch{Check ($_.Exception.Message -eq 'PUBLISH_COMMAND_INPUT_REJECTED') 'ZERO_GENERATION_REFUSED'}
Confirm-Source $operator
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($operator,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'OPERATOR_SYNTAX'
$phase=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-WorkerPhase'},$true).Extent.Text
Check ($phase.Contains('HttpCompletionOption]::ResponseContentRead') -and !$phase.Contains('HttpCompletionOption]::ResponseHeadersRead')) 'COMPLETE_BODY_DEADLINE'
$flow=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-WorkerRotationFlow'},$true).Extent.Text
Check (!$flow.Contains('Observe') -and !$flow.Contains('Finalize') -and !$flow.Contains('BeginCutover') -and $flow.Contains('RevokeOthers')) 'NO_USAGE_GATE_OR_RECOVERY_CEREMONY'
Check ($null -eq $ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'ConvertFrom-WorkerAdoptionEvent'},$true)) 'OBSOLETE_PARSER_RETIRED_FROM_OPERATOR'
$native=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-WorkerNative'},$true).Extent.Text
Check ($native.Contains("ArgumentList.Add('--context')") -and $native.Contains("ArgumentList.Add('default')")) 'EXPLICIT_DEFAULT_DOCKER_CONTEXT'
Confirm-Source $overlay
$overlayText=[IO.File]::ReadAllText($overlay)
Check ([regex]::Matches($overlayText,'user: "1654:1654"').Count -eq 2 -and [regex]::Matches($overlayText,'cap_drop: \[ALL\]').Count -eq 2 -and [regex]::Matches($overlayText,'no-new-privileges:true').Count -eq 2) 'BOTH_CONSUMER_UID_CAPS_NNP'
Check ([regex]::Matches($overlayText,'read_only: true').Count -eq 2 -and [regex]::Matches($overlayText,'subpath: private').Count -eq 2 -and !$overlayText.Contains('logging:')) 'DIRECTORY_RO_PRESERVED_LOGGING'

# Request construction and guarded orchestration only. Every HTTP/native/journal
# port is synthetic; runtime/provider/adoption acceptance remains NOT_RUN.
$scalarJson=ConvertFrom-WorkerJson '{"enabled":true,"disabled":false,"ratio":1.25}'
Check ($scalarJson.enabled-eq$true-and$scalarJson.disabled-eq$false-and$scalarJson.ratio-eq[decimal]1.25) 'JSON_BOOLEAN_AND_DECIMAL_TYPES_PRESERVED'
$rawCode=$null;try{throw [InvalidOperationException]::new('synthetic detail not for receipts')}catch{$rawCode=Get-WorkerSafeCode $_}
Check ($rawCode-ceq'OPERATION_FAILED_DETAILS_WITHHELD') 'RAW_NATIVE_DETAIL_NEVER_BECOMES_RECEIPT_CODE'
$emptyGuidRejected=$false;try{New-WorkerJournal ([Guid]::Empty) ([Guid]::NewGuid()) ('a'*40)|Out-Null}catch{$emptyGuidRejected=$_.Exception.Message-ceq'OPERATION_ID_REQUIRED'}
Check $emptyGuidRejected 'EMPTY_OPERATION_REFUSED_BEFORE_ANY_PORT'
$noConsumer=Assert-WorkerConsumerMount @{consumer=$null}
Check ($null-eq$noConsumer) 'MISSING_CONSUMER_IS_NOT_ADOPTION_PROOF'
$badPhase=$false;try{Invoke-WorkerPhase 'unknown' (New-WorkerJournal ([Guid]::NewGuid()) ([Guid]::NewGuid()) ('a'*40))|Out-Null}catch{$badPhase=$_.Exception.Message-ceq'AUTH_PHASE_REJECTED'}
Check $badPhase 'UNKNOWN_AUTH_PHASE_REFUSED_BEFORE_SEND'

function Invoke-PurePhaseCase([string]$Case) {
    $previous=$script:WorkerContext
    $s=New-Scenario;$journal=Invoke-WorkerRotationFlow $s.journal $s.ports
    $body=@{operationId=$journal.operationId;organisationId=$journal.organisationId;state='ISSUED'}
    if($Case-ceq'wrong-identity'){$body.operationId=[Guid]::NewGuid().ToString('D')}
    $response=[Net.Http.HttpResponseMessage]::new($(if($Case-ceq'status'){[Net.HttpStatusCode]::ServiceUnavailable}else{[Net.HttpStatusCode]::OK}))
    $response.Content=[Net.Http.StringContent]::new($(if($Case-ceq'malformed'){'{'}else{$body|ConvertTo-Json -Compress}))
    $state=@{calls=0;request=$null;method='';uri='';completion=$null;payload=$null;response=$response}
    try {
        $client=[pscustomobject]@{state=$state}
        $client|Add-Member -MemberType ScriptMethod -Name Send -Value {
            param($request,$completion)
            $this.state.calls++;$this.state.request=$request;$this.state.method=$request.Method.Method
            $this.state.uri=$request.RequestUri.AbsolutePath;$this.state.completion=$completion
            $this.state.payload=ConvertFrom-WorkerJson ($request.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            return $this.state.response
        }
        $script:WorkerContext=@{Client=$client}
        $phase=if($Case-cin@('revoke','readonly-denied')){'revoke-others'}else{'prepare'}
        if($Case-ceq'readonly-denied'){$journal.publications['client-projection'].readOnlyMount=$false}
        $errorCode=$null;$result=$null
        try{$result=Invoke-WorkerPhase $phase $journal}catch{$errorCode=$_.Exception.Message}
        $requestDisposed=$false;$responseDisposed=$false
        if($state.calls-gt0){
            try{$null=$state.request.Content.ReadAsStringAsync()}catch{$requestDisposed=$true}
            try{$null=$response.Content.ReadAsStringAsync()}catch{$responseDisposed=$true}
        }
        return @{error=$errorCode;result=$result;state=$state;requestDisposed=$requestDisposed;responseDisposed=$responseDisposed;operationId=$journal.operationId;organisationId=$journal.organisationId}
    } finally {$script:WorkerContext=$previous;$response.Dispose()}
}
foreach($phaseCase in @('prepare','revoke')){
    $phaseResult=Invoke-PurePhaseCase $phaseCase
    $expectedPath=if($phaseCase-ceq'revoke'){'/internal/m1/worker-rotation/revoke-others'}else{'/internal/m1/worker-rotation/prepare'}
    Check ($null-eq$phaseResult.error-and$phaseResult.state.calls-eq1-and$phaseResult.state.method-ceq'POST'-and$phaseResult.state.uri-ceq$expectedPath-and$phaseResult.state.completion-eq[Net.Http.HttpCompletionOption]::ResponseContentRead) "PURE_HTTP_$($phaseCase)_EXACT_ROUTE_METHOD_AND_COMPLETE_BODY"
    Check ($phaseResult.state.payload.operationId-ceq$phaseResult.operationId-and$phaseResult.state.payload.organisationId-ceq$phaseResult.organisationId) "PURE_HTTP_$($phaseCase)_EXACT_OPERATION_ORGANISATION"
    Check ($phaseResult.requestDisposed-and$phaseResult.responseDisposed) "PURE_HTTP_$($phaseCase)_REQUEST_AND_RESPONSE_RELEASED"
    if($phaseCase-ceq'revoke'){Check ($phaseResult.state.payload.deliveries.Count-eq2) 'PURE_HTTP_REVOKE_BINDS_BOTH_VERIFIED_DELIVERIES'}
}
foreach($phaseCase in @('status','wrong-identity','malformed','readonly-denied')){
    $phaseResult=Invoke-PurePhaseCase $phaseCase
    $expectedCode=switch($phaseCase){
        'status' {'AUTH_PHASE_REJECTED_OR_UNCERTAIN'}
        'wrong-identity' {'AUTH_RESPONSE_IDENTITY_REJECTED'}
        'malformed' {'OPERATION_FAILED_DETAILS_WITHHELD'}
        'readonly-denied' {'ACTUAL_CONSUMER_RO_SCOPE_REQUIRED'}
    }
    Check ($phaseResult.error-ceq$expectedCode) "PURE_HTTP_$($phaseCase)_REFUSED"
    if($phaseCase-ceq'readonly-denied'){Check ($phaseResult.state.calls-eq0) 'PURE_HTTP_UNPROVEN_RO_MOUNT_NEVER_SENDS_REVOCATION'}
    else{Check ($phaseResult.requestDisposed-and$phaseResult.responseDisposed) "PURE_HTTP_$($phaseCase)_FAILURE_RELEASES_MESSAGES"}
}

function Invoke-PureOwnedHelperCase([string]$Case,[switch]$Initialize) {
    $previous=$script:WorkerContext
    $journal=New-WorkerJournal ([Guid]::NewGuid()) ([Guid]::NewGuid()) ('a'*40)
    $profile='adminarea-retry';$volume='synthetic-rotation';$id='e'*64
    $command=if($Initialize){'test "$(find /target -mindepth 1 -maxdepth 1 | wc -l)" -eq 0; chown 1654:1654 /target'}else{'test -f /input/current.json'}
    $private=Join-Path ([IO.Path]::GetTempPath()) 'synthetic-worker-no-files'
    $relative='workers/operations/'+$journal.operationId+'/'+$profile+'.json'
    $journal.stages[$profile]=@{path=(Join-Path $private $relative)}
    $state=@{calls=[Collections.Generic.List[string]]::new();arguments=@();inspections=0;journalIds=[Collections.Generic.List[object]]::new();case=$Case}
    try {
        $script:WorkerContext=@{Private=$private;JournalPath='synthetic-journal-not-written';Bindings=@{ownerSid='S-1-5-21-100-200-300-1001';registryId=('a'*32);dockerPrivateRoot='/synthetic/private'}}
        $labels=Get-WorkerResourceLabels $profile;$labels['com.zeka.operation']=$journal.operationId;$labels['com.zeka.source']=$journal.sourceCommit
        $labels['com.zeka.command.sha256']=Get-WorkerHash ([Text.Encoding]::UTF8.GetBytes($command))
        $inspection=@{Id=$id;Image=$script:WorkerHelperImage;Config=@{Labels=$labels;User=$(if($Initialize){'0:0'}else{'1654:1654'});Cmd=@('sh','-euc',$command)};
            HostConfig=@{NetworkMode='none';ReadonlyRootfs=$true;CapDrop=@('ALL');CapAdd=@();SecurityOpt=@('no-new-privileges=true')};
            Mounts=@(@{Type='volume';Name=$volume;Destination='/target';RW=$true});State=@{Running=$false;ExitCode=0}}
        if($Initialize){$inspection.HostConfig.CapAdd=@('CHOWN')}
        if(!$Initialize){$inspection.Mounts+=@(@{Type='bind';Source=('/synthetic/private/'+$relative);Destination='/input/current.json';RW=$false})}
        switch($Case){
            'sandbox' {$inspection.HostConfig.NetworkMode='host'}
            'user' {$inspection.Config.User='unexpected'}
            'caps' {$inspection.HostConfig.CapAdd=@('SYS_ADMIN')}
            'target' {$inspection.Mounts[0].Name='unrelated-volume'}
            'secret' {$inspection.Mounts[1].RW=$true}
            'exit' {$inspection.State.Running=$true}
        }
        function Invoke-WorkerNative {
            param([string[]]$Arguments,[double]$TimeoutSeconds=30,[switch]$AllowFailure)
            $operation=$Arguments[0];$state.calls.Add($operation)
            switch($operation){
                'ps' {return @{stdout=$(if($state.case-ceq'retained'){$id}else{''})}}
                'create' {$state.arguments=$Arguments;return @{stdout=$id}}
                'start' {return @{stdout=''}}
                'wait' {return @{stdout=$(if($state.case-ceq'wait'){'17'}else{'0'})}}
                'rm' {if($state.case-ceq'cleanup'){throw 'SYNTHETIC_HELPER_CLEANUP_FAILED'};return @{stdout=$id}}
                default {throw 'UNEXPECTED_NATIVE_OPERATION'}
            }
        }
        function Get-WorkerDockerJson {
            param([string[]]$Arguments,[double]$TimeoutSeconds=30)
            if($Arguments.Count-ne2-or$Arguments[0]-cne'inspect'-or$Arguments[1]-cne$id){throw 'UNEXPECTED_INSPECTION_ARGUMENTS'}
            $state.inspections++;return $inspection
        }
        function Write-WorkerJournal {
            param([string]$Path,[System.Collections.IDictionary]$Journal)
            if($Path-cne'synthetic-journal-not-written'){throw 'UNEXPECTED_JOURNAL_PATH'}
            $state.journalIds.Add($Journal.retainedHelperId)
        }
        $failure=$null
        try{Invoke-WorkerOwnedHelper $journal $profile $volume $command -Initialize:$Initialize}catch{$failure=$_.Exception.Message}
        return @{error=$failure;state=$state;journal=$journal;id=$id}
    } finally {$script:WorkerContext=$previous}
}
foreach($initializer in @($false,$true)){
    $helperResult=Invoke-PureOwnedHelperCase 'success' -Initialize:$initializer
    Check ($null-eq$helperResult.error-and($helperResult.state.calls-join',')-ceq'ps,create,start,wait,rm'-and$null-eq$helperResult.journal.retainedHelperId) "PURE_HELPER_$($initializer)_BOUNDED_SUCCESS_REMOVES_ONLY_EXACT_FAKE_ID"
    Check ($helperResult.state.journalIds.Count-eq2-and$helperResult.state.journalIds[0]-ceq$helperResult.id-and$null-eq$helperResult.state.journalIds[1]) "PURE_HELPER_$($initializer)_RETENTION_IS_CLEARED_ONLY_AFTER_CLEANUP"
    $helperArguments=$helperResult.state.arguments
    Check ($helperArguments-contains'none'-and$helperArguments-contains'--read-only'-and$helperArguments-contains'ALL'-and$helperArguments-contains'no-new-privileges=true'-and$helperArguments-contains'--log-driver') "PURE_HELPER_$($initializer)_ACTUAL_ARGUMENTS_KEEP_ISOLATION"
    if($initializer){Check ($helperArguments-contains'0:0'-and$helperArguments-contains'CHOWN') 'PURE_INITIALIZER_EXPLICIT_NARROW_CAPABILITY'}
    else{Check ($helperArguments-contains'1654:1654'-and@($helperArguments|Where-Object {$_-like'type=bind,*readonly'}).Count-eq1) 'PURE_PUBLISHER_READONLY_INPUT_AND_UNPRIVILEGED_UID'}
}
$codes=@{retained='RETAINED_HELPER_REQUIRES_INSPECTION';sandbox='HELPER_ADMISSION_REJECTED';user='HELPER_USER_REJECTED';caps='HELPER_CAPABILITY_REJECTED';target='HELPER_TARGET_MOUNT_REJECTED';secret='HELPER_SECRET_MOUNT_REJECTED';wait='HELPER_FAILED_PRESERVED';exit='HELPER_EXIT_UNCERTAIN';cleanup='SYNTHETIC_HELPER_CLEANUP_FAILED'}
foreach($defect in $codes.Keys){
    $helperResult=Invoke-PureOwnedHelperCase $defect
    Check ($helperResult.error-ceq$codes[$defect]) "PURE_HELPER_$($defect)_REFUSED"
    if($defect-ceq'retained'){Check (($helperResult.state.calls-join',')-ceq'ps') 'PURE_RETAINED_HELPER_NEVER_CREATES_OR_STARTS'}
    else{
        Check ($helperResult.journal.retainedHelperId-ceq$helperResult.id) "PURE_HELPER_$($defect)_RETAINS_INSPECTION_ID"
        $removals=@($helperResult.state.calls|Where-Object {$_-ceq'rm'}).Count
        Check ($removals-eq$(if($defect-ceq'cleanup'){1}else{0})) "PURE_HELPER_$($defect)_NO_RETRY_OR_UNAPPROVED_CLEANUP"
    }
}

@{tests=$script:checks;passed=$script:checks;failed=0;runtime='NOT_RUN';provider='NOT_RUN';adoption='NOT_RUN';historicalObservability=@{case='EXACT_ACTUAL_USE_EVENT';original='FAIL_UNCLASSIFIED';retiredFrom='REVOCATION_AND_OPERATOR_LOG_PARSER';c1Closure='INDEPENDENT_EXPLANATION_OR_EXPLICIT_REVIEWED_RETIREMENT_REQUIRED'};secretMaterial='GENERATED_IN_MEMORY_ONLY'}|ConvertTo-Json -Depth 5 -Compress
