[CmdletBinding()]
param([string]$ReportFile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../../Deployments/local-stack/W3-NativeDiagnostics.ps1')
$checks=[Collections.Generic.List[object]]::new()
$canary=[Guid]::NewGuid().ToString('N')
function Check([string]$Name,[scriptblock]$Action){&$Action;$checks.Add([ordered]@{name=$Name;passed=$true})}
function NoLeak($Value){if(($Value|ConvertTo-Json -Depth 8 -Compress).Contains($canary,[StringComparison]::Ordinal)){throw 'SYNTHETIC_DETAIL_ESCAPED'}}
Check 'typed_fields_preserved' {
 $r=ConvertFrom-SafePostgresDiagnostic '{"sqlState":"42883","message":"Synthetic operator mismatch.","hint":"Add explicit casts.","position":17}'
 if($r.sqlState-cne'42883'-or$r.message-cne'Synthetic operator mismatch.'-or$r.hint-cne'Add explicit casts.'-or$r.position-ne17){throw 'FOUR_FIELDS_NOT_PRESERVED'}
}
Check 'detail_extra_field_rejected_without_echo' {
 $value=@{sqlState='23505';message='Synthetic duplicate rejection.';hint=$null;position=0;detail=$canary}|ConvertTo-Json -Compress
 try{ConvertFrom-SafePostgresDiagnostic $value|Out-Null;throw 'EXPECTED_REFUSAL'}catch{if($_.Exception.Message-cne'POSTGRES_DIAGNOSTIC_REJECTED'){throw 'UNSAFE_REFUSAL'}}
}
Check 'duplicate_field_rejected' {
 try{ConvertFrom-SafePostgresDiagnostic '{"sqlState":"23505","message":"one","message":"two","hint":null,"position":0}'|Out-Null;throw 'EXPECTED_REFUSAL'}catch{if($_.Exception.Message-cne'POSTGRES_DIAGNOSTIC_REJECTED'){throw 'UNSAFE_REFUSAL'}}
}
Check 'detail_and_forged_hint_suffix_cannot_escape_legacy_projection' {
 $text="ERROR:  23505: Synthetic duplicate rejection.`nDETAIL: Key (Id)=("+$canary+") already exists.`nHINT: "+$canary+"`nPOSITION: 9`nCONTEXT: "+$canary
 $r=ConvertFrom-LegacyPsqlDiagnostic $text;NoLeak $r
 if($r.diagnostic.sqlState-cne'23505'-or$r.diagnostic.message-cne'Synthetic duplicate rejection.'-or$null-ne$r.diagnostic.hint-or$null-ne$r.diagnostic.position){throw 'LEGACY_AMBIGUITY_NOT_WITHHELD'}
 NoLeak (ConvertTo-W3SafeText $text)
 NoLeak (ConvertTo-W3SafeNativeError $text 'docker' @('exec','fixture','psql'))
}
Check 'legacy_numeric_position_not_fabricated_from_caret' {
 $r=ConvertFrom-LegacyPsqlDiagnostic "ERROR:  42883: Synthetic operator mismatch.`nLINE 1: fixture`n        ^"
 if($null-ne$r.diagnostic.position){throw 'FABRICATED_POSITION'}
}
Check 'legacy_available_hint_and_position_preserved_before_untrusted_fields' {
 $r=ConvertFrom-LegacyPsqlDiagnostic "ERROR:  42883: Synthetic operator mismatch.`nHINT: Add explicit casts.`nPOSITION: 17"
 if($r.diagnostic.hint-cne'Add explicit casts.'-or$r.diagnostic.position-ne17){throw 'AVAILABLE_FIELDS_LOST'}
}
Check 'unknown_psql_stderr_withheld' {NoLeak (ConvertTo-W3SafeNativeError $canary 'psql' @())}
Check 'known_secret_masking_retained_for_typed_fields' {
 Register-W3Secret $canary
 $safe=ConvertTo-W3SafeText ('Approved message '+$canary) -StructuredPostgresField
 NoLeak $safe
 if(!$safe.Contains('[REDACTED]',[StringComparison]::Ordinal)){throw 'SECRET_MASKING_REMOVED'}
}
Check 'nonpostgres_error_semantics_retained' {
 if((ConvertTo-W3SafeNativeError 'No such container: fixture' 'docker' @('inspect','fixture'))-cne'No such container: fixture'){throw 'NONPOSTGRES_NATIVE_ERROR_CHANGED'}
}
$invalid=[Collections.Generic.List[string]]::new()
foreach($json in @('null','[]','true','17','"text"')){$invalid.Add($json)}
foreach($case in @(@('sqlState',$null),@('sqlState',23505),@('sqlState','abcde'),@('sqlState','1234'),@('sqlState','123456'),@('sqlState','42-83'),
 @('message',$null),@('message',17),@('message',$false),@('message',@('text')),@('hint',17),@('hint',$false),@('hint',@('text')),
 @('position',$null),@('position',-1),@('position','17'),@('position',$true),@('position',1.25),@('position',2147483648L))){
 $fields=[ordered]@{sqlState='42883';message='Synthetic message.';hint=$null;position=17};$fields[$case[0]]=$case[1];$invalid.Add(($fields|ConvertTo-Json -Compress))
}
foreach($field in @('sqlState','message','hint','position')){$fields=[ordered]@{sqlState='42883';message='Synthetic message.';hint=$null;position=17};$fields.Remove($field);$invalid.Add(($fields|ConvertTo-Json -Compress))}
$invalid.Add('{"sqlState":"42883","message":"Synthetic.","hint":null,"position":1.0}')
foreach($json in $invalid){
 $inputJson=$json
 Check ('typed_malformed_field_'+$checks.Count) {
  try{ConvertFrom-SafePostgresDiagnostic $inputJson|Out-Null;throw 'EXPECTED_REFUSAL'}catch{if($_.Exception.Message-cne'POSTGRES_DIAGNOSTIC_REJECTED'){throw 'NONUNIFORM_TYPED_REFUSAL'}}
 }
}
Check 'typed_zero_position_and_null_hint_valid' {$r=ConvertFrom-SafePostgresDiagnostic '{"sqlState":"23505","message":"Synthetic.","hint":null,"position":0}';if($r.position-ne0-or$null-ne$r.hint){throw 'VALID_TYPED_FIELDS_CHANGED'}}
foreach($shape in @('credential','bearer','pat','connection','email','windows','unix','unc','literal','identifier','escaped-literal','multiline-literal','multiline-identifier')){
 $kind=$shape
 Check ('sensitive_shape_'+$shape) {
  $sensitive=[Guid]::NewGuid().ToString('N')
  $payload=switch($kind){
   'credential' {'Password='+$sensitive} 'bearer' {'Bearer '+$sensitive} 'pat' {'ghp_'+$sensitive}
   'connection' {'Host=fixture;Database=fixture;Username=fixture;Password='+$sensitive}
   'email' {'synthetic.'+$sensitive+'@example.invalid'} 'windows' {'C:\private\'+$sensitive}
   'unix' {'/private/'+$sensitive} 'unc' {'\\private\'+$sensitive}
   'literal' {"'"+$sensitive+"'"} 'identifier' {'"'+$sensitive+'"'}
   'escaped-literal' {"'before''"+$sensitive+"'"}
   'multiline-literal' {"'first`n"+$sensitive+"'"} 'multiline-identifier' {"`"first`n"+$sensitive+'"'}
  }
  $inputJson=@{sqlState='42883';message=('Synthetic '+$payload);hint=$payload;position=17}|ConvertTo-Json -Compress
  $r=ConvertFrom-SafePostgresDiagnostic $inputJson
  if(($r|ConvertTo-Json -Compress).Contains($sensitive,[StringComparison]::Ordinal)){throw 'SENSITIVE_FIELD_ESCAPED'}
  if($r.sqlState-cne'42883'-or$r.position-ne17){throw 'NON_TEXT_FIELDS_CHANGED'}
 }
}
Check 'operator_types_and_multiline_hint_preserved' {
 $message='operator does not exist: name[] = text[]';$hint="No operator matches the given name and argument types.`nYou might need to add explicit type casts."
 $r=ConvertFrom-SafePostgresDiagnostic (@{sqlState='42883';message=$message;hint=$hint;position=17}|ConvertTo-Json -Compress)
 if($r.message-cne$message-or$r.hint-cne$hint){throw 'USEFUL_OPERATOR_DIAGNOSTIC_LOST'}
}
foreach($kind in @('single-quote','double-quote','path','backslash','newline','semicolon')){
 $shape=$kind
 Check ('registered_secret_fragments_'+$kind) {
  $prefix=[Guid]::NewGuid().ToString('N');$suffix=[Guid]::NewGuid().ToString('N')
  $separator=switch($shape){'single-quote' {"'"} 'double-quote' {'"'} 'path' {'/private/'} 'backslash' {'\'} 'newline' {[Environment]::NewLine} 'semicolon' {';'}}
  $secret=$prefix+$separator+$suffix
  Register-W3Secret $prefix;Register-W3Secret $secret
  $value=@{sqlState='23505';message=('Synthetic '+$secret);hint=$secret;position=0}|ConvertTo-Json -Compress
  $output=ConvertFrom-SafePostgresDiagnostic $value|ConvertTo-Json -Compress
  if($output.Contains($prefix,[StringComparison]::Ordinal)-or$output.Contains($suffix,[StringComparison]::Ordinal)){throw 'REGISTERED_SECRET_FRAGMENT_ESCAPED'}
 }
}
Check 'typed_state_final_lf_rejected' {
 $value=@{sqlState="42883`n";message='Synthetic.';hint=$null;position=0}|ConvertTo-Json -Compress
 try{ConvertFrom-SafePostgresDiagnostic $value|Out-Null;throw 'EXPECTED_REFUSAL'}catch{if($_.Exception.Message-cne'POSTGRES_DIAGNOSTIC_REJECTED'){throw 'NONUNIFORM_TYPED_REFUSAL'}}
}
$result=[ordered]@{schema='zeka-safe-postgres-fixture/v1';status='PASS';count=$checks.Count;checks=$checks.ToArray();realDatabaseAccess=$false;runtimeOperationsExecuted=$false}
if($ReportFile){$path=[IO.Path]::GetFullPath($ReportFile);$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($result|ConvertTo-Json -Depth 8));$stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}
$result|ConvertTo-Json -Depth 8 -Compress
