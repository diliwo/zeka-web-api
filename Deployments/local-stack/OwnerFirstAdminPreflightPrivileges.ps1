# Definitions only: this producer returns metadata SQL and never connects or changes privileges.
function Get-OwnerFirstAdminPrivilegeQuery {
return @'
WITH relations(name) AS (VALUES ('AspNetUsers'),('IdempotencyRecords'),('Organisations'),('OrganisationMemberships'),('PermissionSets'),('AuditEntries'),('OutboxMessages')),
required_privileges(name, privilege) AS (VALUES
 ('AspNetUsers','SELECT'),('AspNetUsers','INSERT'),('AspNetUsers','UPDATE'),
 ('IdempotencyRecords','SELECT'),('IdempotencyRecords','INSERT'),('IdempotencyRecords','UPDATE'),
 ('Organisations','SELECT'),('OrganisationMemberships','SELECT'),('OrganisationMemberships','INSERT'),
 ('PermissionSets','SELECT'),('AuditEntries','INSERT'),('OutboxMessages','INSERT')),
required_columns(name) AS (VALUES ('Id'),('DisplayName'),('OwnerUserId'),('Status'),('Slug'),('CreatedAtUtc'),('UpdatedAtUtc'),('ConcurrencyVersion'),('TimeZoneId'),('TimeZoneConfigVersion')),
relation_facts AS (
 SELECT r.name,c.oid IS NOT NULL AS present,n.nspname='public' AS public_schema,
 COALESCE(pg_get_userbyid(c.relowner)='zeka_auth_owner',false) AS owner_matches
 FROM relations r LEFT JOIN pg_namespace n ON n.nspname='public'
 LEFT JOIN pg_class c ON c.relnamespace=n.oid AND c.relname=r.name),
privilege_facts AS (
 SELECT name,privilege,COALESCE(has_table_privilege(current_user,to_regclass(format('%I.%I','public',name)),privilege),false) AS allowed FROM required_privileges),
column_facts AS (
 SELECT r.name,a.attnum IS NOT NULL AS present,
 CASE WHEN a.attnum IS NULL THEN false ELSE has_column_privilege(current_user,c.oid,a.attnum,'INSERT') END AS insert_allowed,
 CASE WHEN a.attnum IS NULL THEN false ELSE NOT has_column_privilege(current_user,c.oid,a.attnum,'UPDATE') END AS update_denied
 FROM required_columns r LEFT JOIN pg_namespace n ON n.nspname='public'
 LEFT JOIN pg_class c ON c.relnamespace=n.oid AND c.relname='Organisations'
 LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attname=r.name AND a.attnum>0 AND NOT a.attisdropped)
SELECT json_build_object(
 'schema','zeka-m1-owner-preflight-privileges/v1',
 'identity',json_build_object('databaseMatches',current_database()='zeka_m1_auth','currentRoleMatches',current_user='zeka_auth_runtime',
 'sessionRoleMatches',session_user='zeka_auth_runtime','schemaExists',EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='public'),
 'schemaUsage',COALESCE(has_schema_privilege(current_user,to_regnamespace('public'),'USAGE'),false)),
 'relations',(SELECT json_agg(json_build_object('name',name,'present',present,'publicSchema',COALESCE(public_schema,false),'ownerMatches',owner_matches) ORDER BY name) FROM relation_facts),
 'otherTablePrivileges',(SELECT json_agg(json_build_object('name',name,'privilege',privilege,'allowed',allowed) ORDER BY name,privilege) FROM privilege_facts),
 'otherTablePrivilegesAllowed',(SELECT COALESCE(bool_and(allowed),false) FROM privilege_facts),
 'organisationsTableInsertDenied',NOT COALESCE(has_table_privilege(current_user,to_regclass('public."Organisations"'),'INSERT'),false),
 'organisationsTableUpdateDenied',NOT COALESCE(has_table_privilege(current_user,to_regclass('public."Organisations"'),'UPDATE'),false),
 'protectedOrganisationUpdates',(SELECT json_agg(json_build_object('name',name,'present',present,'updateDenied',update_denied) ORDER BY name) FROM column_facts WHERE name IN ('TimeZoneId','TimeZoneConfigVersion')),
 'organisationInsertColumns',(SELECT json_agg(json_build_object('name',name,'present',present,'insertAllowed',insert_allowed) ORDER BY name) FROM column_facts));
'@
}
function Stop-OwnerFirstAdminPrivilegeFacts {throw [InvalidOperationException]::new('OWNER_PREFLIGHT_PRIVILEGES_REJECTED')}
function Assert-OwnerPrivilegeShape($Value,[string[]]$Fields) {
 if($Value-is[Collections.IDictionary]){$keys=@($Value.Keys)}elseif($Value-is[pscustomobject]){$keys=@($Value.PSObject.Properties.Name)}else{Stop-OwnerFirstAdminPrivilegeFacts}
 if($keys.Count-ne$Fields.Count){Stop-OwnerFirstAdminPrivilegeFacts}
 foreach($key in $keys){if($key-cnotin$Fields){Stop-OwnerFirstAdminPrivilegeFacts}}
}
function Assert-OwnerPrivilegeTrue($Value) {if($Value-isnot[bool]-or!$Value){Stop-OwnerFirstAdminPrivilegeFacts}}
function Assert-OwnerFirstAdminPrivilegeFacts($Facts) {
 Assert-OwnerPrivilegeShape $Facts @('schema','identity','relations','otherTablePrivileges','otherTablePrivilegesAllowed','organisationsTableInsertDenied','organisationsTableUpdateDenied','protectedOrganisationUpdates','organisationInsertColumns')
 if($Facts.schema-isnot[string]-or$Facts.schema-cne'zeka-m1-owner-preflight-privileges/v1'){Stop-OwnerFirstAdminPrivilegeFacts}
 $fields=@('databaseMatches','currentRoleMatches','sessionRoleMatches','schemaExists','schemaUsage');Assert-OwnerPrivilegeShape $Facts.identity $fields
 foreach($field in $fields){Assert-OwnerPrivilegeTrue $Facts.identity.$field}
 $relations=@('AspNetUsers','IdempotencyRecords','Organisations','OrganisationMemberships','PermissionSets','AuditEntries','OutboxMessages')
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 if($Facts.relations-isnot[Array]-or$Facts.relations.Count-ne7){Stop-OwnerFirstAdminPrivilegeFacts}
 foreach($r in $Facts.relations){
  Assert-OwnerPrivilegeShape $r @('name','present','publicSchema','ownerMatches')
  if($r.name-isnot[string]-or$r.name-cnotin$relations-or!$seen.Add($r.name)){Stop-OwnerFirstAdminPrivilegeFacts}
  foreach($field in @('present','publicSchema','ownerMatches')){Assert-OwnerPrivilegeTrue $r.$field}
 }
 $pairs=@('AspNetUsers:SELECT','AspNetUsers:INSERT','AspNetUsers:UPDATE','IdempotencyRecords:SELECT','IdempotencyRecords:INSERT','IdempotencyRecords:UPDATE',
 'Organisations:SELECT','OrganisationMemberships:SELECT','OrganisationMemberships:INSERT','PermissionSets:SELECT','AuditEntries:INSERT','OutboxMessages:INSERT')
 $seen.Clear();if($Facts.otherTablePrivileges-isnot[Array]-or$Facts.otherTablePrivileges.Count-ne12){Stop-OwnerFirstAdminPrivilegeFacts}
 foreach($p in $Facts.otherTablePrivileges){
  Assert-OwnerPrivilegeShape $p @('name','privilege','allowed')
  if($p.name-isnot[string]-or$p.privilege-isnot[string]){Stop-OwnerFirstAdminPrivilegeFacts}
  $key=$p.name+':'+$p.privilege;if($key-cnotin$pairs-or!$seen.Add($key)){Stop-OwnerFirstAdminPrivilegeFacts};Assert-OwnerPrivilegeTrue $p.allowed
 }
 Assert-OwnerPrivilegeTrue $Facts.otherTablePrivilegesAllowed
 # Whole-table INSERT would also satisfy column checks, so it must independently remain denied.
 Assert-OwnerPrivilegeTrue $Facts.organisationsTableInsertDenied
 Assert-OwnerPrivilegeTrue $Facts.organisationsTableUpdateDenied
 $protected=@('TimeZoneId','TimeZoneConfigVersion');$seen.Clear()
 if($Facts.protectedOrganisationUpdates-isnot[Array]-or$Facts.protectedOrganisationUpdates.Count-ne2){Stop-OwnerFirstAdminPrivilegeFacts}
 foreach($c in $Facts.protectedOrganisationUpdates){
  Assert-OwnerPrivilegeShape $c @('name','present','updateDenied')
  if($c.name-isnot[string]-or$c.name-cnotin$protected-or!$seen.Add($c.name)){Stop-OwnerFirstAdminPrivilegeFacts}
  Assert-OwnerPrivilegeTrue $c.present;Assert-OwnerPrivilegeTrue $c.updateDenied
 }
 $allowed=@('Id','DisplayName','OwnerUserId','Status','Slug','CreatedAtUtc','UpdatedAtUtc','ConcurrencyVersion');$denied=@('TimeZoneId','TimeZoneConfigVersion');$seen.Clear()
 if($Facts.organisationInsertColumns-isnot[Array]-or$Facts.organisationInsertColumns.Count-ne10){Stop-OwnerFirstAdminPrivilegeFacts}
 foreach($c in $Facts.organisationInsertColumns){
  Assert-OwnerPrivilegeShape $c @('name','present','insertAllowed')
  if($c.name-isnot[string]-or$c.name-cnotin($allowed+$denied)-or!$seen.Add($c.name)){Stop-OwnerFirstAdminPrivilegeFacts}
  Assert-OwnerPrivilegeTrue $c.present
  if($c.insertAllowed-isnot[bool]-or$c.insertAllowed-ne($c.name-cin$allowed)){Stop-OwnerFirstAdminPrivilegeFacts}
 }
}
