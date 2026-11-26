[CmdletBinding()]
param([string]$ReportFile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../../Deployments/local-stack/OwnerFirstAdminPreflightPrivileges.ps1')
# Independently fixed accepted contract; pure synthetic metadata only, no SQL or runtime operations.
$checks=[Collections.Generic.List[object]]::new()
function Facts {
 $relations=@('AspNetUsers','IdempotencyRecords','Organisations','OrganisationMemberships','PermissionSets','AuditEntries','OutboxMessages')
 $pairs=@('AspNetUsers:SELECT','AspNetUsers:INSERT','AspNetUsers:UPDATE','IdempotencyRecords:SELECT','IdempotencyRecords:INSERT','IdempotencyRecords:UPDATE',
 'Organisations:SELECT','OrganisationMemberships:SELECT','OrganisationMemberships:INSERT','PermissionSets:SELECT','AuditEntries:INSERT','OutboxMessages:INSERT')
 $allowed=@('Id','DisplayName','OwnerUserId','Status','Slug','CreatedAtUtc','UpdatedAtUtc','ConcurrencyVersion')
 return [ordered]@{schema='zeka-m1-owner-preflight-privileges/v1';identity=[ordered]@{databaseMatches=$true;currentRoleMatches=$true;sessionRoleMatches=$true;schemaExists=$true;schemaUsage=$true};
 relations=@($relations|ForEach-Object {[ordered]@{name=$_;present=$true;publicSchema=$true;ownerMatches=$true}});
 otherTablePrivileges=@($pairs|ForEach-Object {$parts=$_.Split(':');[ordered]@{name=$parts[0];privilege=$parts[1];allowed=$true}});
 otherTablePrivilegesAllowed=$true;organisationsTableInsertDenied=$true;organisationsTableUpdateDenied=$true;
 protectedOrganisationUpdates=@(@('TimeZoneId','TimeZoneConfigVersion')|ForEach-Object {[ordered]@{name=$_;present=$true;updateDenied=$true}});
 organisationInsertColumns=@(($allowed+@('TimeZoneId','TimeZoneConfigVersion'))|ForEach-Object {[ordered]@{name=$_;present=$true;insertAllowed=($_-cin$allowed)}})}
}
function Check([string]$Name,[scriptblock]$Action,[bool]$Deny=$true) {
 try {&$Action;if($Deny){throw 'EXPECTED_DENIAL_MISSING'}}
 catch {if(!$Deny-or$_.Exception.Message-cne'OWNER_PREFLIGHT_PRIVILEGES_REJECTED'){throw ('FIXTURE_FAILED_'+$Name)}}
 $checks.Add([ordered]@{name=$Name;passed=$true})
}
Check 'exact_ten_column_shape_accepted' {Assert-OwnerFirstAdminPrivilegeFacts (Facts)} $false
Check 'actual_json_booleans_accepted' {$f=(Facts)|ConvertTo-Json -Depth 8|ConvertFrom-Json;Assert-OwnerFirstAdminPrivilegeFacts $f} $false
foreach($name in @('Id','DisplayName','OwnerUserId','Status','Slug','CreatedAtUtc','UpdatedAtUtc','ConcurrencyVersion')){
 $column=$name
 Check ('missing_grant_'+$name) {$f=Facts;($f.organisationInsertColumns|Where-Object name -CEQ $column).insertAllowed=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}
 Check ('missing_column_'+$name) {$f=Facts;($f.organisationInsertColumns|Where-Object name -CEQ $column).present=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}
}
foreach($name in @('TimeZoneId','TimeZoneConfigVersion')){$column=$name;Check ('protected_grant_'+$name) {$f=Facts;($f.organisationInsertColumns|Where-Object name -CEQ $column).insertAllowed=$true;Assert-OwnerFirstAdminPrivilegeFacts $f}}
Check 'whole_table_grant_denied_even_when_all_column_checks_allow' {$f=Facts;$f.organisationsTableInsertDenied=$false;foreach($c in $f.organisationInsertColumns){$c.insertAllowed=$true};Assert-OwnerFirstAdminPrivilegeFacts $f}
# These three UPDATE denials preserve Issue78; they were not present in the old external thirteen-pair predicate.
Check 'whole_table_update_grant_denied' {$f=Facts;$f.organisationsTableUpdateDenied=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}
foreach($i in 0..1){$index=$i;Check ('protected_update_grant_'+$i) {$f=Facts;$f.protectedOrganisationUpdates[$index].updateDenied=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}}
Check 'missing_protected_update_entry' {$f=Facts;$f.protectedOrganisationUpdates=@($f.protectedOrganisationUpdates|Select-Object -First 1);Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'duplicate_protected_update_entry' {$f=Facts;$f.protectedOrganisationUpdates[0]=$f.protectedOrganisationUpdates[1];Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'missing_table_update_fact' {$f=Facts;$f.Remove('organisationsTableUpdateDenied');Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'missing_protected_update_fact' {$f=Facts;$f.protectedOrganisationUpdates[0].Remove('updateDenied');Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'whole_table_grant_independently_denied' {$f=Facts;$f.organisationsTableInsertDenied=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}
foreach($i in 0..11){$index=$i;Check ('other_pair_denied_'+$i) {$f=Facts;$f.otherTablePrivileges[$index].allowed=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}}
foreach($name in @('databaseMatches','currentRoleMatches','sessionRoleMatches','schemaExists','schemaUsage')){$field=$name;Check ('identity_'+$name) {$f=Facts;$f.identity[$field]=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}}
foreach($i in 0..6){foreach($name in @('present','publicSchema','ownerMatches')){$index=$i;$field=$name;Check ('relation_'+$i+'_'+$name) {$f=Facts;$f.relations[$index][$field]=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}}}
foreach($value in @($null,'true',1)){
 $bad=$value
 Check ('nonboolean_identity_'+$checks.Count) {$f=Facts;$f.identity.schemaUsage=$bad;Assert-OwnerFirstAdminPrivilegeFacts $f}
 Check ('nonboolean_column_'+$checks.Count) {$f=Facts;$f.organisationInsertColumns[0].insertAllowed=$bad;Assert-OwnerFirstAdminPrivilegeFacts $f}
 Check ('nonboolean_denial_'+$checks.Count) {$f=Facts;$f.organisationsTableInsertDenied=$bad;Assert-OwnerFirstAdminPrivilegeFacts $f}
 Check ('nonboolean_table_update_'+$checks.Count) {$f=Facts;$f.organisationsTableUpdateDenied=$bad;Assert-OwnerFirstAdminPrivilegeFacts $f}
 Check ('nonboolean_protected_update_'+$checks.Count) {$f=Facts;$f.protectedOrganisationUpdates[0].updateDenied=$bad;Assert-OwnerFirstAdminPrivilegeFacts $f}
 Check ('nonboolean_pair_'+$checks.Count) {$f=Facts;$f.otherTablePrivileges[0].allowed=$bad;Assert-OwnerFirstAdminPrivilegeFacts $f}
}
Check 'missing_identity_fact' {$f=Facts;$f.identity.Remove('schemaUsage');Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'missing_top_fact' {$f=Facts;$f.Remove('otherTablePrivilegesAllowed');Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'null_facts' {Assert-OwnerFirstAdminPrivilegeFacts $null}
Check 'duplicate_column' {$f=Facts;$f.organisationInsertColumns[0].name='DisplayName';Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'missing_array_entry' {$f=Facts;$f.organisationInsertColumns=@($f.organisationInsertColumns|Select-Object -Skip 1);Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'duplicate_pair' {$f=Facts;$f.otherTablePrivileges[0]=$f.otherTablePrivileges[1];Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'duplicate_relation' {$f=Facts;$f.relations[0]=$f.relations[1];Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'malformed_column_shape' {$f=Facts;$f.organisationInsertColumns[0].extra=$true;Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'malformed_pair_shape' {$f=Facts;$f.otherTablePrivileges[0]='invalid';Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'wrong_schema_version' {$f=Facts;$f.schema='unknown';Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'aggregate_false' {$f=Facts;$f.otherTablePrivilegesAllowed=$false;Assert-OwnerFirstAdminPrivilegeFacts $f}
Check 'fixed_metadata_query_contract' {
 $sql=Get-OwnerFirstAdminPrivilegeQuery
 foreach($pair in (Facts).otherTablePrivileges){if(!$sql.Contains("('"+$pair.name+"','"+$pair.privilege+"')",[StringComparison]::Ordinal)){throw 'PAIR_CHANGED'}}
 foreach($c in (Facts).organisationInsertColumns){if(!$sql.Contains("('"+$c.name+"')",[StringComparison]::Ordinal)){throw 'COLUMN_CHANGED'}}
 $pairSection=[regex]::Match($sql,'(?s)required_privileges\(name, privilege\) AS \(VALUES(?<items>.*?)\),\s*required_columns').Groups['items'].Value
 $pairRows=@([regex]::Matches($pairSection,"\('([^']+)','([^']+)'\)"))
 $expected=@((Facts).otherTablePrivileges|ForEach-Object {$_.name+':'+$_.privilege})
 if($pairRows.Count-ne12){throw 'QUERY_PAIR_COUNT'}
 $actual=@($pairRows|ForEach-Object {$_.Groups[1].Value+':'+$_.Groups[2].Value})
 if(@($actual|Sort-Object -Unique).Count-ne12-or@(Compare-Object $expected $actual -CaseSensitive).Count-ne0){throw 'QUERY_PAIR_SET'}
 $columnSection=[regex]::Match($sql,'(?s)required_columns\(name\) AS \(VALUES (?<items>.*?)\),\s*relation_facts').Groups['items'].Value
 $actual=@([regex]::Matches($columnSection,"\('([^']+)'\)")|ForEach-Object {$_.Groups[1].Value})
 $expected=@((Facts).organisationInsertColumns|ForEach-Object {$_.name})
 if($actual.Count-ne10-or@($actual|Sort-Object -Unique).Count-ne10-or@(Compare-Object $expected $actual -CaseSensitive).Count-ne0){throw 'QUERY_COLUMN_SET'}
 foreach($token in @("current_database()='zeka_m1_auth'","current_user='zeka_auth_runtime'","session_user='zeka_auth_runtime'","n.nspname='public'","='zeka_auth_owner'",'bool_and(allowed)',"'organisationsTableInsertDenied',NOT COALESCE(has_table_privilege","'organisationsTableUpdateDenied',NOT COALESCE(has_table_privilege","ELSE NOT has_column_privilege(current_user,c.oid,a.attnum,'UPDATE') END AS update_denied")){
  if(!$sql.Contains($token,[StringComparison]::Ordinal)){throw 'QUERY_CONTRACT_CHANGED'}
 }
 if($sql-cmatch'(?im)^\s*(GRANT|REVOKE|UPDATE|DELETE|INSERT INTO|CREATE|ALTER|DROP)\b'){throw 'NON_METADATA_QUERY'}
} $false
$result=[ordered]@{schema='zeka-owner-preflight-privilege-fixtures/v1';status='PASS';count=$checks.Count;checks=$checks.ToArray();sqlExecuted=$false;runtimeOperationsExecuted=$false}
if($ReportFile){$path=[IO.Path]::GetFullPath($ReportFile);$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($result|ConvertTo-Json -Depth 8));$stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}
$result|ConvertTo-Json -Depth 8 -Compress
