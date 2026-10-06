# lib-cases.ps1 — Runner 用例 JSON 生成库（返回 JSON 文本片段；不用 ConvertTo-Json）
# 用法： . 'lib-cases.ps1' ; WriteCase -Path <file> -Id <id> -Title <t> -Steps (@(Prelude) + @(...))

function EscJson([string]$t) {
  if ($null -eq $t) { return '' }
  return $t.Replace('\', '\\').Replace('"', '\"')
}
function JNote([string]$t) { '{ "op": "note", "text": "' + (EscJson $t) + '" }' }
function JKill([string]$n, [int]$w) { '{ "op": "kill", "name": "' + $n + '", "waitMs": ' + $w + ' }' }
function JSleep([int]$ms) { '{ "op": "sleep", "ms": ' + $ms + ' }' }
function JLaunch([int]$waitMs = 25000) { '{ "op": "launch", "exe": "C:\\PCMig\\PCMig.WinUI.exe", "windowTitleRegex": "迁移工具", "waitMs": ' + $waitMs + ' }' }
function JLaunchOther([string]$exe, [string]$rx, [int]$waitMs = 15000) { '{ "op": "launch", "exe": "' + $exe + '", "windowTitleRegex": "' + (EscJson $rx) + '", "waitMs": ' + $waitMs + ' }' }
function JFocus() { '{ "op": "focus" }' }
function JWinUp() { '{ "op": "key", "vk": "WIN+UP" }' }
function JWindow([string]$rx = '迁移工具') { '{ "op": "window", "titleRegex": "' + (EscJson $rx) + '" }' }
function JReadId([string]$id, [bool]$soft = $true) { '{ "op": "read", "soft": ' + $soft.ToString().ToLower() + ', "target": { "id": "' + $id + '" } }' }
function JReadListItem([int]$idx, [bool]$soft = $true) { '{ "op": "read", "soft": ' + $soft.ToString().ToLower() + ', "target": { "type": "ListItem", "all": true, "index": ' + $idx + ' } }' }
function JReadCheckBoxInList([int]$idx, [bool]$soft = $true) { '{ "op": "read", "soft": ' + $soft.ToString().ToLower() + ', "target": { "type": "CheckBox", "within": { "type": "ListItem", "all": true, "index": ' + $idx + ' } } }' }
function JTypeId([string]$id, [string]$text) { '{ "op": "type", "target": { "id": "' + $id + '" }, "text": "' + (EscJson $text) + '", "clearFirst": true }' }
function JTypeSecret([string]$id, [string]$alias) { '{ "op": "type", "target": { "id": "' + $id + '" }, "secretAlias": "' + $alias + '", "clearFirst": true }' }
function JClickId([string]$id) { '{ "op": "click", "target": { "id": "' + $id + '" } }' }
function JClickText([string]$rx, [bool]$soft = $false) { '{ "op": "click", "soft": ' + $soft.ToString().ToLower() + ', "target": { "type": "Text", "nameRegex": "' + (EscJson $rx) + '" } }' }
function JClickButtonRx([string]$rx, [bool]$soft = $false) { '{ "op": "click", "soft": ' + $soft.ToString().ToLower() + ', "target": { "type": "Button", "nameRegex": "' + (EscJson $rx) + '" } }' }
function JClickLabel([int]$label) { '{ "op": "click", "target": { "label": ' + $label + ' } }' }
function JWaitFor([string]$id, [string]$cond, [string]$val, [int]$timeoutMs, [int]$pollMs = 300, [bool]$soft = $false) {
  $s = '{ "op": "waitfor", "soft": ' + $soft.ToString().ToLower() + ', "target": { "id": "' + $id + '" }, "condition": "' + $cond + '"'
  if ($val -ne $null -and $val -ne '') { $s += ', "value": "' + (EscJson $val) + '"' }
  $s += ', "timeoutMs": ' + $timeoutMs + ', "pollMs": ' + $pollMs + ' }'
  return $s
}
function JAssertMatches([string]$id, [string]$pattern, [bool]$soft = $true) { '{ "op": "assert", "soft": ' + $soft.ToString().ToLower() + ', "kind": "valueMatches", "target": { "id": "' + $id + '" }, "value": "' + (EscJson $pattern) + '" }' }
function JAssertNotEmpty([string]$id, [bool]$soft = $true) { '{ "op": "assert", "soft": ' + $soft.ToString().ToLower() + ', "kind": "valueNotEmpty", "target": { "id": "' + $id + '" } }' }
function JAssertExists([string]$id, [bool]$soft = $true) { '{ "op": "assert", "soft": ' + $soft.ToString().ToLower() + ', "kind": "exists", "target": { "id": "' + $id + '" } }' }
function JDump([string]$out) { '{ "op": "dump", "out": "' + $out + '" }' }
function JShot() { '{ "op": "screenshot" }' }
function JTgl([int]$idx, [string]$want) { '{ "op": "toggle", "target": { "type": "CheckBox", "within": { "type": "TreeItem", "all": true, "index": ' + $idx + ' } }, "want": "' + $want + '" }' }
function JReadTgl([int]$idx, [bool]$soft = $true) { '{ "op": "read", "soft": ' + $soft.ToString().ToLower() + ', "target": { "type": "CheckBox", "within": { "type": "TreeItem", "all": true, "index": ' + $idx + ' } } }' }

# ---- 组合块 ----
function Prelude() {
  @(
    (JKill 'msedge' 1000),
    (JKill 'PCMig.WinUI' 2500),
    (JSleep 800),
    (JLaunch 25000),
    (JFocus),
    (JWinUp),
    (JSleep 1200),
    (JWindow)
  )
}
function ConnectBlock([string]$vmHost, [string]$user = 'CORP\user01', [string]$alias = 'domain_user01', [bool]$wait = $true) {
  $a = @(
    (JTypeId 'Step1.HostInput' $vmHost),
    (JTypeId 'Step1.UsernameInput' $user),
    (JTypeSecret 'Step1.PasswordInput' $alias),
    (JClickId 'Step1.Connect')
  )
  if ($wait) { $a += (JWaitFor 'Step1.Connect' 'enabled' '' 60000 500) }
  return $a
}
function StatusReadBlock() {
  @(
    (JReadId 'SourceStatusText'),
    (JReadId 'Step1.StatusText'),
    (JReadId 'Step1.ShareList'),
    (JSleep 500)
  )
}
function AddShareBlock([string]$name) {
  @(
    (JClickText '添加共享'),
    (JSleep 900),
    (JReadId 'Step1.ManualShareName'),
    (JTypeId 'Step1.ManualShareName' $name),
    (JClickId 'Step1.AddShare'),
    (JSleep 1500),
    (JReadId 'Step1.StatusText'),
    (JReadId 'Step1.ShareList'),
    (JSleep 300)
  )
}
function EscCloseBlock() { @((JClickId 'Step1.HostInput'), (JSleep 300), ('{ "op": "key", "vk": "ESC" }'), (JSleep 800)) }
function GotoStep2() { @((JClickButtonRx '下一步'), (JSleep 2500), (JReadId 'StateLineText')) }
function BackToStep1() { @((JClickText '上一步' $true), (JSleep 2000), (JReadId 'SourceStatusText')) }
function SetTarget([string]$path) { @((JTypeId 'TargetRootBox' $path), (JSleep 400)) }
function Prepare() { @((JClickId 'Step2.Prepare'), (JSleep 3500), (JReadId 'StateMessageText'), (JReadId 'Step2.Start')) }

function WriteCase([string]$Path, [string]$Id, [string]$Title, [string[]]$Steps, [bool]$everyStep = $false) {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('{')
  [void]$sb.AppendLine('  "id": "' + $Id + '",')
  [void]$sb.AppendLine('  "title": "' + $Title + '",')
  [void]$sb.AppendLine('  "screenshotEveryStep": ' + $everyStep.ToString().ToLower() + ',')
  [void]$sb.AppendLine('  "steps": [')
  for ($i = 0; $i -lt $Steps.Count; $i++) {
    $sep = ','
    if ($i -eq $Steps.Count - 1) { $sep = '' }
    [void]$sb.AppendLine('    ' + $Steps[$i] + $sep)
  }
  [void]$sb.AppendLine('  ]')
  [void]$sb.AppendLine('}')
  [IO.File]::WriteAllText($Path, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
  return $Path
}
function ValidateCase([string]$Path) {
  $t = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
  try { $o = $t | ConvertFrom-Json; return ('PARSE_OK id=' + $o.id + ' steps=' + @($o.steps).Count + ' bytes=' + (Get-Item $Path).Length) }
  catch { return ('PARSE_FAIL ' + $Path + ' :: ' + $_.Exception.Message) }
}
# ---- appended: escaped variants (later definition wins) ----
function JLaunchOther([string]$exe, [string]$rx, [int]$waitMs = 15000) { '{ "op": "launch", "exe": "' + (EscJson $exe) + '", "windowTitleRegex": "' + (EscJson $rx) + '", "waitMs": ' + $waitMs + ' }' }
function JLaunchArgs([string]$exe, [string]$argStr, [int]$waitMs = 15000) { '{ "op": "launch", "exe": "' + (EscJson $exe) + '", "args": "' + (EscJson $argStr) + '", "waitMs": ' + $waitMs + ' }' }
function JTypeLiteral([string]$id, [string]$text) { '{ "op": "type", "target": { "id": "' + $id + '" }, "text": "' + (EscJson $text) + '", "clearFirst": true, "sensitive": true }' }
