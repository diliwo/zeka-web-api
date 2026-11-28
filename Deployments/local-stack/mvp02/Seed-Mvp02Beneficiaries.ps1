#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StateRoot,
    [ValidatePattern('^[A-Za-z0-9_-]{1,48}$')][string]$DatabaseDirectory = 'database',
    [switch]$Enroll,
    [switch]$ResumeAfterOwnerAVerification,
    [switch]$PreflightOnly,
    [string]$RevocationWitnessContainer
)

$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathFullyQualified($StateRoot)) { throw 'Pass the absolute owned MVP02 fixture state root' }
if (($Enroll -and ($PreflightOnly -or $RevocationWitnessContainer)) -or
    ($PreflightOnly -and $RevocationWitnessContainer) -or ($ResumeAfterOwnerAVerification -and !$Enroll)) {
    throw 'Choose a single fixture operation'
}
$root = [IO.Path]::GetFullPath($StateRoot).TrimEnd('\')
if ($root -notmatch '^[A-Za-z]:\\') { throw 'MVP02 fixtures require a local Windows drive' }
$project = Split-Path -Leaf $root
if ($project -cnotmatch '^zeka-mvp02-[a-f0-9]{8}$') { throw 'MVP02 fixture name rejected' }
$fixtureId = $project.Substring('zeka-mvp02-'.Length)
$pgName = $project + '-postgres-1'
$fixtureMarker = 'mvp02-fixture-' + $fixtureId
$ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$allowedSids = @($ownerSid, 'S-1-5-18', 'S-1-5-32-544')
$stage = 'target'

function Assert-PrivatePath([string]$Path, [switch]$Directory, [switch]$Protected) {
    $item = Get-Item -LiteralPath $Path
    $absolute = $item.FullName
    if ($absolute -cne $root -and !$absolute.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Path outside fixture root'
    }
    if ($item.PSIsContainer -ne [bool]$Directory -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Fixture input path type unexpected'
    }
    $acl = Get-Acl -LiteralPath $absolute
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $ownerSid -or
        ($Protected -and !$acl.AreAccessRulesProtected)) {
        throw 'Fixture input owner or ACL unexpected'
    }
    if (@($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
        $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin $allowedSids
    }).Count -ne 0) {
        throw 'Fixture input has an unexpected access rule'
    }
}

function Assert-DatabaseTarget {
    if ((& docker context show) -cne 'default' -or $LASTEXITCODE -ne 0) {
        throw 'Docker context mismatch'
    }
    if ((& docker --context default context inspect default --format '{{.Endpoints.docker.Host}}') -cne
        'npipe:////./pipe/docker_engine' -or $LASTEXITCODE -ne 0) {
        throw 'Docker named pipe mismatch'
    }
    if ((& docker --context default info --format '{{.ID}}') -cne $expectedEngine -or $LASTEXITCODE -ne 0) {
        throw 'Docker engine mismatch'
    }
    $network = & docker --context default network inspect --format '{{.Id}} {{.Internal}} {{index .Labels "com.diliwo.zeka.mvp02"}}' ($project+'_internal')
    if ($LASTEXITCODE -ne 0 -or $network -cne "$expectedBridge true $fixtureId") { throw 'Internal bridge identity mismatch' }
    $format = '{{.Id}} {{index .Config.Labels "com.docker.compose.project"}} {{index .Config.Labels "com.docker.compose.service"}} {{.State.Health.Status}} {{len .NetworkSettings.Networks}} {{range .NetworkSettings.Networks}}{{.NetworkID}}{{end}} {{len .HostConfig.PortBindings}}'
    $summary = & docker --context default inspect --format $format $pgName
    $identity = '^' + $expectedPostgresShort + '[0-9a-f]{52} ' + [regex]::Escape($project) +
        ' postgres healthy 1 ' + $expectedBridge + ' 0$'
    if ($LASTEXITCODE -ne 0 -or $summary -notmatch $identity) { throw 'PostgreSQL container mismatch' }
    return ($summary -split ' ')[0]
}

function Read-RoleConfiguration([string]$Service, [string]$Key, [string]$Db, [string]$Role) {
    $delivery = Join-Path $root $DatabaseDirectory
    $serviceRoot = Join-Path $delivery $Service
    $path = Join-Path $serviceRoot 'database.LocalStack.json'
    Assert-PrivatePath $delivery -Directory
    Assert-PrivatePath $serviceRoot -Directory
    Assert-PrivatePath $path
    $settings = ConvertFrom-Json -AsHashtable -InputObject ([IO.File]::ReadAllText($path))
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    $connection.psbase.ConnectionString = [string]$settings.ConnectionStrings[$Key]
    if ($connection['Host'] -cne 'postgres' -or [int]$connection['Port'] -ne 5432 -or
        $connection['Database'] -cne $Db -or $connection['Username'] -cne $Role -or
        -not [string]::Equals([string]$connection['Pooling'], 'false', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$connection['Include Error Detail'], 'false', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime delivery identity mismatch'
    }
    $value = [string]$connection['Password']
    if ($value.Length -lt 16 -or $value.Length -gt 512 -or $value -match '[\x00\r\n]') {
        throw 'Invalid runtime credential shape'
    }
    return $value
}

function Invoke-RoleSql([string]$Db, [string]$Role, [string]$Credential, [string]$Sql, [switch]$ReadOnly) {
    $containerId = Assert-DatabaseTarget
    $guard = @'
DO $guard$
BEGIN
    IF current_database() <> '__DATABASE__' OR session_user <> '__ROLE__' OR current_user <> '__ROLE__'
       OR inet_server_addr() <> inet '127.0.0.1'
       OR inet_server_port() <> 5432 OR current_setting('server_version_num')::int NOT BETWEEN 170000 AND 179999
       OR NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = current_user AND rolcanlogin
           AND NOT (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication)) THEN
        RAISE SQLSTATE 'P5201' USING MESSAGE = 'Fixture runtime identity rejected';
    END IF;
END
$guard$;
'@
    $guard = $guard.Replace('__DATABASE__', $Db).Replace('__ROLE__', $Role)
    $begin = if ($ReadOnly) { 'BEGIN READ ONLY;' } else { 'BEGIN;' }
    $launcher = 'IFS= read -r PGPASSWORD; export PGPASSWORD; exec psql -X -w -h 127.0.0.1 -p 5432 -U "$1" -d "$2" -Atq -v ON_ERROR_STOP=1 -v VERBOSITY=sqlstate'
    # Send role credentials over local stdin without putting them in Docker command arguments.
    $stdin = $Credential + [char]10 + $begin + [char]10 + $guard + [char]10 + $Sql + [char]10 + 'COMMIT;' + [char]10
    try {
        $output = $stdin | & docker --context default exec -i $containerId sh -eu -c $launcher mvp02 $Role $Db 2>&1
        $native = $LASTEXITCODE
        if ($native -ne 0) {
            $codes = @([regex]::Matches(($output | Out-String), '(?m)^(?:ERROR|FATAL|PANIC):\s*([0-9A-Z]{5})\s*$') |
                ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
            throw ('PostgreSQL execution failed ' + $native + ' (' + ($codes -join ',') + ')')
        }
        $result = @($output | Where-Object { $_ -is [string] -and $_.StartsWith('{') })
        if ($result.Count -ne 1) { throw 'Unexpected PostgreSQL result shape' }
        return ConvertFrom-Json -AsHashtable -InputObject $result[0]
    }
    finally {
        $stdin = $null
        $Credential = $null
        $output = $null
    }
}

function Confirm-Guid([string]$Value) {
    $id = [guid]::Empty
    if (![guid]::TryParseExact($Value, 'D', [ref]$id) -or $id -eq [guid]::Empty) { throw 'Invalid fixture GUID' }
    return $id.ToString('D')
}


function Assert-ControlContainer([string]$ContainerId) {
    if ($ContainerId -cnotmatch '^[0-9a-f]{64}$') { throw 'Browser control requires a full container ID' }
    $null = Assert-DatabaseTarget
    $format = '{{.Id}} {{.State.Running}} {{len .NetworkSettings.Networks}} {{range .NetworkSettings.Networks}}{{.NetworkID}}{{end}} {{index .Config.Labels "zeka.project"}} {{index .Config.Labels "zeka.role"}} {{index .Config.Labels "zeka.task"}}'
    $ownership = & docker --context default inspect --format $format $ContainerId
    $expected = $ContainerId + ' true 1 ' + $expectedEdgeBridge + ' ' +
        $project + ' browser-witness ZEK-52'
    if ($LASTEXITCODE -ne 0 -or $ownership -cne $expected) {
        throw 'Browser control container is outside the admitted task edge bridge'
    }
}

function Wait-ControlMarker([string]$ContainerId, [string]$Marker, [int]$Seconds) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        $null = & docker --context default exec $ContainerId test -f ('/tmp/zeka-browser-control/' + $Marker) 2>&1
        if ($LASTEXITCODE -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Set-ControlMarker([string]$ContainerId, [string]$Marker) {
    Assert-ControlContainer $ContainerId
    $markerPath = '/tmp/zeka-browser-control/' + $Marker
    $writeMarker = 'set -C; printf "ok\n" > "$1"'
    $null = & docker --context default exec $ContainerId sh -eu -c $writeMarker mvp02 $markerPath 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Browser control marker update failed' }
}

function Invoke-AssignedRevocationWitness([string]$ContainerId, [string]$Org, [string]$Subject,
    [string]$Membership, [string]$Db, [string]$Role, [string]$Credential) {
    Assert-ControlContainer $ContainerId
    if (!(Wait-ControlMarker $ContainerId 'membership-revocation-ready' 120)) {
        throw 'Assigned browser prerequisite timed out before membership mutation'
    }

    $target = @'
"Id" = '__MEMBERSHIP__'::uuid AND "OrganisationId" = '__ORG__'::uuid AND "UserId" = '__SUBJECT__'::uuid
AND "PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a04'::uuid AND "EndedAtUtc" IS NULL
'@
    $target = $target.Replace('__MEMBERSHIP__',$Membership).Replace('__ORG__',$Org).Replace('__SUBJECT__',$Subject)
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ', [Globalization.CultureInfo]::InvariantCulture)
    $observe = @'
SELECT json_build_object('status',"Status",'version',"ConcurrencyVersion",
    'ownedSuspension',"SuspendedAtUtc" IS NOT DISTINCT FROM '__STAMP__'::timestamptz,
    'unsuspended',"SuspendedAtUtc" IS NULL)
FROM public."OrganisationMemberships" WHERE __TARGET__;
'@
    $observe = $observe.Replace('__STAMP__',$stamp).Replace('__TARGET__',$target)
    $before = Invoke-RoleSql $Db $Role $Credential $observe -ReadOnly
    if ($before.status -ne 1 -or $before.version -lt 1 -or !$before.unsuspended) {
        throw 'Expected initial active synthetic membership'
    }
    $beforeVersion = [long]$before.version
    $suspendedVersion = $beforeVersion + 1
    $suspend = @'
WITH change AS (
    UPDATE public."OrganisationMemberships" SET "Status"=2,
        "SuspendedAtUtc"='__STAMP__'::timestamptz,"ConcurrencyVersion"="ConcurrencyVersion"+1
    WHERE __TARGET__ AND "Status"=1 AND "SuspendedAtUtc" IS NULL AND "ConcurrencyVersion"=__VERSION__
        AND "JoinedAtUtc" <= '__STAMP__'::timestamptz
    RETURNING "Status","ConcurrencyVersion"
)
SELECT json_build_object('status',"Status",'version',"ConcurrencyVersion") FROM change;
'@
    $suspend = $suspend.Replace('__STAMP__',$stamp).Replace('__TARGET__',$target).Replace('__VERSION__',[string]$beforeVersion)
    $restore = @'
WITH change AS (
    UPDATE public."OrganisationMemberships" SET "Status"=1,
        "SuspendedAtUtc"=NULL,"ConcurrencyVersion"="ConcurrencyVersion"+1
    WHERE __TARGET__ AND "Status"=2 AND "SuspendedAtUtc"='__STAMP__'::timestamptz AND "ConcurrencyVersion"=__VERSION__
    RETURNING "Status","ConcurrencyVersion"
)
SELECT json_build_object('status',"Status",'version',"ConcurrencyVersion") FROM change;
'@
    $restore = $restore.Replace('__STAMP__',$stamp).Replace('__TARGET__',$target).Replace('__VERSION__',[string]$suspendedVersion)
    $observed = $false
    try {
        $changed = Invoke-RoleSql $Db $Role $Credential $suspend
        $current = Invoke-RoleSql $Db $Role $Credential $observe -ReadOnly
        if ($changed.status -ne 2 -or $changed.version -ne $suspendedVersion -or
            $current.status -ne 2 -or $current.version -ne $suspendedVersion -or !$current.ownedSuspension) {
            throw 'Synthetic suspension readback mismatch'
        }
        Set-ControlMarker $ContainerId 'membership-revoked'
        $observed = Wait-ControlMarker $ContainerId 'membership-request-done' 35
    }
    finally {
        $current = Invoke-RoleSql $Db $Role $Credential $observe -ReadOnly
        if ($current.status -eq 2 -and $current.version -eq $suspendedVersion -and $current.ownedSuspension) {
            $activated = Invoke-RoleSql $Db $Role $Credential $restore
            $current = Invoke-RoleSql $Db $Role $Credential $observe -ReadOnly
            if ($activated.status -ne 1 -or $current.status -ne 1 -or
                $current.version -ne ($suspendedVersion + 1) -or !$current.unsuspended) {
                throw 'Synthetic membership restoration readback mismatch'
            }
            Write-Output 'SYNTHETIC_MEMBERSHIP_RESTORED'
            Set-ControlMarker $ContainerId 'membership-restored'
        }
        elseif ($current.status -ne 1 -or $current.version -ne $beforeVersion -or !$current.unsuspended) {
            throw 'Unexpected synthetic membership state after attempted suspension'
        }
    }
    if (!$observed) { throw 'Browser revocation observation marker was absent' }
    Write-Output 'SYNTHETIC_REVOCATION_ROUND_TRIP_READY'
}
$authSql = @'
DO $data$
BEGIN
    PERFORM set_config('zeka.organisation_id', '__ORG_A__', true);
    IF zeka.current_organisation_id() IS DISTINCT FROM '__ORG_A__'::uuid
       OR NOT EXISTS (SELECT 1 FROM public."AspNetUsers" WHERE "Id" = '__OWNER_A__'::uuid
            AND "EmailConfirmed" AND "Status" = 2 AND "NormalizedEmail" ~ '^MVP02-__FIXTURE_ID__-OWNERA-[0-9A-F]{12}@EXAMPLE\.INVALID$')
       OR NOT EXISTS (SELECT 1 FROM public."AspNetUsers" WHERE "Id" = '__OWNER_B__'::uuid
            AND "EmailConfirmed" AND "Status" = 2 AND "NormalizedEmail" ~ '^MVP02-__FIXTURE_ID__-OWNERB-[0-9A-F]{12}@EXAMPLE\.INVALID$')
       OR NOT EXISTS (SELECT 1 FROM public."AspNetUsers" WHERE "Id" = '__ASSIGNED__'::uuid
            AND "EmailConfirmed" AND "Status" = 2 AND "NormalizedEmail" ~ '^MVP02-__FIXTURE_ID__-ASSIGNED-[0-9A-F]{12}@EXAMPLE\.INVALID$')
       OR NOT EXISTS (SELECT 1 FROM public."Organisations" WHERE "Id" = '__ORG_A__'::uuid
            AND "OwnerUserId" = '__OWNER_A__'::uuid AND "Status" = 2 AND "DisplayName" = 'MVP02 Fixture Alpha')
       OR NOT EXISTS (SELECT 1 FROM public."Organisations" WHERE "Id" = '__ORG_B__'::uuid
            AND "OwnerUserId" = '__OWNER_B__'::uuid AND "Status" = 2 AND "DisplayName" = 'MVP02 Fixture Beta')
       OR NOT EXISTS (SELECT 1 FROM public."PermissionSets" WHERE "Id" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a04'::uuid
            AND "Code" = 'LimitedViewer' AND "IsSystem")
       OR NOT EXISTS (SELECT 1 FROM public."OrganisationMemberships" WHERE "Id" = '__MEMBER_A__'::uuid
            AND "OrganisationId" = '__ORG_A__'::uuid AND "UserId" = '__OWNER_A__'::uuid AND "Status" = 1
            AND "PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid)
       OR NOT EXISTS (SELECT 1 FROM public."OrganisationMemberships" WHERE "Id" = '__MEMBER_B__'::uuid
            AND "OrganisationId" = '__ORG_B__'::uuid AND "UserId" = '__OWNER_B__'::uuid AND "Status" = 1
            AND "PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid)
       OR EXISTS (SELECT 1 FROM public."OrganisationMemberships" WHERE "UserId" = '__ASSIGNED__'::uuid
            AND NOT ("OrganisationId" = '__ORG_A__'::uuid AND "Status" = 1
                AND "PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a04'::uuid)) THEN
        RAISE SQLSTATE 'P5202' USING MESSAGE = 'Synthetic identity and catalogue binding rejected';
    END IF;
END
$data$;
INSERT INTO public."OrganisationMemberships"
    ("Id","OrganisationId","UserId","PermissionSetId","Status","JoinedAtUtc","ConcurrencyVersion")
SELECT gen_random_uuid(), '__ORG_A__'::uuid, '__ASSIGNED__'::uuid,
    'd8eceeeb-b796-4d77-92a5-6a11fab10a04'::uuid, 1, statement_timestamp(), 1
WHERE NOT EXISTS (SELECT 1 FROM public."OrganisationMemberships" WHERE "UserId" = '__ASSIGNED__'::uuid);
DO $data$
BEGIN
    IF (SELECT count(*) FROM public."OrganisationMemberships" m
        WHERE m."OrganisationId" = '__ORG_A__'::uuid AND m."UserId" = '__ASSIGNED__'::uuid AND m."Status" = 1
          AND m."PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a04'::uuid
          AND NOT EXISTS (SELECT 1 FROM public."MembershipPermissionGrants" g
            WHERE g."OrganisationId" = '__ORG_A__'::uuid AND g."OrganisationMembershipId" = m."Id")) <> 1 THEN
        RAISE SQLSTATE 'P5207' USING MESSAGE = 'Synthetic membership verification failed';
    END IF;
END
$data$;
SELECT json_build_object('membershipId',"Id") FROM public."OrganisationMemberships"
WHERE "OrganisationId" = '__ORG_A__'::uuid AND "UserId" = '__ASSIGNED__'::uuid AND "Status" = 1
  AND "PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a04'::uuid
  AND NOT EXISTS (SELECT 1 FROM public."MembershipPermissionGrants" g
    WHERE g."OrganisationId" = '__ORG_A__'::uuid AND g."OrganisationMembershipId" = "OrganisationMemberships"."Id");
'@

$clientSql = @'
DO $data$
BEGIN
    PERFORM set_config('zeka.organisation_id', '__ORGANISATION__', true);
    IF zeka.current_organisation_id() IS DISTINCT FROM '__ORGANISATION__'::uuid
       OR NOT (SELECT relrowsecurity AND relforcerowsecurity AND relowner <> (SELECT oid FROM pg_roles WHERE rolname = current_user)
            FROM pg_class WHERE oid = 'public."Clients"'::regclass)
       OR EXISTS (SELECT 1 FROM public."Clients" WHERE "OrganisationId" = '__ORGANISATION__'::uuid
            AND "ReferenceNumber" LIKE '__MARKER__-%'
            AND ("CreatedBy" <> '__MARKER__' OR "Softdelete" OR "LastName" <> 'Mvp02lookup'))
       OR EXISTS (SELECT 1 FROM public."Clients" WHERE "OrganisationId" = '__ORGANISATION__'::uuid
            AND "CreatedBy" <> '__MARKER__') THEN
        RAISE SQLSTATE 'P5203' USING MESSAGE = 'Synthetic Client tenant binding rejected';
    END IF;
END
$data$;
WITH fixture AS (
    SELECT n, lpad(n::text, 2, '0') AS suffix, 800315000 + __OFFSET__ + n AS digits
    FROM generate_series(0,__LAST_INDEX__) n
)
INSERT INTO public."Clients"
    ("ReferenceNumber","CivilStatus","FirstName","LastName","Gender","BirthDate","PlaceOfBirth",
     "Nationality","Ssn","SocialWorkerName","CreatedBy","Created","LastModifiedBy","Softdelete","OrganisationId")
SELECT '__MARKER__-' || suffix, 4, '__GROUP__' || suffix, 'Mvp02lookup', 0, date '1980-03-15',
    'Synthetic location','Synthetic nationality', lpad(digits::text,9,'0') || lpad((97 - digits % 97)::text,2,'0'),
    'Synthetic fixture staff','__MARKER__',localtimestamp,'__MARKER__',false,'__ORGANISATION__'::uuid
FROM fixture
ON CONFLICT ("OrganisationId","ReferenceNumber") DO NOTHING;

__ASSIGNMENT_SQL__

DO $data$
BEGIN
    IF (SELECT count(*) FROM public."Clients" WHERE "CreatedBy" = '__MARKER__') <> (__LAST_INDEX__ + 1) THEN
        RAISE SQLSTATE 'P5204' USING MESSAGE = 'Synthetic beneficiary count mismatch';
    END IF;
END
$data$;
SELECT json_build_object('beneficiaries', json_agg(json_build_object('beneficiaryId', "Id",
    'displayName', "LastName" || ' ' || "FirstName") ORDER BY "FirstName", "Id"))
FROM public."Clients" WHERE "CreatedBy" = '__MARKER__';
'@

$assignmentSql = @'
DO $data$
BEGIN
    IF EXISTS (SELECT 1 FROM public."SocialWorkers" WHERE "OrganisationMembershipId" = '__ASSIGNED_MEMBERSHIP__'::uuid
        AND ("CreatedBy" <> '__MARKER__' OR "Softdelete" OR "ProjectionVersion" <> 1)) THEN
        RAISE SQLSTATE 'P5205' USING MESSAGE = 'Unexpected synthetic staff projection';
    END IF;
END
$data$;
INSERT INTO public."SocialWorkers"
    ("FirstName","LastName","UserName","TeamName","TeamAcronym","CreatedBy","Created","LastModifiedBy",
     "Softdelete","OrganisationId","LastProjectionEventId","OrganisationMembershipId","ProjectionVersion")
VALUES ('Fixture','Assigned','__MARKER__','Synthetic fixture','MVP02','__MARKER__',localtimestamp,'__MARKER__',
    false,'__ORGANISATION__'::uuid,gen_random_uuid(),'__ASSIGNED_MEMBERSHIP__'::uuid,1)
ON CONFLICT ("OrganisationId","OrganisationMembershipId") DO NOTHING;
INSERT INTO public."SocialCases"
    ("StartDate","SocialWorkerId","ClientId","CreatedBy","Created","LastModifiedBy","Softdelete","OrganisationId")
SELECT localtimestamp - interval '2 days', worker."SocialWorkerId", beneficiary."Id",
    '__MARKER__',localtimestamp,'__MARKER__',false,'__ORGANISATION__'::uuid
FROM public."Clients" beneficiary JOIN public."SocialWorkers" worker
    ON worker."OrganisationId" = beneficiary."OrganisationId"
    AND worker."OrganisationMembershipId" = '__ASSIGNED_MEMBERSHIP__'::uuid
WHERE beneficiary."ReferenceNumber" = '__MARKER__-00' AND beneficiary."OrganisationId" = '__ORGANISATION__'::uuid
    AND NOT EXISTS (SELECT 1 FROM public."SocialCases" c WHERE c."ClientId" = beneficiary."Id");
DO $data$
BEGIN
    IF (SELECT count(*) FROM public."SocialCases" c JOIN public."Clients" b ON b."Id" = c."ClientId"
            JOIN public."SocialWorkers" w ON w."SocialWorkerId" = c."SocialWorkerId"
        WHERE b."ReferenceNumber" = '__MARKER__-00' AND w."OrganisationMembershipId" = '__ASSIGNED_MEMBERSHIP__'::uuid
            AND NOT c."Softdelete" AND c."EndDate" IS NULL AND c."CreatedBy" = '__MARKER__') <> 1
       OR (SELECT count(*) FROM public."SocialCases") <> 1 THEN
        RAISE SQLSTATE 'P5206' USING MESSAGE = 'Synthetic assignment binding mismatch';
    END IF;
END
$data$;
'@

try {
    Assert-PrivatePath $root -Directory -Protected
    $receipt = Join-Path $root 'infrastructure.json'
    Assert-PrivatePath $receipt
    $scan = & sonar analyze secrets $receipt 2>&1
    if ($LASTEXITCODE -ne 0 -or ($scan | Out-String) -match '\b[A-Za-z][A-Za-z0-9_-]*:S\d+\b') {
        throw 'Infrastructure receipt secret scan did not pass'
    }
    $runtime = ConvertFrom-Json -AsHashtable -InputObject ([IO.File]::ReadAllText($receipt))
    if ($runtime.fixtureId -cne $fixtureId -or $runtime.project -cne $project -or
        $runtime.internalNetwork -cne ($project+'_internal') -or $runtime.edgeNetwork -cne ($project+'_edge') -or
        $runtime.internalNetworkId -cnotmatch '^[0-9a-f]{64}$' -or $runtime.edgeNetworkId -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Owned infrastructure receipt did not match the fixture'
    }
    $postgresRows = @($runtime.containers | Where-Object { ($_ -split '\|',4)[1] -ceq $pgName })
    if ($postgresRows.Count -ne 1) { throw 'The PostgreSQL receipt is missing or ambiguous' }
    $expectedPostgresShort = ($postgresRows[0] -split '\|',4)[0]
    if ($expectedPostgresShort -cnotmatch '^[0-9a-f]{12}$') { throw 'PostgreSQL receipt ID mismatch' }
    $expectedEngine = Confirm-Guid $runtime.engineId
    $expectedBridge = $runtime.internalNetworkId
    $expectedEdgeBridge = $runtime.edgeNetworkId
    $null = Assert-DatabaseTarget
    $stage = 'runtime-roles'
    $authRole = 'zeka_auth_runtime'
    $clientRole = 'zeka_client_runtime'
    $authDb = 'zeka_m1_auth'
    $clientDb = 'zeka_m1_client'
    $authConnector = Read-RoleConfiguration 'auth' 'Default' $authDb $authRole
    $clientConnector = Read-RoleConfiguration 'client' 'ClientApiConnection' $clientDb $clientRole
    $probe = "SELECT json_build_object('accepted', true);"
    if (!(Invoke-RoleSql $authDb $authRole $authConnector $probe -ReadOnly).accepted -or
        !(Invoke-RoleSql $clientDb $clientRole $clientConnector $probe -ReadOnly).accepted) {
        throw 'Read-only runtime role admission failed'
    }
    if ($PreflightOnly) {
        Write-Output 'SYNTHETIC_SEED_TARGET_ADMITTED'
        return
    }
    if ($Enroll) {
        $stage = 'enroll'
        $runner = Join-Path $PSScriptRoot 'Enroll-Mvp02SyntheticAccounts.cjs'
        $scan = & sonar analyze secrets $runner 2>&1
        if ($LASTEXITCODE -ne 0 -or ($scan | Out-String) -match '\b[A-Za-z][A-Za-z0-9_-]*:S\d+\b') {
            throw 'Auth enrollment runner secret scan did not pass'
        }
        $argv = @($runner, $root)
        if ($ResumeAfterOwnerAVerification) {
            Assert-PrivatePath (Join-Path $root 'synthetic-accounts.json')
            $argv += '--resume-after-owner-a-verification'
        }
        & node @argv
        if ($LASTEXITCODE -ne 0) { throw 'Genuine Auth enrollment did not complete' }
    }

    $stage = 'auth-bindings'
    $bindingPath = Join-Path $root 'synthetic-auth-bindings.json'
    $receiptPath = Join-Path $root 'synthetic-beneficiary-bindings.json'
    Assert-PrivatePath $bindingPath

    $bindings = ConvertFrom-Json -AsHashtable -InputObject ([IO.File]::ReadAllText($bindingPath))
    if ($bindings.project -cne $project) { throw 'Auth fixture project mismatch' }
    $ownerA = Confirm-Guid $bindings.ownerA.subjectId
    $ownerB = Confirm-Guid $bindings.ownerB.subjectId
    $assigned = Confirm-Guid $bindings.assigned.subjectId
    $orgA = Confirm-Guid $bindings.ownerA.organisationId
    $orgB = Confirm-Guid $bindings.ownerB.organisationId
    $memberA = Confirm-Guid $bindings.ownerA.membershipId
    $memberB = Confirm-Guid $bindings.ownerB.membershipId
    if (@($ownerA,$ownerB,$assigned,$orgA,$orgB,$memberA,$memberB | Select-Object -Unique).Count -ne 7) {
        throw 'Auth fixture identifiers are not distinct'
    }

    if ($RevocationWitnessContainer) {
        $stage = 'assigned-revocation-witness'
        Assert-PrivatePath $receiptPath
        $scope = ConvertFrom-Json -AsHashtable -InputObject ([IO.File]::ReadAllText($receiptPath))
        if ($scope.project -cne $project -or (Confirm-Guid $scope.assigned.organisationId) -cne $orgA) {
            throw 'Seeded assigned fixture binding mismatch'
        }
        $assignedIdentity = Confirm-Guid $scope.assignedMembershipId
        Invoke-AssignedRevocationWitness $RevocationWitnessContainer $orgA $assigned $assignedIdentity $authDb $authRole $authConnector
        return
    }
    if (Test-Path -LiteralPath $receiptPath) { throw 'Completed baseline already exists' }
    $sql = $authSql.Replace('__OWNER_A__',$ownerA).Replace('__OWNER_B__',$ownerB).Replace('__ASSIGNED__',$assigned).
        Replace('__ORG_A__',$orgA).Replace('__ORG_B__',$orgB).Replace('__MEMBER_A__',$memberA).Replace('__MEMBER_B__',$memberB).Replace('__FIXTURE_ID__',$fixtureId.ToUpperInvariant())
    $stage = 'auth-membership'
    $assignedMembership = Confirm-Guid (Invoke-RoleSql $authDb $authRole $authConnector $sql).membershipId

    $stage = 'client-alpha'
    $sqlA = $clientSql.Replace('__ASSIGNMENT_SQL__',$assignmentSql).Replace('__ASSIGNED_MEMBERSHIP__',$assignedMembership).
        Replace('__ORGANISATION__',$orgA).Replace('__MARKER__',$fixtureMarker).
        Replace('__OFFSET__','101').Replace('__LAST_INDEX__','21').Replace('__GROUP__','Alpha')
    $alpha = (Invoke-RoleSql $clientDb $clientRole $clientConnector $sqlA).beneficiaries

    $stage = 'client-beta'
    $sqlB = $clientSql.Replace('__ASSIGNMENT_SQL__','').Replace('__ORGANISATION__',$orgB).
        Replace('__MARKER__',$fixtureMarker).Replace('__OFFSET__','201').
        Replace('__LAST_INDEX__','0').Replace('__GROUP__','Beta')
    $beta = (Invoke-RoleSql $clientDb $clientRole $clientConnector $sqlB).beneficiaries

    $stage = 'verify-and-record'
    if ($alpha.Count -ne 22 -or $beta.Count -ne 1 -or
        $alpha[0].displayName -cne 'Mvp02lookup Alpha00' -or $beta[0].displayName -cne 'Mvp02lookup Beta00' -or
        @(@($alpha.beneficiaryId) + @($beta.beneficiaryId) | Select-Object -Unique).Count -ne 23) {
        throw 'Unexpected scoped beneficiary set'
    }
    $receipt = [ordered]@{
        project = $project
        lookupText = 'Mvp02lookup'
        assignedMembershipId = $assignedMembership
        ownerA = @{ organisationId = $orgA; hasMore = $true; all = $alpha; items = @($alpha | Select-Object -First 20) }
        ownerB = @{ organisationId = $orgB; hasMore = $false; items = @($beta) }
        assigned = @{ organisationId = $orgA; hasMore = $false; items = @($alpha[0]); denied = $alpha[1] }
    }
    $stream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        try { $writer.WriteLine(($receipt | ConvertTo-Json -Depth 8)) }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
    Write-Output 'SYNTHETIC_SEED_READY'
}
catch {
    $message = $_.Exception.Message
    if ($message -match '^PostgreSQL execution failed \d+ \([A-Z0-9,]*\)$') {
        throw "MVP02 synthetic seed stopped at $stage ($message)"
    }
    throw "MVP02 synthetic seed stopped at $stage"
}
finally {
    $authConnector = $null
    $clientConnector = $null
}
