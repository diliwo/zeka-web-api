#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$operator=Join-Path $root 'Deployments\local-stack\Invoke-WorkerCredentialRotation.ps1'
$contract=Join-Path $root 'Shared\WorkerCredentials\WorkerCredentialPackage.cs'
$overlay=Join-Path $root 'Deployments\local-stack\compose.worker-rotation.yaml'
foreach($path in @($operator,$contract,$overlay)){
    $scan=& sonar analyze secrets $path 2>&1
    if($LASTEXITCODE -ne 0 -or ($scan -join [Environment]::NewLine) -notmatch 'No secrets found' -or ($scan -join [Environment]::NewLine) -match 'warning'){throw 'SOURCE_SCAN_REJECTED'}
}
. $operator -DefinitionsOnly
Add-Type -Path $contract
$script:checks=0
function Check([bool]$Value,[string]$Name){$script:checks++;if(!$Value){throw "PURE_ASSERTION_FAILED_$Name"}}
function New-Scenario([string]$Failure='', [string]$Observation='ADOPTED'){
    $state=@{calls=[Collections.Generic.List[string]]::new();failure=$Failure;observation=$Observation;inspection='CUTOVER_STARTED';saved=@()}
    $ports=@{
        Save={param($j) $state.calls.Add('save');$state.saved+=@(($j|ConvertTo-Json -Depth 12 -Compress))}.GetNewClosure()
        Prepare={param($j) $state.calls.Add('prepare');if($state.failure -eq 'prepare'){throw 'SECOND_IDENTITY_ISSUE_FAILED'};@{state='PREPARED'}}.GetNewClosure()
        Stage={param($j,$p) $state.calls.Add('stage');if($state.failure -eq 'stage'){throw 'SECOND_STAGE_FAILED'};@{'adminarea-retry'=@{generation=1};'client-projection'=@{generation=1}}}.GetNewClosure()
        VerifyStage={param($j) $state.calls.Add('verify-stage');if($state.failure -eq 'verify'){throw 'STAGED_PAIR_RECOVERY_UNAVAILABLE'}}.GetNewClosure()
        BeginCutover={param($j) $state.calls.Add('begin');if($state.failure -eq 'begin'){throw 'BEGIN_CUTOVER_UNCERTAIN'};@{state='CUTOVER_STARTED'}}.GetNewClosure()
        Publish={param($j,$profile) $state.calls.Add("publish/$profile");if($state.failure -eq "publish/$profile"){throw 'CONTROLLED_PUBLISH_FAILED'};@{profile=$profile;verified=$true}}.GetNewClosure()
        Observe={param($j) $state.calls.Add('observe');$w=@{};if($state.observation -eq 'ADOPTED'){$w['adminarea-retry']=@{};$w['client-projection']=@{}};@{state=$state.observation;witnesses=$w}}.GetNewClosure()
        Finalize={param($j) $state.calls.Add('finalize');if($state.failure -eq 'finalize'){throw 'FINALIZE_UNCERTAIN'};@{state='FINALIZED'}}.GetNewClosure()
        Inspect={param($j) $state.calls.Add('inspect');@{state=$state.inspection}}.GetNewClosure()
        Abort={param($j) $state.calls.Add('abort');@{state='ABORTED'}}.GetNewClosure()
    }
    @{state=$state;ports=$ports;journal=(New-WorkerJournal ([Guid]::NewGuid()) ([Guid]::NewGuid()) ('a'*40))}
}
$s=New-Scenario;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
Check ($r.state -eq 'FINALIZED') 'PAIR_SUCCESS'
Check ($s.state.calls.IndexOf('begin') -lt $s.state.calls.IndexOf('publish/adminarea-retry')) 'BEGIN_BEFORE_FIRST_PUBLISH'
Check ($s.state.calls.IndexOf('publish/client-projection') -lt $s.state.calls.IndexOf('finalize')) 'BOTH_BEFORE_FINALIZE'
Check ($r.history[0].state -eq 'PREPARE_SUBMITTED') 'DURABLE_PREPARE_INTENT'
foreach($failure in @('prepare','stage')){
    $s=New-Scenario $failure;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
    Check ($r.state -eq 'RECOVERY_REQUIRED') "${failure}_FAIL_CLOSED"
    Check (!$s.state.calls.Contains('begin') -and !$s.state.calls.Contains('finalize')) "${failure}_NO_CUTOVER_OR_FINALIZE"
}
$s=New-Scenario 'begin';$r=Invoke-WorkerRotationFlow $s.journal $s.ports
Check ($r.state -eq 'DEGRADED_RECOVERY_REQUIRED') 'UNCERTAIN_BEGIN_EXPLICIT'
Check (!$s.state.calls.Contains('publish/adminarea-retry') -and !$s.state.calls.Contains('abort')) 'UNCERTAIN_BEGIN_NO_PUBLISH_OR_ABORT'
$s=New-Scenario 'publish/client-projection';$r=Invoke-WorkerRotationFlow $s.journal $s.ports
Check ($r.state -eq 'DEGRADED_RECOVERY_REQUIRED' -and $r.publications.ContainsKey('adminarea-retry')) 'PARTIAL_DELIVERY_PRESERVED'
Check (!$s.state.calls.Contains('finalize') -and !$s.state.calls.Contains('abort')) 'PARTIAL_DELIVERY_NO_REVOKE_OR_ABORT'
$s.state.failure='';$r=Invoke-WorkerRotationFlow $r $s.ports -ResumeOperation
Check ($r.state -eq 'FINALIZED') 'SAME_OPERATION_RECOVERED'
Check (@($s.state.calls|Where-Object {$_ -eq 'prepare'}).Count -eq 1) 'RECOVERY_NEVER_REMINTS'
foreach($status in @('ISSUED_DELIVERED_PENDING_FIRST_USE','DELIVERED_PENDING_ADOPTION')){
    $s=New-Scenario '' $status;$r=Invoke-WorkerRotationFlow $s.journal $s.ports
    Check ($r.state -eq $status -and $r.publications.Count -eq 2) "${status}_EXPLICIT"
    Check (!$s.state.calls.Contains('finalize')) "${status}_NO_FINALIZE"
}
$s=New-Scenario 'finalize';$r=Invoke-WorkerRotationFlow $s.journal $s.ports
Check ($r.state -eq 'FINALIZE_UNCERTAIN') 'UNCERTAIN_FINALIZE_PRESERVED'
$s.state.inspection='FINALIZED';$count=$s.state.calls.Count;$r=Invoke-WorkerRotationFlow $r $s.ports -ResumeOperation
Check ($r.state -eq 'FINALIZED' -and @($s.state.calls|Select-Object -Skip $count|Where-Object {$_ -like 'publish/*'}).Count -eq 0) 'FINALIZE_RECOVERY_INSPECT_ONLY'
$s=New-Scenario;$s.journal.state='PREPARED';$s.state.inspection='PREPARED'
$r=Invoke-WorkerRotationFlow $s.journal $s.ports -Abort
Check ($r.state -eq 'ABORTED' -and $s.state.calls.Contains('abort')) 'PREPARED_ABORT_ONLY'
$s=New-Scenario;$s.journal.cutoverStarted=$true;$s.journal.state='CUTOVER_STARTED'
$r=Invoke-WorkerRotationFlow $s.journal $s.ports -Abort
Check (!$s.state.calls.Contains('abort') -and $r.state -eq 'DEGRADED_RECOVERY_REQUIRED') 'POSTCUTOVER_ABORT_REFUSED'
$s=New-Scenario;$s.journal.state='PREPARE_SUBMITTED';$s.state.failure='verify'
$r=Invoke-WorkerRotationFlow $s.journal $s.ports -ResumeOperation
Check (!$s.state.calls.Contains('prepare') -and !$s.state.calls.Contains('publish/adminarea-retry')) 'LOST_SECRET_NO_BLIND_REPREPARE'
# Preserve the first worker's once-per-generation event across partial delivery.
$s=New-Scenario;$s.journal.stages=@{'adminarea-retry'=@{}};$save={param($j)}
$first=Get-WorkerPublicationLowerBound $s.journal 'adminarea-retry' $save
$again=Get-WorkerPublicationLowerBound $s.journal 'adminarea-retry' $save
Check ($first -ceq $again) 'RECOVERY_PRESERVES_ORIGINAL_ADOPTION_LOWER_BOUND'
# No native command is invoked: this port exposes only a synthetic Docker
# inspection with deliberately different HOST and container process identities.
$consumer=@{containerId=('c'*64);imageId=('sha256:'+('d'*64));hostPid=54321;startedAt='2026-11-02T12:00:00Z';processId=1;processInstanceId=[Guid]::NewGuid();labels=@{'com.docker.compose.project'='zeka-m1';'com.docker.compose.service'='adminarea'};mounts=@(@{type='volume';name='synthetic-rotation';source='/var/lib/docker/volumes/synthetic/_data/private';destination='/run/zeka-worker-credential';readOnly=$true})}
$binding=@{consumer=$consumer;volumeName='synthetic-rotation'}
$script:fakeInspection=@{Id=$consumer.containerId;Image=$consumer.imageId;State=@{Running=$true;Pid=54321;StartedAt=$consumer.startedAt};Config=@{User='1654:1654';Labels=$consumer.labels};HostConfig=@{CapDrop=@('ALL');CapAdd=@();SecurityOpt=@('no-new-privileges=true');LogConfig=@{Type='none'}};Mounts=@(@{Type='volume';Name='synthetic-rotation';Source='/var/lib/docker/volumes/synthetic/_data/private';Destination='/run/zeka-worker-credential';RW=$false})}
function Get-WorkerDockerJson([string[]]$Arguments,[double]$TimeoutSeconds=30){return $script:fakeInspection}
Check ((Assert-WorkerConsumer $binding).hostPid -eq 54321) 'HOST_PID_NOT_CONFUSED_WITH_CONTAINER_PID'
foreach($defect in @('uid','caps','nnp','extra-mount','pid')){
    switch($defect){
        'uid' {$script:fakeInspection.Config.User='0:0'}
        'caps' {$script:fakeInspection.HostConfig.CapDrop=@()}
        'nnp' {$script:fakeInspection.HostConfig.SecurityOpt=@()}
        'extra-mount' {$script:fakeInspection.Mounts+=@(@{Type='bind';Source='/var/run/docker.sock';Destination='/var/run/docker.sock';RW=$false})}
        'pid' {$script:fakeInspection.State.Pid=54322}
    }
    $refused=$false;try{Assert-WorkerConsumer $binding|Out-Null}catch{$refused=$true}
    Check $refused "CONSUMER_${defect}_REFUSED"
    $script:fakeInspection.Config.User='1654:1654';$script:fakeInspection.HostConfig.CapDrop=@('ALL');$script:fakeInspection.HostConfig.SecurityOpt=@('no-new-privileges=true');$script:fakeInspection.State.Pid=54321
    $script:fakeInspection.Mounts=@($script:fakeInspection.Mounts|Where-Object {$_.Type -eq 'volume'})
}
$expected=@{profile='adminarea-retry';operationId=[Guid]::NewGuid();generation=7;processId=1;processInstanceId=[Guid]::NewGuid();notBeforeUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1)}
$event=@{event='zeka-worker-credential-adopted';profile=$expected.profile;operationId=$expected.operationId;generation=7;processId=1;processInstanceId=$expected.processInstanceId;utc=[DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O')}
$line=$event|ConvertTo-Json -Compress
Check ($null -ne (ConvertFrom-WorkerAdoptionEvent $line $expected)) 'EXACT_ACTUAL_USE_EVENT'
Check ($null -ne (ConvertFrom-WorkerAdoptionEvent (@{Message=$line}|ConvertTo-Json -Compress) $expected)) 'JSON_CONSOLE_WRAPPER'
$event.generation=8
Check ($null -eq (ConvertFrom-WorkerAdoptionEvent ($event|ConvertTo-Json -Compress) $expected)) 'WRONG_GENERATION_REFUSED'
$event.generation=7;$event.processInstanceId=[Guid]::NewGuid()
Check ($null -eq (ConvertFrom-WorkerAdoptionEvent ($event|ConvertTo-Json -Compress) $expected)) 'DIFFERENT_PROCESS_INSTANCE_REFUSED'
$event.processInstanceId=$expected.processInstanceId;$event.secret='must-never-be-persisted'
Check ($null -eq (ConvertFrom-WorkerAdoptionEvent ($event|ConvertTo-Json -Compress) $expected)) 'UNEXPECTED_LOG_FIELD_REFUSED'
Check ($null -eq (ConvertFrom-WorkerAdoptionEvent ('arbitrary '+$line) $expected)) 'ARBITRARY_LOG_SUBSTRING_REFUSED'
$command=Get-WorkerPublishCommand ('a'*64) ('b'*64) ([Guid]::NewGuid().ToString('N'))
Check ($command.Contains('mv -T') -and $command.Contains('sync -f /target/private') -and $command.Contains('chmod 0500 /target/private')) 'ATOMIC_CHILD_FSYNC_RESEAL_PLAN'
try{Get-WorkerPublishCommand ('a'*64+';') '' ([Guid]::NewGuid().ToString('N'))|Out-Null;throw 'UNEXPECTED_ACCEPT'}catch{Check ($_.Exception.Message -eq 'PUBLISH_COMMAND_INPUT_REJECTED') 'SHELL_ARGUMENT_INJECTION_REFUSED'}
try{Get-WorkerRemainingBudget ([DateTimeOffset]::UtcNow.AddSeconds(-1))|Out-Null;throw 'UNEXPECTED_ACCEPT'}catch{Check ($_.Exception.Message -eq 'ADOPTION_DEADLINE_REACHED') 'EXPIRED_OBSERVATION_BUDGET_REFUSED'}
# Synthetic material is generated in memory, never committed as a password/JWT.
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
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($operator,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'OPERATOR_SYNTAX'
$phase=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-WorkerPhase'},$true).Extent.Text
Check ($phase.Contains('HttpCompletionOption]::ResponseContentRead') -and !$phase.Contains('HttpCompletionOption]::ResponseHeadersRead')) 'WHOLE_RESPONSE_DEADLINE_NOT_HEADERS_ONLY'
$http=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-WorkerHttpClient'},$true).Extent.Text
Check ($http.Contains('MaxResponseContentBufferSize=262144') -and $http.Contains('CertificateChainPolicy=$policy') -and !$http.Contains('RemoteCertificateValidationCallback=')) 'BOUNDED_BODY_STRICT_CA_NO_CALLBACK_BYPASS'
$native=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-WorkerNative'},$true).Extent.Text
Check ($native.Contains("ArgumentList.Add('--context')") -and $native.Contains("ArgumentList.Add('default')")) 'EXPLICIT_DEFAULT_DOCKER_CONTEXT'
$overlayText=[IO.File]::ReadAllText($overlay)
Check ([regex]::Matches($overlayText,'user: "1654:1654"').Count -eq 2 -and [regex]::Matches($overlayText,'cap_drop: \[ALL\]').Count -eq 2 -and [regex]::Matches($overlayText,'no-new-privileges:true').Count -eq 2) 'BOTH_CONSUMER_UID_CAPS_NNP'
Check ([regex]::Matches($overlayText,'read_only: true').Count -eq 2 -and [regex]::Matches($overlayText,'subpath: private').Count -eq 2 -and !$overlayText.Contains('logging:')) 'DIRECTORY_RO_KEEP_LOGGING_GATE'
@{tests=$script:checks;passed=$script:checks;failed=0;runtime='NOT_RUN';provider='NOT_RUN';secretMaterial='GENERATED_IN_MEMORY_ONLY'}|ConvertTo-Json -Compress
