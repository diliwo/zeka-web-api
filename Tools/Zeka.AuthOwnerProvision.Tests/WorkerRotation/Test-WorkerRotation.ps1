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
@{tests=$script:checks;passed=$script:checks;failed=0;runtime='NOT_RUN';provider='NOT_RUN';adoption='NOT_RUN';historicalObservability=@{case='EXACT_ACTUAL_USE_EVENT';original='FAIL_UNCLASSIFIED';retiredFrom='REVOCATION_AND_OPERATOR_LOG_PARSER';c1Closure='INDEPENDENT_EXPLANATION_OR_EXPLICIT_REVIEWED_RETIREMENT_REQUIRED'};secretMaterial='GENERATED_IN_MEMORY_ONLY'}|ConvertTo-Json -Depth 5 -Compress
