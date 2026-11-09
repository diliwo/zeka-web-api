# Central text policy for approved fields; data fields are never executable SQL.
function Protect-SafePostgresText([string]$Value) {
 # Match complete registered values longest-first while fields are still raw. Generic masking can remove delimiters.
 $known=Get-Variable -Name W3Secrets -Scope Script -ErrorAction SilentlyContinue
 if($known){foreach($secret in @($known.Value|Sort-Object {$_.Length} -Descending)){$Value=$Value.Replace($secret,'[REDACTED]',[StringComparison]::Ordinal)}}
 $rules=@(
  '(?i)(?:host|server|data source)\s*=\s*(?:"[^"]*"|''[^'']*''|[^;\r\n]+)(?:\s*;\s*[a-z_ ]+\s*=\s*(?:"[^"]*"|''[^'']*''|[^;\r\n]+))*',
  '(?i)\b(?:password|passwd|pwd|token|secret|access_token|refresh_token|client_secret|api_key|bearer_token)\b\s*[:=]\s*(?:"[^"]*"|''[^'']*''|[^\s;,\r\n]+)',
  '(?i)\bBearer\s+[^\s;,\r\n]+',
  '(?:gh[pousr]_|github_pat_|sqa_|squ_|sqp_|sk_live_|sk_test_)[A-Za-z0-9_=-]{8,}',
  '[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}',
  '(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}',
  '(?i)[a-z][a-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@[^\s]+',
  '[A-Za-z]:[\\/][^;\r\n]+',
  '\\\\[^;\r\n]+',
  '/(?:[^/\s;,]+/)+[^;\r\n]*',
  '''(?:''''|[^''])*(?:''|$)',
  '"(?:""|[^"])*(?:"|$)')
 try {
  foreach($pattern in $rules){$regex=[regex]::new($pattern,[Text.RegularExpressions.RegexOptions]::CultureInvariant-bor[Text.RegularExpressions.RegexOptions]::NonBacktracking,[TimeSpan]::FromMilliseconds(100));$Value=$regex.Replace($Value,'[REDACTED]')}
  return $Value
 } catch [Text.RegularExpressions.RegexMatchTimeoutException] {return '[POSTGRES_TEXT_WITHHELD]'}
}
# Definitions only. A PostgreSQL diagnostic is a four-field projection, never a serialized exception.
function ConvertFrom-SafePostgresDiagnostic([string]$Json) {
 $document=$null
 try {
  if($Json.Length-gt131072){throw 'REJECTED'}
  $document=[Text.Json.JsonDocument]::Parse($Json)
  $root=$document.RootElement;if($root.ValueKind-ne[Text.Json.JsonValueKind]::Object){throw 'REJECTED'}
  $names=@($root.EnumerateObject()|ForEach-Object {$_.Name})
  if($names.Count-ne4-or@($names|Sort-Object -Unique).Count-ne4-or@($names|Where-Object {$_-cnotin@('sqlState','message','hint','position')}).Count-ne0){throw 'REJECTED'}
  $stateValue=$root.GetProperty('sqlState');$messageValue=$root.GetProperty('message');$hint=$root.GetProperty('hint');$position=$root.GetProperty('position')
  if($stateValue.ValueKind-ne[Text.Json.JsonValueKind]::String-or$messageValue.ValueKind-ne[Text.Json.JsonValueKind]::String-or
     $hint.ValueKind-notin@([Text.Json.JsonValueKind]::Null,[Text.Json.JsonValueKind]::String)-or$position.ValueKind-ne[Text.Json.JsonValueKind]::Number){throw 'REJECTED'}
  $state=$stateValue.GetString();$message=$messageValue.GetString();$number=0
  if($state-cnotmatch'\A[0-9A-Z]{5}\z'-or$null-eq$message-or!$position.TryGetInt32([ref]$number)-or$number-lt0){throw 'REJECTED'}
  $hintText=if($hint.ValueKind-eq[Text.Json.JsonValueKind]::Null){$null}else{$hint.GetString()}
  return [ordered]@{sqlState=$state;message=(Protect-SafePostgresText $message);hint=$(if($null-eq$hintText){$null}else{Protect-SafePostgresText $hintText});position=$number}
 } catch {throw [InvalidOperationException]::new('POSTGRES_DIAGNOSTIC_REJECTED')}
 finally {if($document){$document.Dispose()}}
}
function Get-SafePostgresExceptionDiagnostic($Failure) {
 $error=if($Failure-is[Management.Automation.ErrorRecord]){$Failure.Exception}else{$Failure}
 for($depth=0;$depth-lt16-and$null-ne$error;$depth++){
  if($error.GetType().FullName-ceq'Npgsql.PostgresException'){
   $value=[ordered]@{sqlState=$error.SqlState;message=$error.MessageText;hint=$error.Hint;position=$error.Position}
   return ConvertFrom-SafePostgresDiagnostic ($value|ConvertTo-Json -Compress)
  }
  $error=$error.InnerException
 }
 return $null
}
function Test-PostgresDiagnosticText([string]$Text) {
 if(!$Text){return $false}
 return [regex]::new('(?m)^(?:psql:[^\r\n]*?:\s*)?(?:ERROR|FATAL|PANIC|DETAIL|CONTEXT):',
  [Text.RegularExpressions.RegexOptions]::CultureInvariant,[TimeSpan]::FromMilliseconds(100)).IsMatch($Text)
}
function ConvertFrom-LegacyPsqlDiagnostic([string]$Text) {
 # Textual psql does not authenticate field boundaries. DETAIL may contain forged HINT prefixes.
 # Once a nonapproved field appears, withhold the entire remaining suffix, not merely its first line.
 if($Text.Length-gt131072){return [ordered]@{diagnostic=$null;availability='WITHHELD_OVERSIZE_LEGACY_STDERR'}}
 $primary=[regex]::new('^(?:psql:.*?:\s*)?(?:ERROR|FATAL|PANIC):\s*(?:(?<state>[0-9A-Z]{5}):\s*)?(?<message>.*)$',
  [Text.RegularExpressions.RegexOptions]::CultureInvariant,[TimeSpan]::FromMilliseconds(100))
 $state=$null;$message=$null;$hint=$null;$position=$null;$suffixWithheld=$false
 foreach($line in ($Text-split'\r?\n')){
  if($suffixWithheld){continue}
  if($line-match'^\s*(?:DETAIL|CONTEXT|QUERY|STATEMENT|INTERNAL QUERY|LOCATION):'){$suffixWithheld=$true;continue}
  $match=$primary.Match($line)
  if($match.Success-and$null-eq$message){$state=if($match.Groups['state'].Success){$match.Groups['state'].Value}else{$null};$message=$match.Groups['message'].Value;continue}
  if($null-ne$message-and$line-match'^HINT:\s*(.*)$'){$hint=$Matches[1];continue}
  if($null-ne$message-and$line-match'^POSITION:\s*([0-9]{1,9})$'){$position=[int]$Matches[1];continue}
  if($line){$suffixWithheld=$true}
 }
 $diagnostic=if($null-ne$message){[ordered]@{sqlState=$state;message=(Protect-SafePostgresText $message);hint=$(if($null-eq$hint){$null}else{Protect-SafePostgresText $hint});position=$position}}else{$null}
 return [ordered]@{diagnostic=$diagnostic;availability='LEGACY_FIRST_LINE_ONLY; UNAUTHENTICATED_SUFFIX_WITHHELD; POSITION_ONLY_IF_EXPLICIT'}
}
