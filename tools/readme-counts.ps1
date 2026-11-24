<#
.SYNOPSIS
    Counts the projects in AgenticAI.slnx and keeps README.md's generated blocks in step.

.DESCRIPTION
    Reads every <Project Path="..."> in AgenticAI.slnx, groups the paths by top folder
    (src, exercises, contoso) and by chapter folder, and renders two Markdown blocks:

        <!-- BEGIN generated:counts -->    ...    <!-- END generated:counts -->
        <!-- BEGIN generated:projects -->  ...    <!-- END generated:projects -->

    It also checks the folders against the solution: every .csproj under src, exercises
    and contoso (outside bin and obj) must be in AgenticAI.slnx, and every project in the
    solution must exist on disk.

    By default it only checks, and exits 1 when README.md differs from what it would
    write. With -Write it rewrites the blocks in place. Run from anywhere:

        pwsh tools/readme-counts.ps1          # check (CI)
        pwsh tools/readme-counts.ps1 -Write   # update README.md after adding a project

    A new chapter folder needs a title in $Chapters below, or the script stops and says so.
#>
param(
    [switch] $Write
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$slnx = Join-Path $root 'AgenticAI.slnx'
$readme = Join-Path $root 'README.md'

# Chapter titles and parts, as the book prints them.
$Parts = @(
    'Part I: Understand and Start'
    'Part II: Contracts and Authorized Actions'
    'Part III: Knowledge and Documents'
    'Part IV: Composition and Durable Execution'
    'Part V: Interfaces, Security and Observation'
    'Part VI: Qualification and Operations'
)
$Chapters = @{
    1 = 'Choosing the Right Amount of Agency'
    2 = 'Models, Context and Capabilities'
    3 = 'A Reproducible Development Environment'
    4 = 'Your First Agent and Its First Test'
    5 = 'Typed Responses and Domain Validation'
    6 = 'Conversations, Memory and Data Ownership'
    7 = 'Tools and Bounded Execution'
    8 = 'Authorizing and Committing Agent Actions'
    9 = 'MCP Across Trust Boundaries'
    10 = 'Building a Grounded RAG Baseline'
    11 = 'RAG With Permissions, Freshness and Evidence'
    12 = 'Reliable Multimodal Document Processing'
    13 = 'Choosing a Multi-Agent Topology'
    14 = 'Workflows, Branches and Human Review'
    15 = 'Durable Execution and External Effects'
    16 = 'Skills, Harnesses and Capability Lifecycle'
    17 = 'Hosting, Identity and Tenant Isolation'
    18 = 'Streaming Interfaces and Approval Experiences'
    19 = 'Observability, Quality and Incident Diagnosis'
    20 = 'Security Policies and Governance'
    21 = 'Evaluation With Evidence and Uncertainty'
    22 = 'Cost, Latency and Capacity Engineering'
    23 = 'Qualifying the Contoso Agent System'
    24 = 'Operating and Evolving Agents'
}
function Get-Part([int] $n) { [int][math]::Ceiling($n / 4) }   # four chapters per part

# --- Read the solution -----------------------------------------------------------------
[xml] $xml = Get-Content $slnx -Raw
$paths = @($xml.SelectNodes('//Project') | ForEach-Object { $_.GetAttribute('Path') -replace '\\', '/' })
$projects = foreach ($p in $paths) {
    $seg = $p -split '/'
    if ($seg.Count -lt 3) { throw "Unexpected project path in AgenticAI.slnx: $p" }
    $csproj = Join-Path $root $p
    $text = if (Test-Path $csproj) { Get-Content $csproj -Raw } else { '' }
    [pscustomobject]@{
        Path    = $p
        Top     = $seg[0]
        Folder  = $seg[1]
        Name    = [IO.Path]::GetFileNameWithoutExtension($p)
        Library = -not ($text -match '<OutputType>\s*Exe\s*</OutputType>|Sdk="Microsoft\.NET\.Sdk\.Web"|Aspire\.AppHost\.Sdk|<IsAspireHost>\s*true')
    }
}

# --- Check the folders against the solution --------------------------------------------
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($p in $projects) {
    if (-not (Test-Path (Join-Path $root $p.Path))) { $problems.Add("In AgenticAI.slnx but not on disk: $($p.Path)") }
    if ($p.Top -notin 'src', 'exercises', 'contoso') { $problems.Add("Outside src, exercises and contoso: $($p.Path)") }
}
$inSolution = [System.Collections.Generic.HashSet[string]]::new([string[]] $paths, [StringComparer]::OrdinalIgnoreCase)
$skip = '[\\/](bin|obj)[\\/]'
$onDisk = foreach ($top in 'src', 'exercises', 'contoso') {
    Get-ChildItem (Join-Path $root $top) -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch $skip }
}
foreach ($f in $onDisk) {
    $rel = [IO.Path]::GetRelativePath($root, $f.FullName) -replace '\\', '/'
    if (-not $inSolution.Contains($rel)) { $problems.Add("On disk but not in AgenticAI.slnx: $rel") }
}

# Projects with a --self-test mode, found the way the build workflow finds them.
$selfTest = [System.Collections.Generic.HashSet[string]]::new()
foreach ($top in 'src', 'exercises', 'contoso') {
    Get-ChildItem (Join-Path $root $top) -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch $skip } |
        Where-Object { Select-String -Path $_.FullName -SimpleMatch 'args.Contains("--self-test")' -Quiet } |
        ForEach-Object {
            $dir = $_.Directory
            while ($dir -and -not (Get-ChildItem $dir.FullName -Filter '*.csproj' -File)) { $dir = $dir.Parent }
            if ($dir) { [void] $selfTest.Add($dir.FullName) }
        }
}

# --- Render ------------------------------------------------------------------------------
function Join-Names($items, [string] $By = 'Name') { ($items | Sort-Object $By | ForEach-Object { "``$($_.Name)``" }) -join ', ' }
function Format-Count([int] $n, [string] $noun) { if ($n -eq 1) { "1 $noun" } else { "$n ${noun}s" } }
function Get-ChapterNumber([string] $folder) {
    if ($folder -match '^Ch(\d{2})\.') { return [int] $Matches[1] }
    return $null
}

$src = @($projects | Where-Object Top -eq 'src')
$ex = @($projects | Where-Object Top -eq 'exercises')
$co = @($projects | Where-Object Top -eq 'contoso')
$libs = @($projects | Where-Object Library)
$runnable = $projects.Count - $libs.Count
$coVersions = ($co | ForEach-Object { ($_.Folder -split '-')[0] } | Sort-Object { [int]($_.TrimStart('v')) })
$coRange = if ($coVersions) { "$($coVersions[0])-$($coVersions[-1])" } else { 'none' }
$chapterSrcFolders = @($src | Where-Object { Get-ChapterNumber $_.Folder } | Select-Object -ExpandProperty Folder -Unique)
$exFolders = @($ex | Select-Object -ExpandProperty Folder -Unique)

$counts = @(
    "**$($projects.Count) projects** in ``AgenticAI.slnx``:"
    ''
    "- **$($src.Count)** under [``src/``](./src): the samples of $($chapterSrcFolders.Count) chapters, Appendix A, and the shared libraries in ``src/Common``"
    "- **$($ex.Count)** worked exercise solutions under [``exercises/``](./exercises), in $($exFolders.Count) folders"
    "- **$($co.Count)** Contoso versions under [``contoso/``](./contoso) ($coRange)"
    ''
    "$runnable are runnable and $($libs.Count) are libraries ($(Join-Names $libs)). $($selfTest.Count) projects have an offline ``--self-test`` mode."
) -join "`n"

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($part in 1..$Parts.Count) {
    $nums = 1..24 | Where-Object { (Get-Part $_) -eq $part }
    $partSrc = @($src | Where-Object { $n = Get-ChapterNumber $_.Folder; $n -and (Get-Part $n) -eq $part })
    $partEx = @($ex | Where-Object { $n = Get-ChapterNumber $_.Folder; $n -and (Get-Part $n) -eq $part })
    $lines.Add("#### $($Parts[$part - 1])")
    $lines.Add('')
    $lines.Add("$(Format-Count $partSrc.Count 'chapter project'), $(Format-Count $partEx.Count 'exercise project').")
    $lines.Add('')
    $lines.Add('| Ch | Chapter | Projects | Exercises |')
    $lines.Add('|---:|---|---|---|')
    foreach ($n in $nums) {
        $prefix = 'Ch{0:D2}.' -f $n
        $s = @($src | Where-Object { $_.Folder.StartsWith($prefix) })
        $e = @($ex | Where-Object { $_.Folder.StartsWith($prefix) })
        $sFolder = $s | Select-Object -First 1 -ExpandProperty Folder
        $eFolder = $e | Select-Object -First 1 -ExpandProperty Folder
        $sCell = if ($s) { "[``src/$sFolder``](./src/$sFolder) ($($s.Count)): $(Join-Names $s)" } else { 'none' }
        $eCell = if ($e) { "[$($e.Count)](./exercises/$eFolder)" } else { '0' }
        $lines.Add("| $n | $($Chapters[$n]) | $sCell | $eCell |")
    }
    $lines.Add('')
}
$appA = @($src | Where-Object Folder -like 'AppA.*')
$appAEx = @($ex | Where-Object Folder -like 'AppA.*')
$common = @($src | Where-Object Folder -eq 'Common')
$lines.Add('#### Outside the chapters')
$lines.Add('')
$lines.Add('| Folder | Projects |')
$lines.Add('|---|---|')
if ($appA) { $lines.Add("| [``src/$($appA[0].Folder)``](./src/$($appA[0].Folder)), Appendix A | $($appA.Count): $(Join-Names $appA) |") }
if ($appAEx) { $lines.Add("| [``exercises/$($appAEx[0].Folder)``](./exercises/$($appAEx[0].Folder)), Appendix A | $($appAEx.Count): $(Join-Names $appAEx) |") }
if ($common) { $lines.Add("| [``src/Common``](./src/Common), shared libraries | $($common.Count): $(Join-Names $common) |") }
$lines.Add("| [``contoso/``](./contoso), the running example | $($co.Count): $(Join-Names $co -By Folder) |")
$projectsBlock = ($lines -join "`n").TrimEnd()

# Every project must land in exactly one row above.
$known = @($src | Where-Object { (Get-ChapterNumber $_.Folder) -or $_.Folder -like 'AppA.*' -or $_.Folder -eq 'Common' }).Count +
         @($ex | Where-Object { (Get-ChapterNumber $_.Folder) -or $_.Folder -like 'AppA.*' }).Count + $co.Count
if ($known -ne $projects.Count) { $problems.Add("$($projects.Count - $known) project(s) sit in a folder this script does not place in a table.") }
foreach ($f in @($src + $ex | ForEach-Object { Get-ChapterNumber $_.Folder } | Where-Object { $_ } | Select-Object -Unique)) {
    if (-not $Chapters.ContainsKey($f)) { $problems.Add("Chapter $f has a folder but no title in tools/readme-counts.ps1.") }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Output "FAIL: $_" }
    exit 1
}

# --- Compare or write ---------------------------------------------------------------------
$original = (Get-Content $readme -Raw) -replace "`r`n", "`n"
$updated = $original
foreach ($block in @(@{ Name = 'counts'; Body = $counts }, @{ Name = 'projects'; Body = $projectsBlock })) {
    $begin = "<!-- BEGIN generated:$($block.Name) -->"
    $end = "<!-- END generated:$($block.Name) -->"
    $pattern = [regex]::Escape($begin) + '[\s\S]*?' + [regex]::Escape($end)
    if (-not [regex]::IsMatch($updated, $pattern)) {
        Write-Output "FAIL: README.md has no '$begin' ... '$end' block."
        exit 1
    }
    $replacement = "$begin`n$($block.Body)`n$end"
    $updated = [regex]::Replace($updated, $pattern, { param($m) $replacement })
}

$summary = "$($projects.Count) projects: $($src.Count) src, $($ex.Count) exercises, $($co.Count) contoso; $runnable runnable, $($libs.Count) libraries, $($selfTest.Count) with --self-test"
if ($updated -eq $original) {
    Write-Output "PASS: README.md matches AgenticAI.slnx ($summary)"
    exit 0
}
if ($Write) {
    [IO.File]::WriteAllText($readme, $updated, [Text.UTF8Encoding]::new($false))
    Write-Output "UPDATED: README.md ($summary)"
    exit 0
}
$old = $original -split "`n"
$new = $updated -split "`n"
Compare-Object $old $new | ForEach-Object {
    $side = if ($_.SideIndicator -eq '=>') { '+ ' } else { '- ' }
    Write-Output "$side$($_.InputObject)"
}
Write-Output "FAIL: README.md is out of date with AgenticAI.slnx ($summary). Run: pwsh tools/readme-counts.ps1 -Write"
exit 1
