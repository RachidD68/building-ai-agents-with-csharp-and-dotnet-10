# ============================================================================
# Exercise 03 — scripted acceptance check for the image (PowerShell 7, Docker).
#   pwsh ./check-image.ps1
# 1. dotnet publish to a temp folder; the folder holds no secret file
# 2. docker build from that folder (--pull=false --network none)
# 3. the image runs as a non-root user
# 4. no layer's history mentions a key (a planted ENV key is caught first)
# 5. the self-test passes inside the image (--network none)
# Exit 0 when every check passes. Needs the local base image
# mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled.
# ============================================================================

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path ([IO.Path]::GetTempPath()) ("ch17-ex03-publish-" + [Guid]::NewGuid().ToString('N'))
$image = 'ch17-ex03-agent-api:check'
$planted = 'ch17-ex03-agent-api:planted'
$secretPattern = '(?i)(apikey|signingkeys|password|client_secret|sk-[a-z0-9])'
$passed = 0; $total = 0
function Check([string] $name, [bool] $ok) {
    $script:total++; if ($ok) { $script:passed++ }
    Write-Output ("  {0}  {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name)
}

Write-Output '=== Ex03.ContainerizedDeploy: image check ==='
try {
    dotnet publish (Join-Path $here 'Ex03.ContainerizedDeploy.csproj') -c Release -o $out --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    $secretFiles = Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -match '^(appsettings\.Development\.json|secrets\.json)$|\.pfx$' }
    $keyed = (Get-Content (Join-Path $out 'appsettings.json') -Raw) -match '"ApiKey"\s*:\s*"[^"]+"'
    Check "the published folder has no secret file and no non-empty ApiKey" (($secretFiles.Count -eq 0) -and -not $keyed)

    docker build --pull=false --network none -q -f (Join-Path $here 'Dockerfile') -t $image $out | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker build failed ($LASTEXITCODE)" }
    $user = docker image inspect --format '{{.Config.User}}' $image
    Check "the image runs as a non-root user (Config.User = '$user')" (($user -ne '') -and ($user -notin @('root', '0', '0:0')))

    # The check must be able to fail: plant a key in an ENV layer and look for it.
    "FROM $image`nENV LlmProvider__OpenAI__ApiKey=sk-planted-for-the-check" |
        docker build --pull=false --network none -q -t $planted - | Out-Null
    $plantedHits = docker history --no-trunc --format '{{.CreatedBy}}' $planted | Select-String -Pattern $secretPattern
    Check "DEFECT REPRODUCED: a key set with ENV is visible in docker history ($($plantedHits.Count) layer)" ($plantedHits.Count -ge 1)
    $hits = docker history --no-trunc --format '{{.CreatedBy}}' $image | Select-String -Pattern $secretPattern
    Check "the real image: no layer history mentions a key ($($hits.Count) matches)" ($hits.Count -eq 0)

    $selfTest = docker run --rm --network none $image --self-test
    $code = $LASTEXITCODE
    $result = $selfTest | Select-String -Pattern '^RESULT ' | Select-Object -Last 1
    Check "the self-test passes inside the image, no network ($result, exit $code)" ($code -eq 0)
}
finally {
    docker image rm -f $planted $image 2>$null | Out-Null
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
}
Write-Output ''
Write-Output "RESULT $passed/$total"
exit $(if ($passed -eq $total) { 0 } else { 1 })
