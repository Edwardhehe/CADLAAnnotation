param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('requirements', 'zwcad-review', 'code-review')]
    [string]$Role,

    [Parameter(Mandatory = $true)]
    [string]$Task,

    [string[]]$ContextFile = @(),
    [ValidateSet('deepseek-v4-pro', 'deepseek-v4-flash')]
    [string]$Model,
    [int]$MaxTokens = 4096,
    [ValidateRange(10, 600)]
    [int]$RequestTimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rolePath = Join-Path $root ".deepseek\roles\$Role.md"

if ([string]::IsNullOrWhiteSpace($Model)) {
    $Model = if ($Role -eq 'requirements') { 'deepseek-v4-flash' } else { 'deepseek-v4-pro' }
}

$userKey = [Environment]::GetEnvironmentVariable('DEEPSEEK_API_KEY', 'User')
$apiKey = if (-not [string]::IsNullOrWhiteSpace($userKey)) { $userKey.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:DEEPSEEK_API_KEY)) { $env:DEEPSEEK_API_KEY.Trim() } else { '' }
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw 'DEEPSEEK_API_KEY is not set. Store it in the user environment and restart Codex.'
}
if (-not (Test-Path -LiteralPath $rolePath)) {
    throw "Role file not found: $rolePath"
}

$systemPrompt = (Get-Content -LiteralPath $rolePath -Raw -Encoding UTF8).Trim()
$context = New-Object System.Collections.Generic.List[string]
foreach ($file in $ContextFile) {
    $resolved = (Resolve-Path -LiteralPath $file).Path
    if (-not $resolved.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Context file must be inside the workspace: $resolved"
    }
    $content = Get-Content -LiteralPath $resolved -Raw -Encoding UTF8
    $context.Add("FILE: $resolved`n$content")
}

$userPrompt = $Task
if ($context.Count -gt 0) {
    $userPrompt += "`n`nREAD-ONLY CONTEXT:`n" + ($context -join "`n`n---`n`n")
}

$payload = @{
    model = $Model
    messages = @(
        @{ role = 'system'; content = $systemPrompt }
        @{ role = 'user'; content = $userPrompt }
    )
    max_tokens = $MaxTokens
    stream = $false
    thinking = @{ type = 'disabled' }
} | ConvertTo-Json -Depth 10

$headers = @{ Authorization = "Bearer $apiKey" }
$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
$rawResponse = Invoke-WebRequest -UseBasicParsing -Method Post `
    -Uri 'https://api.deepseek.com/chat/completions' `
    -Headers $headers `
    -ContentType 'application/json; charset=utf-8' `
    -Body $payloadBytes `
    -TimeoutSec $RequestTimeoutSec
$responseText = [System.Text.Encoding]::UTF8.GetString($rawResponse.RawContentStream.ToArray())
$response = $responseText | ConvertFrom-Json

if (-not $response.choices -or [string]::IsNullOrWhiteSpace($response.choices[0].message.content)) {
    throw 'DeepSeek returned no worker output.'
}

$response.choices[0].message.content
