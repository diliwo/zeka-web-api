# Metadata-only admission, not full deployment verification or behavioral tenant isolation.
# The retained failing query is preserved separately. This factory fixes only name[]/text[] resolution.
function Get-OwnerFirstAdminRlsMetadataQuery {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ManifestJson, [Parameter(Mandatory)][string]$SourceCommit)
    if ($SourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or [string]::IsNullOrWhiteSpace($ManifestJson) -or $ManifestJson.Length -gt 1048576) { throw 'OWNER_RLS_BINDING_REJECTED' }
    $bytes = [Text.Encoding]::UTF8.GetBytes($ManifestJson)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if ($hash -cne '6543c54a1d7ab5539ef321af8e81c0a5a0692f3378912a5926fd159432e68ad6') { throw 'OWNER_RLS_MANIFEST_BINDING_REJECTED' }
    $encoded = [Convert]::ToBase64String($bytes)
    $query = @'
WITH manifest AS (
 SELECT convert_from(decode('__MANIFEST_BASE64__','base64'),'UTF8')::jsonb AS j
), managed_schemas AS (
 SELECT jsonb_array_elements_text(j->'managedSchemas') AS schema_name FROM manifest
), expected_tables AS (
 SELECT x->>'schema' AS schema_name,x->>'name' AS table_name,true AS protected FROM manifest,jsonb_array_elements(j->'protectedTables') x
 UNION ALL SELECT x->>'schema',x->>'name',false FROM manifest,jsonb_array_elements(j->'excludedTables') x
 UNION ALL SELECT j->'migrationHistoryTable'->>'schema',j->'migrationHistoryTable'->>'name',false FROM manifest
), actual_tables AS (
 SELECT n.nspname AS schema_name,c.relname AS table_name,c.oid AS relation_oid,o.rolname AS owner_name,c.relrowsecurity AS enabled,c.relforcerowsecurity AS forced
 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
 JOIN pg_catalog.pg_roles o ON o.oid=c.relowner WHERE n.nspname IN(SELECT schema_name FROM managed_schemas) AND c.relkind IN('r','p')
), table_failures AS (
 SELECT coalesce(e.schema_name,a.schema_name)||'.'||coalesce(e.table_name,a.table_name) AS object_name
 FROM expected_tables e FULL JOIN actual_tables a USING(schema_name,table_name)
 WHERE e.table_name IS NULL OR a.relation_oid IS NULL OR a.owner_name IS DISTINCT FROM(SELECT j->>'ownerRole' FROM manifest)
 OR a.enabled IS DISTINCT FROM e.protected OR a.forced IS DISTINCT FROM e.protected
), organisation_column_failures AS (
 SELECT e.schema_name||'.'||e.table_name AS object_name FROM expected_tables e LEFT JOIN actual_tables a USING(schema_name,table_name)
 WHERE e.protected AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_attribute x WHERE x.attrelid=a.relation_oid AND x.attname='OrganisationId'
 AND x.attnum>0 AND NOT x.attisdropped AND x.attnotnull AND pg_catalog.format_type(x.atttypid,x.atttypmod)='uuid')
), expected_policies AS (
 SELECT e.schema_name,e.table_name,(j->>'policyPrefix')||lower(e.table_name)||'_organisation' AS policy_name,
 j->>'policyUsingExpression' AS using_expression,j->>'policyWithCheckExpression' AS check_expression,
 ARRAY(SELECT role_name FROM(
   SELECT j->>'runtimeRole' AS role_name
   UNION ALL SELECT jsonb_array_elements_text(z->'roles') FROM jsonb_array_elements(coalesce(j->'additionalPolicyRoles','[]'::jsonb)) z
   WHERE z->'object'->>'schema'=e.schema_name AND z->'object'->>'name'=e.table_name
 ) r ORDER BY role_name)::text[] AS roles
 FROM expected_tables e CROSS JOIN manifest WHERE e.protected
), actual_policies AS (
 SELECT n.nspname AS schema_name,c.relname AS table_name,p.polname AS policy_name,p.oid AS policy_oid,p.polcmd AS command,p.polpermissive AS permissive,
 pg_catalog.pg_get_expr(p.polqual,p.polrelid,false) AS using_expression,pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false) AS check_expression,
 ARRAY(SELECT CASE WHEN role_oid=0 THEN 'PUBLIC' ELSE coalesce(r.rolname::text,'UNKNOWN_ROLE_'||role_oid::text) END
 FROM unnest(p.polroles) role_oid LEFT JOIN pg_catalog.pg_roles r ON r.oid=role_oid
 ORDER BY 1)::text[] AS roles
 FROM pg_catalog.pg_policy p JOIN pg_catalog.pg_class c ON c.oid=p.polrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname IN(SELECT schema_name FROM managed_schemas)
), policy_failures AS (
 SELECT coalesce(e.schema_name,a.schema_name)||'.'||coalesce(e.table_name,a.table_name)||':'||coalesce(e.policy_name,a.policy_name) AS object_name
 FROM expected_policies e FULL JOIN actual_policies a USING(schema_name,table_name,policy_name)
 WHERE e.policy_name IS NULL OR a.policy_oid IS NULL OR a.command IS DISTINCT FROM '*'::"char" OR a.permissive IS DISTINCT FROM true
 OR a.using_expression IS DISTINCT FROM e.using_expression OR a.check_expression IS DISTINCT FROM e.check_expression OR a.roles IS DISTINCT FROM e.roles
), prohibited_failures AS (
 SELECT n.nspname||'.'||c.relname AS object_name FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace CROSS JOIN manifest
 WHERE n.nspname IN(SELECT schema_name FROM managed_schemas)
 AND CASE c.relkind WHEN 'f' THEN 'FOREIGN_TABLE' WHEN 'm' THEN 'MATERIALIZED_VIEW' WHEN 'v' THEN 'VIEW' ELSE '' END
 IN(SELECT jsonb_array_elements_text(j->'prohibitedRelationKinds'))
), topology_failures AS (
 SELECT pn.nspname||'.'||p.relname||'->'||cn.nspname||'.'||c.relname AS object_name FROM pg_catalog.pg_inherits i
 JOIN pg_catalog.pg_class p ON p.oid=i.inhparent JOIN pg_catalog.pg_namespace pn ON pn.oid=p.relnamespace
 JOIN pg_catalog.pg_class c ON c.oid=i.inhrelid JOIN pg_catalog.pg_namespace cn ON cn.oid=c.relnamespace
 WHERE pn.nspname IN(SELECT schema_name FROM managed_schemas) OR cn.nspname IN(SELECT schema_name FROM managed_schemas)
), context_facts AS (
 SELECT p.oid,o.rolname AS owner_name,l.lanname AS language,p.prosecdef,p.proleakproof,p.provolatile,p.proconfig,
 encode(pg_catalog.sha256(pg_catalog.convert_to(pg_catalog.rtrim(pg_catalog.pg_get_functiondef(p.oid),E'\r\n'),'UTF8')),'hex') AS definition_sha256
 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace JOIN pg_catalog.pg_roles o ON o.oid=p.proowner
 JOIN pg_catalog.pg_language l ON l.oid=p.prolang CROSS JOIN manifest
 WHERE n.nspname||'.'||p.proname=j->>'contextFunction' AND pg_catalog.pg_get_function_identity_arguments(p.oid)=''
), context_admission AS (
 SELECT count(*)=1 AND coalesce(bool_and(owner_name=(SELECT j->>'ownerRole' FROM manifest) AND language='plpgsql'
 AND NOT prosecdef AND NOT proleakproof AND provolatile='v' AND proconfig IS NOT DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
 AND definition_sha256=(SELECT d->>'definitionSha256' FROM manifest,jsonb_array_elements(j->'runtimeFunctionDefinitions') d
 WHERE d->>'functionIdentity'=(j->>'contextFunction')||'()')),false) AS allowed,
 coalesce(json_agg(definition_sha256),'[]'::json) AS observed_definition_sha256 FROM context_facts
), role_admission AS (
 SELECT count(*)=1 AND coalesce(bool_and(r.rolname='zeka_auth_runtime' AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
 AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolinherit AND NOT r.rolreplication AND r.rolconnlimit=-1 AND r.rolconfig IS NULL
 AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members x WHERE x.member=r.oid OR x.roleid=r.oid)
 AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_db_role_setting x WHERE x.setrole=r.oid)
 AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_parameter_acl x WHERE pg_catalog.has_parameter_privilege(current_user,x.parname,'SET')
 OR pg_catalog.has_parameter_privilege(current_user,x.parname,'ALTER SYSTEM'))),false) AS allowed
 FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
), verdict AS (
 SELECT current_database()='zeka_m1_auth' AND current_user='zeka_auth_runtime' AND session_user='zeka_auth_runtime'
 AND current_setting('transaction_read_only')='on' AND current_setting('server_version_num')::int>=170000 AND current_setting('server_version_num')::int<180000
 AND (SELECT allowed FROM role_admission) AND (SELECT allowed FROM context_admission)
 AND NOT EXISTS(SELECT 1 FROM table_failures) AND NOT EXISTS(SELECT 1 FROM organisation_column_failures)
 AND NOT EXISTS(SELECT 1 FROM policy_failures) AND NOT EXISTS(SELECT 1 FROM prohibited_failures) AND NOT EXISTS(SELECT 1 FROM topology_failures)
 AND (SELECT count(*)=42 FROM expected_tables) AND (SELECT count(*)=23 FROM expected_policies)
 AND (SELECT (j->>'schemaVersion')::int=19 AND j->>'service'='AuthManagement' AND jsonb_array_length(j->'relationTopology')=0 FROM manifest) AS allowed
)
SELECT json_build_object(
 'schema','zeka-m1-auth-rls-metadata-admission/v1','manifestSchemaVersion',19,
 'manifestSha256','6543c54a1d7ab5539ef321af8e81c0a5a0692f3378912a5926fd159432e68ad6',
 'source','__SOURCE__','metadataAdmission',(SELECT allowed FROM verdict),
 'readOnly',current_setting('transaction_read_only')='on','runtimeRoleAdmitted',(SELECT allowed FROM role_admission),
 'expectedTables',42,'actualTables',(SELECT count(*) FROM actual_tables),'expectedProtectedTables',23,'actualPolicies',(SELECT count(*) FROM actual_policies),
 'sevenProvisioningTablesExplicitlyExcluded',(SELECT count(*)=7 FROM expected_tables WHERE NOT protected AND schema_name='public'
 AND table_name IN('AspNetUsers','AuditEntries','IdempotencyRecords','OrganisationMemberships','Organisations','OutboxMessages','PermissionSets')),
 'tableFailures',coalesce((SELECT json_agg(object_name ORDER BY object_name) FROM table_failures),'[]'::json),
 'organisationColumnFailures',coalesce((SELECT json_agg(object_name ORDER BY object_name) FROM organisation_column_failures),'[]'::json),
 'policyFailures',coalesce((SELECT json_agg(object_name ORDER BY object_name) FROM policy_failures),'[]'::json),
 'prohibitedRelationFailures',coalesce((SELECT json_agg(object_name ORDER BY object_name) FROM prohibited_failures),'[]'::json),
 'topologyFailures',coalesce((SELECT json_agg(object_name ORDER BY object_name) FROM topology_failures),'[]'::json),
 'contextFunctionAdmitted',(SELECT allowed FROM context_admission),'contextFunctionDefinitionSha256',(SELECT observed_definition_sha256 FROM context_admission),
 'migrationHistoryData','NOT_RUN_RUNTIME_DENIED','fullManifestVerifier','NOT_RUN','behavioralTenantIsolation','NOT_RUN');
'@
    return $query.Replace('__MANIFEST_BASE64__', $encoded).Replace('__SOURCE__', $SourceCommit)
}

function Assert-OwnerFirstAdminRlsMetadataFacts {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Facts, [Parameter(Mandatory)][string]$SourceCommit)
    if ($SourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or $null -eq $Facts) { throw 'OWNER_RLS_METADATA_REJECTED' }
    foreach ($name in @('metadataAdmission','readOnly','runtimeRoleAdmitted','sevenProvisioningTablesExplicitlyExcluded','contextFunctionAdmitted')) {
        if ($Facts.$name -isnot [bool] -or -not $Facts.$name) { throw 'OWNER_RLS_METADATA_REJECTED' }
    }
    foreach ($name in @('schema','source','manifestSha256','migrationHistoryData','fullManifestVerifier','behavioralTenantIsolation')) {
        if ($Facts.$name -isnot [string]) { throw 'OWNER_RLS_METADATA_REJECTED' }
    }
    if ($Facts.schema -cne 'zeka-m1-auth-rls-metadata-admission/v1' -or $Facts.source -cne $SourceCommit -or $Facts.manifestSha256 -cne '6543c54a1d7ab5539ef321af8e81c0a5a0692f3378912a5926fd159432e68ad6') { throw 'OWNER_RLS_METADATA_REJECTED' }
    foreach ($pair in @(@('manifestSchemaVersion',19),@('expectedTables',42),@('actualTables',42),@('expectedProtectedTables',23),@('actualPolicies',23))) {
        $value = $Facts.($pair[0])
        if (($value -isnot [int] -and $value -isnot [long]) -or $value -ne $pair[1]) { throw 'OWNER_RLS_METADATA_REJECTED' }
    }
    foreach ($name in @('tableFailures','organisationColumnFailures','policyFailures','prohibitedRelationFailures','topologyFailures')) {
        if ($Facts.$name -isnot [array] -or $Facts.$name.Count -ne 0) { throw 'OWNER_RLS_METADATA_REJECTED' }
    }
    if ($Facts.contextFunctionDefinitionSha256 -isnot [array] -or $Facts.contextFunctionDefinitionSha256.Count -ne 1 -or $Facts.contextFunctionDefinitionSha256[0] -isnot [string] -or $Facts.contextFunctionDefinitionSha256[0] -cne 'be88914966acd2f06292da857c60c6c9a98dc7f37fd3210f99d9c031f9f2341b') { throw 'OWNER_RLS_METADATA_REJECTED' }
    if ($Facts.migrationHistoryData -cne 'NOT_RUN_RUNTIME_DENIED' -or $Facts.fullManifestVerifier -cne 'NOT_RUN' -or $Facts.behavioralTenantIsolation -cne 'NOT_RUN') { throw 'OWNER_RLS_METADATA_REJECTED' }
}
