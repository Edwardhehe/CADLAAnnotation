$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'invoke-deepseek-worker.ps1'
$reply = & $runner -Role requirements -Task '只回复：LA_WORKER_OK' -Model deepseek-v4-flash -MaxTokens 128 -RequestTimeoutSec 45
if (($reply | Out-String).Trim() -ne 'LA_WORKER_OK') {
    throw "Unexpected worker reply: $reply"
}
Write-Host 'DeepSeek worker is ready.'
