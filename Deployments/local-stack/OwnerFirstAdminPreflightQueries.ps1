# Pure source-owned query factories. No connection, file, credential or native operations.
# Extracted from the retained 1657478 read-only preflight; privilege policy is delegated unchanged.
function ConvertTo-OwnerPreflightSqlLiteral {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-OwnerFirstAdminPreflightTransactionTemplates {
    @(
        [pscustomobject]@{ Id = 'owner.transaction.begin-readonly.v1'; Sql = 'BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY' }
        [pscustomobject]@{ Id = 'owner.transaction.statement-timeout.v1'; Sql = "SET LOCAL statement_timeout='10000ms'" }
        [pscustomobject]@{ Id = 'owner.transaction.lock-timeout.v1'; Sql = "SET LOCAL lock_timeout='1000ms'" }
        [pscustomobject]@{ Id = 'owner.transaction.rollback.v1'; Sql = 'ROLLBACK' }
    )
}

function Get-OwnerFirstAdminPreflightQueryIds {
    @('owner.identity.v1', 'owner.schema-presence.v1', 'owner.schema-shape.v1',
      'owner.privileges.v1', 'owner.catalogue.v1', 'owner.existing-identity.v1',
      'owner.idempotency.v1', 'owner.rls-metadata.v1')
}

function Get-OwnerFirstAdminPreflightQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('owner.identity.v1','owner.schema-presence.v1','owner.schema-shape.v1','owner.privileges.v1','owner.catalogue.v1','owner.existing-identity.v1','owner.idempotency.v1','owner.rls-metadata.v1')][string]$QueryId,
        [string]$InitializationId, [string]$DatabaseSourceCommit, [string]$ModelJson,
        [string]$SubjectId, [string]$NormalizedEmail, [string]$IdempotencyKey,
        [string]$ManifestJson, [string]$SourceCommit
    )
    if ($QueryId -eq 'owner.privileges.v1') {
        if (-not (Get-Command Get-OwnerFirstAdminPrivilegeQuery -CommandType Function -ErrorAction SilentlyContinue)) { throw 'OWNER_PRIVILEGE_FACTORY_NOT_IMPORTED' }
        return Get-OwnerFirstAdminPrivilegeQuery
    }
    if ($QueryId -eq 'owner.rls-metadata.v1') {
        if (-not (Get-Command Get-OwnerFirstAdminRlsMetadataQuery -CommandType Function -ErrorAction SilentlyContinue)) { throw 'OWNER_RLS_FACTORY_NOT_IMPORTED' }
        return Get-OwnerFirstAdminRlsMetadataQuery -ManifestJson $ManifestJson -SourceCommit $SourceCommit
    }
    $bindings = @{}
    if ($QueryId -eq 'owner.identity.v1') {
        if ($InitializationId -cnotmatch '\A[A-Za-z0-9_-]{16,128}\z' -or $DatabaseSourceCommit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'OWNER_BOOTSTRAP_BINDING_REJECTED' }
        $bindings['__INIT__'] = ConvertTo-OwnerPreflightSqlLiteral $InitializationId
        $bindings['__DBSOURCE__'] = ConvertTo-OwnerPreflightSqlLiteral $DatabaseSourceCommit
        $bindings['__DBMARKER__'] = ConvertTo-OwnerPreflightSqlLiteral ('zeka-m1-database/v1:' + $InitializationId + ':auth:' + $DatabaseSourceCommit)
    }
    if ($QueryId -in @('owner.schema-presence.v1', 'owner.schema-shape.v1')) {
        if ([string]::IsNullOrWhiteSpace($ModelJson) -or $ModelJson.Length -gt 1048576) { throw 'OWNER_MODEL_INPUT_REJECTED' }
        try { $model = ConvertFrom-Json -InputObject $ModelJson -Depth 40 -ErrorAction Stop } catch { throw 'OWNER_MODEL_INPUT_REJECTED' }
        $expected = @('AspNetUsers','AuditEntries','IdempotencyRecords','OrganisationMemberships','Organisations','OutboxMessages','PermissionSets')
        if ($model.tables -isnot [array] -or $model.tables.Count -ne 7) { throw 'OWNER_MODEL_INPUT_REJECTED' }
        $actual = @($model.tables | ForEach-Object { if ($_.schema -cne 'public' -or $_.name -isnot [string]) { throw 'OWNER_MODEL_INPUT_REJECTED' }; $_.name })
        if (@($actual | Sort-Object -Unique).Count -ne 7 -or @($actual | Where-Object { $_ -cnotin $expected }).Count) { throw 'OWNER_MODEL_INPUT_REJECTED' }
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ModelJson))
        $bindings['__MODEL__'] = "convert_from(decode('" + $encoded + "','base64'),'UTF8')::jsonb"
    }
    if ($QueryId -in @('owner.existing-identity.v1', 'owner.idempotency.v1')) {
        $subject = [Guid]::Empty
        if (-not [Guid]::TryParseExact($SubjectId, 'D', [ref]$subject) -or $subject -eq [Guid]::Empty) { throw 'OWNER_SUBJECT_INPUT_REJECTED' }
        $bindings['__SUBJECT__'] = ConvertTo-OwnerPreflightSqlLiteral $subject.ToString('D')
    }
    if ($QueryId -eq 'owner.existing-identity.v1') {
        if ([string]::IsNullOrWhiteSpace($NormalizedEmail) -or $NormalizedEmail.Length -gt 256 -or $NormalizedEmail -cnotmatch '\A[A-Z0-9.!#$%&''*+/=?^_`{|}~-]+@[A-Z0-9](?:[A-Z0-9.-]*[A-Z0-9])?\z') { throw 'OWNER_EMAIL_INPUT_REJECTED' }
        $bindings['__EMAIL__'] = ConvertTo-OwnerPreflightSqlLiteral $NormalizedEmail
    }
    if ($QueryId -eq 'owner.idempotency.v1') {
        if ([string]::IsNullOrWhiteSpace($IdempotencyKey) -or $IdempotencyKey.Length -gt 200 -or $IdempotencyKey -match '[\x00-\x1f\x7f]') { throw 'OWNER_IDEMPOTENCY_INPUT_REJECTED' }
        $keyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($IdempotencyKey)))
        $bindings['__KEYHASH__'] = ConvertTo-OwnerPreflightSqlLiteral $keyHash
    }
    $query = switch ($QueryId) {
        'owner.identity.v1' {
@'
SELECT json_build_object('readOnly',current_setting('transaction_read_only')='on','roleAllowed',
 session_user='zeka_auth_runtime' AND current_user='zeka_auth_runtime' AND current_database()='zeka_m1_auth' AND
 r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolinherit AND NOT r.rolreplication AND r.rolconfig IS NULL AND
 NOT EXISTS(SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid) AND NOT EXISTS(SELECT 1 FROM pg_db_role_setting s WHERE s.setrole=r.oid) AND
 NOT EXISTS(SELECT 1 FROM pg_parameter_acl p WHERE has_parameter_privilege(current_user,p.parname,'SET') OR has_parameter_privilege(current_user,p.parname,'ALTER SYSTEM')),
 'roleMarkerAllowed',(SELECT count(*)=5 AND count(DISTINCT key)=5 FROM json_each(shobj_description(r.oid,'pg_authid')::json)) AND
 (shobj_description(r.oid,'pg_authid')::json->>'schema')='zeka-m1-role/v1' AND (shobj_description(r.oid,'pg_authid')::json->>'service')='auth' AND
 (shobj_description(r.oid,'pg_authid')::json->>'kind')='runtime' AND (shobj_description(r.oid,'pg_authid')::json->>'initializationId')=__INIT__ AND (shobj_description(r.oid,'pg_authid')::json->>'sourceCommit')=__DBSOURCE__,
 'databaseMarkerAllowed',shobj_description(d.oid,'pg_database')=__DBMARKER__)
 FROM pg_roles r CROSS JOIN pg_database d WHERE r.rolname=current_user AND d.datname=current_database()
'@
        }
        'owner.schema-presence.v1' {
@'
WITH spec AS (SELECT __MODEL__ AS j), t AS (SELECT x->>'schema' AS s,x->>'name' AS n FROM spec,jsonb_array_elements(j->'tables') x) SELECT json_build_object('missingTables',coalesce((SELECT json_agg(s||'.'||n) FROM t WHERE to_regclass(format('%I.%I',s,n)) IS NULL),'[]'::json))
'@
        }
        'owner.schema-shape.v1' {
@'
WITH spec AS (SELECT __MODEL__ AS j), t AS (SELECT x AS v,x->>'schema' AS s,x->>'name' AS n FROM spec,jsonb_array_elements(j->'tables') x), c AS (SELECT t.*,z AS col FROM t,jsonb_array_elements(v->'columns') z), ix AS (SELECT t.*,z AS idx FROM t,jsonb_array_elements(v->'indexes') z)
SELECT json_build_object('columnMismatches',coalesce((SELECT json_agg(s||'.'||n||'.'||(col->>'name')) FROM c WHERE NOT EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=to_regclass(format('%I.%I',s,n)) AND a.attname=col->>'name' AND a.attnum>0 AND NOT a.attisdropped AND format_type(a.atttypid,a.atttypmod)=col->>'storeType' AND a.attnotnull=(NOT (col->>'nullable')::boolean))),'[]'::json),
'missingPrimaryKeys',coalesce((SELECT json_agg(s||'.'||n) FROM t WHERE NOT EXISTS(SELECT 1 FROM pg_constraint k WHERE k.conrelid=to_regclass(format('%I.%I',s,n)) AND k.contype='p' AND k.convalidated AND (SELECT jsonb_agg(a.attname ORDER BY ord.n) FROM unnest(k.conkey) WITH ORDINALITY ord(num,n) JOIN pg_attribute a ON a.attrelid=k.conrelid AND a.attnum=ord.num)=v->'primaryKey')),'[]'::json),
'missingUniqueIndexes',coalesce((SELECT json_agg(idx->>'name') FROM ix WHERE NOT EXISTS(SELECT 1 FROM pg_indexes p JOIN pg_class cls ON cls.oid=to_regclass(format('%I.%I',p.schemaname,p.indexname)) JOIN pg_index i ON i.indexrelid=cls.oid WHERE p.schemaname=s AND p.tablename=n AND p.indexname=idx->>'name' AND i.indisunique AND i.indisvalid AND coalesce(pg_get_expr(i.indpred,i.indrelid),'')=coalesce(idx->>'filter','') AND (SELECT jsonb_agg(a.attname ORDER BY ord.n) FROM unnest(i.indkey::smallint[]) WITH ORDINALITY ord(num,n) JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=ord.num WHERE ord.n<=i.indnkeyatts)=idx->'columns')),'[]'::json),
'structuralOnly',true)
'@
        }
        'owner.catalogue.v1' {
@'
SELECT json_build_object('ownerAllowed',count(*)=1 AND bool_and("Code"='Owner' AND "IsSystem"=true)) FROM public."PermissionSets" WHERE "Id"='d8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid
'@
        }
        'owner.existing-identity.v1' {
@'
SELECT json_build_object('identityAllowed',count(*)=0 OR (count(*)=1 AND bool_and("Id"=__SUBJECT__::uuid AND "NormalizedEmail"=__EMAIL__ AND lower("Email")=lower(__EMAIL__) AND "Status"=2 AND "EmailConfirmed"=true))) FROM public."AspNetUsers" WHERE "Id"=__SUBJECT__::uuid OR "NormalizedEmail"=__EMAIL__
'@
        }
        'owner.idempotency.v1' {
@'
SELECT json_build_object('matchingRecords',(SELECT count(*) FROM public."IdempotencyRecords" WHERE "UserId"=__SUBJECT__::uuid AND "Operation"='organisation.provisioning/v1' AND "KeyHash"=__KEYHASH__),'ownedOrganisations',(SELECT count(*) FROM public."Organisations" WHERE "OwnerUserId"=__SUBJECT__::uuid))
'@
        }
        default { throw 'OWNER_QUERY_ID_REJECTED' }
    }
    foreach ($name in $bindings.Keys) { $query = $query.Replace($name, $bindings[$name]) }
    if ($query -match '__[A-Z]+__') { throw 'OWNER_QUERY_BINDING_INCOMPLETE' }
    return $query
}
