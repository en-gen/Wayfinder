<#
.SYNOPSIS
  Scaffolds the Case.Flow product roadmap (scripts/roadmap.json) into Azure DevOps
  as Epics with child Features (falls back to Issue/User Story/PBI on other templates).

.USAGE
  $env:ADO_PAT = '<PAT with Work Items Read & Write>'
  ./scripts/scaffold-roadmap.ps1                      # targets brute-force/Case
  ./scripts/scaffold-roadmap.ps1 -WhatIfMode          # print plan, create nothing

  Idempotent by work-item title: re-running skips anything already created.
  Everything is tagged from roadmap.json ("roadmap-v1").
#>
[CmdletBinding()]
param(
  [string]$Organization = 'brute-force',
  [string]$Project = 'Case',
  [string]$RoadmapPath = (Join-Path $PSScriptRoot 'roadmap.json'),
  [switch]$WhatIfMode
)

$ErrorActionPreference = 'Stop'
if (-not $env:ADO_PAT) { throw 'Set $env:ADO_PAT to a PAT with Work Items (Read & Write) scope.' }

$baseUri = "https://dev.azure.com/$Organization/$([uri]::EscapeDataString($Project))"
$hdr = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$($env:ADO_PAT)")) }

$roadmap = Get-Content $RoadmapPath -Raw | ConvertFrom-Json
$tag = $roadmap.tag

# --- discover work item types available in this project's process template
# (workitemtypes JSON contains empty-string property names; default parsing drops them — use -AsHashtable)
$typesRaw = (Invoke-WebRequest -Headers $hdr -Uri "$baseUri/_apis/wit/workitemtypes?api-version=7.1").Content | ConvertFrom-Json -AsHashtable
$types = @($typesRaw['value'] | ForEach-Object { $_['name'] })
$epicType  = @('Epic') | Where-Object { $types -contains $_ } | Select-Object -First 1
$childType = @('Feature', 'User Story', 'Product Backlog Item', 'Issue') | Where-Object { $types -contains $_ } | Select-Object -First 1
if (-not $epicType)  { $epicType = $childType }
if (-not $childType) { throw "No usable work item type found. Types present: $($types -join ', ')" }
Write-Host "Process types -> epic: $epicType, child: $childType"

# --- existing titles (idempotency)
$wiql = @{ query = "SELECT [System.Id], [System.Title] FROM WorkItems WHERE [System.TeamProject] = '$Project' AND [System.Tags] CONTAINS '$tag'" } | ConvertTo-Json
$existingIds = (Invoke-RestMethod -Method Post -Headers ($hdr + @{ 'Content-Type' = 'application/json' }) -Uri "$baseUri/_apis/wit/wiql?api-version=7.1" -Body $wiql).workItems.id
$existingTitles = @{}
if ($existingIds) {
  foreach ($chunk in [System.Linq.Enumerable]::Chunk([int[]]$existingIds, 150)) {
    $ids = $chunk -join ','
    (Invoke-RestMethod -Headers $hdr -Uri "$baseUri/_apis/wit/workitems?ids=$ids&fields=System.Title&api-version=7.1").value |
      ForEach-Object { $existingTitles[$_.fields.'System.Title'] = $_.id }
  }
}

function New-WorkItem([string]$Type, [string]$Title, [string]$Description, [int]$ParentId = 0) {
  if ($existingTitles.ContainsKey($Title)) { Write-Host "  skip (exists #$($existingTitles[$Title])): $Title"; return $existingTitles[$Title] }
  if ($WhatIfMode) { Write-Host "  would create [$Type]: $Title"; return 0 }
  $ops = @(
    @{ op = 'add'; path = '/fields/System.Title'; value = $Title },
    @{ op = 'add'; path = '/fields/System.Description'; value = $Description },
    @{ op = 'add'; path = '/fields/System.Tags'; value = $tag }
  )
  if ($ParentId -gt 0) {
    $ops += @{ op = 'add'; path = '/relations/-'; value = @{ rel = 'System.LinkTypes.Hierarchy-Reverse'; url = "$baseUri/_apis/wit/workItems/$ParentId" } }
  }
  $body = ConvertTo-Json $ops -Depth 6
  $encType = [uri]::EscapeDataString($Type)
  $wi = Invoke-RestMethod -Method Post -Headers ($hdr + @{ 'Content-Type' = 'application/json-patch+json' }) `
        -Uri "$baseUri/_apis/wit/workitems/`$$encType`?api-version=7.1" -Body $body
  Write-Host "  created [$Type] #$($wi.id): $Title"
  $existingTitles[$Title] = $wi.id
  return $wi.id
}

foreach ($epic in $roadmap.epics) {
  Write-Host "Epic: $($epic.title)"
  $epicId = New-WorkItem -Type $epicType -Title $epic.title -Description $epic.description
  foreach ($f in $epic.features) {
    New-WorkItem -Type $childType -Title $f.title -Description $f.description -ParentId $epicId | Out-Null
  }
}
Write-Host 'Done.'
