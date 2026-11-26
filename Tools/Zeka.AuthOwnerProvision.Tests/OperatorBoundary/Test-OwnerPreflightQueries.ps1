[CmdletBinding()]
param([string]$ReportFile)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $repo 'Deployments/local-stack/OwnerFirstAdminPreflightQueries.ps1')
. (Join-Path $repo 'Deployments/local-stack/OwnerFirstAdminPreflightPrivileges.ps1')
. (Join-Path $repo 'Deployments/local-stack/OwnerFirstAdminRlsMetadata.ps1')
# These fixtures build SQL strings and validate synthetic metadata only. They never connect or execute SQL.
$checks=[Collections.Generic.List[object]]::new()
$source='a'*40
$subject='aabbccdd-1111-2222-3333-444455556666'
$manifest=[IO.File]::ReadAllText((Join-Path $repo 'Services/AuthManager/AuthManager.Infrastructure/Security/rls-manifest.v19.json'))
function Check([string]$Name,[scriptblock]$Action,[string]$Expected='') {
    try {&$Action;if($Expected){throw 'EXPECTED_REFUSAL_MISSING'}}
    catch {if(!$Expected-or($_.Exception.Message-cne$Expected-and$_.Exception.GetType().Name-cne$Expected)){throw ('OWNER_QUERY_METADATA_FIXTURE_FAILED_'+$Name)}}
    $checks.Add([ordered]@{name=$Name;passed=$true})
}
function Require([bool]$Value){if(!$Value){throw 'CONTRACT_EXPECTATION_FAILED'}}
function Model {
    $tables=@('AspNetUsers','AuditEntries','IdempotencyRecords','OrganisationMemberships','Organisations','OutboxMessages','PermissionSets')
    return [ordered]@{tables=@($tables|ForEach-Object{[ordered]@{schema='public';name=$_;columns=@();indexes=@();primaryKey=@('Id')}})}
}
function Query([string]$Id) {
    return Get-OwnerFirstAdminPreflightQuery -QueryId $Id -InitializationId 'synthetic-init-0001' -DatabaseSourceCommit $source `
        -ModelJson ((Model)|ConvertTo-Json -Depth 8 -Compress) -SubjectId $subject -NormalizedEmail 'SYNTHETIC@EXAMPLE.TEST' `
        -IdempotencyKey 'abc' -ManifestJson $manifest -SourceCommit $source
}
function RlsFacts {
    return [ordered]@{
        schema='zeka-m1-auth-rls-metadata-admission/v1';source=$source
        manifestSha256='6543c54a1d7ab5539ef321af8e81c0a5a0692f3378912a5926fd159432e68ad6'
        metadataAdmission=$true;readOnly=$true;runtimeRoleAdmitted=$true
        sevenProvisioningTablesExplicitlyExcluded=$true;contextFunctionAdmitted=$true
        manifestSchemaVersion=19;expectedTables=42;actualTables=42;expectedProtectedTables=23;actualPolicies=23
        tableFailures=@();organisationColumnFailures=@();policyFailures=@();prohibitedRelationFailures=@();topologyFailures=@()
        contextFunctionDefinitionSha256=@('be88914966acd2f06292da857c60c6c9a98dc7f37fd3210f99d9c031f9f2341b')
        migrationHistoryData='NOT_RUN_RUNTIME_DENIED';fullManifestVerifier='NOT_RUN';behavioralTenantIsolation='NOT_RUN'
    }
}

Check 'sql_literal_escapes_quote_without_changing_value' {Require ((ConvertTo-OwnerPreflightSqlLiteral "O'Neil") -ceq "'O''Neil'")}
Check 'sql_literal_empty_value_remains_literal' {Require ((ConvertTo-OwnerPreflightSqlLiteral '') -ceq "''")}
Check 'sql_literal_keeps_injection_text_inside_literal' {Require ((ConvertTo-OwnerPreflightSqlLiteral "x'); SELECT 1; --") -ceq "'x''); SELECT 1; --'")}
Check 'transaction_is_repeatable_read_readonly_and_always_rollback' {
    $t=@(Get-OwnerFirstAdminPreflightTransactionTemplates)
    Require ($t.Count-eq4)
    Require (($t.Id -join '|') -ceq 'owner.transaction.begin-readonly.v1|owner.transaction.statement-timeout.v1|owner.transaction.lock-timeout.v1|owner.transaction.rollback.v1')
    Require (($t.Sql -join '|') -ceq "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY|SET LOCAL statement_timeout='10000ms'|SET LOCAL lock_timeout='1000ms'|ROLLBACK")
}
Check 'closed_eight_query_inventory' {
    $expected=@('owner.identity.v1','owner.schema-presence.v1','owner.schema-shape.v1','owner.privileges.v1','owner.catalogue.v1','owner.existing-identity.v1','owner.idempotency.v1','owner.rls-metadata.v1')
    $actual=@(Get-OwnerFirstAdminPreflightQueryIds)
    Require ($actual.Count-eq8-and@($actual|Sort-Object -Unique).Count-eq8-and@(Compare-Object $expected $actual -CaseSensitive).Count-eq0)
}
foreach($id in Get-OwnerFirstAdminPreflightQueryIds){$queryId=$id
    Check ('query_is_fully_bound_metadata_only_'+$id) {
        $sql=Query $queryId
        Require ($sql -is [string]-and!([string]::IsNullOrWhiteSpace($sql)))
        Require ($sql -cnotmatch '__[A-Z_]+__')
        Require ($sql -notmatch '(?im)^\s*(GRANT|REVOKE|CREATE|ALTER|DROP|INSERT|UPDATE|DELETE|TRUNCATE|CALL|DO)\b')
    }
}
Check 'unknown_query_id_refused_by_public_parameter_contract' {Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.unknown.v1'|Out-Null} 'ParameterBindingValidationException'
Check 'identity_binds_exact_role_database_and_bootstrap_marker' {
    $sql=Query 'owner.identity.v1'
    foreach($literal in @("session_user='zeka_auth_runtime'","current_user='zeka_auth_runtime'","current_database()='zeka_m1_auth'","current_setting('transaction_read_only')='on'",('zeka-m1-database/v1:synthetic-init-0001:auth:'+$source),'NOT r.rolbypassrls','NOT r.rolinherit','NOT r.rolsuper')){Require ($sql.Contains($literal,[StringComparison]::Ordinal))}
}
foreach($bad in @('',('x'*15),('x'*129),'synthetic/init/0001',"synthetic-init-0001`n",'synthetic init 0001')){$invalid=$bad
    Check ('bootstrap_initialization_refused_'+$checks.Count) {Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.identity.v1' -InitializationId $invalid -DatabaseSourceCommit $source|Out-Null} 'OWNER_BOOTSTRAP_BINDING_REJECTED'
}
foreach($bad in @('',('a'*39),('A'*40),('g'*40),($source+"`n"))){$invalid=$bad
    Check ('bootstrap_source_refused_'+$checks.Count) {Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.identity.v1' -InitializationId 'synthetic-init-0001' -DatabaseSourceCommit $invalid|Out-Null} 'OWNER_BOOTSTRAP_BINDING_REJECTED'
}
foreach($id in @('owner.schema-presence.v1','owner.schema-shape.v1')){$queryId=$id
    Check ('model_encoded_losslessly_not_interpolated_'+$id) {
        $input=(Model)|ConvertTo-Json -Depth 8 -Compress
        $sql=Get-OwnerFirstAdminPreflightQuery -QueryId $queryId -ModelJson $input
        $encoded=[regex]::Match($sql,"decode\('(?<bytes>[A-Za-z0-9+/=]+)','base64'\)").Groups['bytes'].Value
        Require ($encoded.Length-gt0-and[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))-ceq$input)
        Require ($sql.Contains("format('%I.%I',s,n)",[StringComparison]::Ordinal))
    }
    foreach($bad in @('', ' ', '{', '{}', '{"tables":"invalid"}', ('x'*1048577))){$invalid=$bad
        Check ('model_shape_refused_'+$id+'_'+$checks.Count) {Get-OwnerFirstAdminPreflightQuery -QueryId $queryId -ModelJson $invalid|Out-Null} 'OWNER_MODEL_INPUT_REJECTED'
    }
    foreach($mutation in @('missing','duplicate','unknown','wrong_case','wrong_schema','nonstring')){$change=$mutation
        Check ('model_table_boundary_refused_'+$id+'_'+$mutation) {
            $m=Model
            switch($change){
                missing {$m.tables=@($m.tables|Select-Object -Skip 1)}
                duplicate {$m.tables[0].name=$m.tables[1].name}
                unknown {$m.tables[0].name='UnadmittedTable'}
                wrong_case {$m.tables[0].name='aspnetusers'}
                wrong_schema {$m.tables[0].schema='other'}
                nonstring {$m.tables[0].name=123}
            }
            Get-OwnerFirstAdminPreflightQuery -QueryId $queryId -ModelJson ($m|ConvertTo-Json -Depth 8 -Compress)|Out-Null
        } 'OWNER_MODEL_INPUT_REJECTED'
    }
}
Check 'schema_shape_checks_columns_primary_keys_and_unique_indexes' {
    $sql=Query 'owner.schema-shape.v1'
    foreach($token in @('columnMismatches','missingPrimaryKeys','missingUniqueIndexes','a.attnotnull','k.convalidated','i.indisunique','i.indisvalid',"'structuralOnly',true")){Require ($sql.Contains($token,[StringComparison]::Ordinal))}
}
Check 'privileges_delegates_to_the_accepted_factory_exactly' {Require ((Query 'owner.privileges.v1') -ceq (Get-OwnerFirstAdminPrivilegeQuery))}
Check 'catalogue_requires_the_exact_single_system_owner' { $sql=Query 'owner.catalogue.v1';foreach($token in @('d8eceeeb-b796-4d77-92a5-6a11fab10a01',"count(*)=1",'"Code"=''Owner''','"IsSystem"=true')){Require ($sql.Contains($token,[StringComparison]::Ordinal))} }
foreach($id in @('owner.existing-identity.v1','owner.idempotency.v1')){$queryId=$id
    foreach($bad in @('','00000000-0000-0000-0000-000000000000','aabbccdd111122223333444455556666','not-a-guid')){$invalid=$bad
        Check ('subject_refused_'+$id+'_'+$checks.Count) {Get-OwnerFirstAdminPreflightQuery -QueryId $queryId -SubjectId $invalid|Out-Null} 'OWNER_SUBJECT_INPUT_REJECTED'
    }
}
Check 'existing_identity_escapes_email_and_canonicalizes_subject' {
    $sql=Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.existing-identity.v1' -SubjectId $subject.ToUpperInvariant() -NormalizedEmail "O'NEIL@EXAMPLE.TEST"
    Require ($sql.Contains("'O''NEIL@EXAMPLE.TEST'",[StringComparison]::Ordinal)-and$sql.Contains($subject,[StringComparison]::Ordinal))
    Require ($sql.Contains('"Status"=2 AND "EmailConfirmed"=true',[StringComparison]::Ordinal))
}
foreach($bad in @('', 'synthetic@example.test', 'UPPER@EXAMPLE.TEST ', "UPPER@EXAMPLE.TEST'; SELECT 1; --",('A'*257))){$invalid=$bad
    Check ('normalized_email_refused_'+$checks.Count) {Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.existing-identity.v1' -SubjectId $subject -NormalizedEmail $invalid|Out-Null} 'OWNER_EMAIL_INPUT_REJECTED'
}
Check 'idempotency_uses_known_sha256_vector_not_raw_key' {
    $sql=Query 'owner.idempotency.v1'
    Require ($sql.Contains('BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD',[StringComparison]::Ordinal))
    Require (!$sql.Contains("'abc'",[StringComparison]::Ordinal)-and$sql.Contains('organisation.provisioning/v1',[StringComparison]::Ordinal))
}
Check 'idempotency_maximum_length_is_accepted_without_raw_interpolation' { $sql=Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.idempotency.v1' -SubjectId $subject -IdempotencyKey ('x'*200);Require (!$sql.Contains(('x'*200),[StringComparison]::Ordinal)) }
foreach($bad in @('', ' ', ('x'*201))){$invalid=$bad
    Check ('idempotency_length_refused_'+$checks.Count) {Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.idempotency.v1' -SubjectId $subject -IdempotencyKey $invalid|Out-Null} 'OWNER_IDEMPOTENCY_INPUT_REJECTED'
}
foreach($code in @((0..31)+127)){$character=[char]$code
    Check ('idempotency_control_character_refused_'+$code) {Get-OwnerFirstAdminPreflightQuery -QueryId 'owner.idempotency.v1' -SubjectId $subject -IdempotencyKey ('x'+$character+'y')|Out-Null} 'OWNER_IDEMPOTENCY_INPUT_REJECTED'
}

Check 'rls_query_delegation_preserves_exact_manifest_and_source' {Require ((Query 'owner.rls-metadata.v1') -ceq (Get-OwnerFirstAdminRlsMetadataQuery -ManifestJson $manifest -SourceCommit $source))}
Check 'rls_query_is_exact_bound_metadata_with_honest_limitations' {
    $sql=Get-OwnerFirstAdminRlsMetadataQuery -ManifestJson $manifest -SourceCommit $source
    $encoded=[regex]::Match($sql,"decode\('(?<bytes>[A-Za-z0-9+/=]+)','base64'\)").Groups['bytes'].Value
    Require ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)) -ceq $manifest)
    foreach($token in @(("'source','"+$source+"'"),'::text[] AS roles','pg_catalog.pg_policy','p.polroles',"'migrationHistoryData','NOT_RUN_RUNTIME_DENIED'","'fullManifestVerifier','NOT_RUN'","'behavioralTenantIsolation','NOT_RUN'")){Require ($sql.Contains($token,[StringComparison]::Ordinal))}
    Require (@([regex]::Matches($sql,'::text\[\] AS roles')).Count-eq2)
}
foreach($bad in @('',('a'*39),('A'*40),('g'*40),($source+"`n"))){$invalid=$bad
    Check ('rls_source_refused_'+$checks.Count) {Get-OwnerFirstAdminRlsMetadataQuery -ManifestJson $manifest -SourceCommit $invalid|Out-Null} $(if($bad-eq''){'ParameterBindingValidationException'}else{'OWNER_RLS_BINDING_REJECTED'})
}
foreach($bad in @(' ',('x'*1048577))){$invalid=$bad
    Check ('rls_manifest_boundary_refused_'+$checks.Count) {Get-OwnerFirstAdminRlsMetadataQuery -ManifestJson $invalid -SourceCommit $source|Out-Null} 'OWNER_RLS_BINDING_REJECTED'
}
Check 'rls_manifest_any_byte_change_refused' {Get-OwnerFirstAdminRlsMetadataQuery -ManifestJson ($manifest+' ') -SourceCommit $source|Out-Null} 'OWNER_RLS_MANIFEST_BINDING_REJECTED'
Check 'rls_facts_accept_exact_synthetic_metadata' {Assert-OwnerFirstAdminRlsMetadataFacts -Facts (RlsFacts) -SourceCommit $source}
Check 'rls_facts_accept_actual_json_types_and_int64_counts' {$f=(RlsFacts)|ConvertTo-Json -Depth 8|ConvertFrom-Json;foreach($name in @('manifestSchemaVersion','expectedTables','actualTables','expectedProtectedTables','actualPolicies')){$f.$name=[long]$f.$name};Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source}
foreach($name in @('metadataAdmission','readOnly','runtimeRoleAdmitted','sevenProvisioningTablesExplicitlyExcluded','contextFunctionAdmitted')){$field=$name
    foreach($bad in @($false,'true',1,$null)){$invalid=$bad
        Check ('rls_required_boolean_'+$name+'_'+$checks.Count) {$f=RlsFacts;$f[$field]=$invalid;Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
    }
}
foreach($name in @('schema','source','manifestSha256','migrationHistoryData','fullManifestVerifier','behavioralTenantIsolation')){$field=$name
    Check ('rls_required_string_type_'+$name) {$f=RlsFacts;$f[$field]=123;Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
    Check ('rls_exact_string_identity_'+$name) {$f=RlsFacts;$f[$field]='UNADMITTED';Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
}
foreach($name in @('manifestSchemaVersion','expectedTables','actualTables','expectedProtectedTables','actualPolicies')){$field=$name
    foreach($bad in @('42',[double]42,-1,$null)){$invalid=$bad
        Check ('rls_count_type_or_value_'+$name+'_'+$checks.Count) {$f=RlsFacts;$f[$field]=$invalid;Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
    }
}
foreach($name in @('tableFailures','organisationColumnFailures','policyFailures','prohibitedRelationFailures','topologyFailures')){$field=$name
    foreach($bad in @('',123,$null)){$invalid=$bad
        Check ('rls_failure_inventory_type_'+$name+'_'+$checks.Count) {$f=RlsFacts;$f[$field]=$invalid;Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
    }
    Check ('rls_nonempty_failure_inventory_'+$name) {$f=RlsFacts;$f[$field]=@('synthetic.unexpected');Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
}
foreach($name in @('empty','duplicate','wrong_hash','nonstring','scalar')){$change=$name
    Check ('rls_context_function_definition_'+$name) {$f=RlsFacts;switch($change){empty{$f.contextFunctionDefinitionSha256=@()};duplicate{$f.contextFunctionDefinitionSha256=@($f.contextFunctionDefinitionSha256[0],$f.contextFunctionDefinitionSha256[0])};wrong_hash{$f.contextFunctionDefinitionSha256=@('b'*64)};nonstring{$f.contextFunctionDefinitionSha256=@(123)};scalar{$f.contextFunctionDefinitionSha256=$f.contextFunctionDefinitionSha256[0]}};Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
}
foreach($name in @('fullManifestVerifier','behavioralTenantIsolation','migrationHistoryData')){$field=$name
    Check ('rls_metadata_cannot_claim_behavioral_or_full_verification_'+$name) {$f=RlsFacts;$f[$field]='PASS';Assert-OwnerFirstAdminRlsMetadataFacts -Facts $f -SourceCommit $source} 'OWNER_RLS_METADATA_REJECTED'
}
Check 'rls_fact_source_argument_must_be_exact_commit_format' {Assert-OwnerFirstAdminRlsMetadataFacts -Facts (RlsFacts) -SourceCommit 'not-a-commit'} 'OWNER_RLS_METADATA_REJECTED'
Check 'rls_null_facts_fail_parameter_admission' {Assert-OwnerFirstAdminRlsMetadataFacts -Facts $null -SourceCommit $source} 'ParameterBindingValidationException'
$result=[ordered]@{schema='zeka-owner-query-metadata-unit-fixtures/v1';status='PASS';count=$checks.Count;checks=$checks.ToArray();sqlExecuted=$false;runtimeOperationsExecuted=$false;behavioralRlsProof='NOT_RUN'}
if($ReportFile){$path=[IO.Path]::GetFullPath($ReportFile);$stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($result|ConvertTo-Json -Depth 8));$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}
$result|ConvertTo-Json -Depth 8 -Compress
