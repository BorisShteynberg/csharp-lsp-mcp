<#
.SYNOPSIS
    Adds csharp-lsp-mcp to a project's Claude Code MCP server configuration.

.PARAMETER ProjectPath
    Absolute path to the project directory. Defaults to the current directory.

.EXAMPLE
    .\add-to-project.ps1
    .\add-to-project.ps1 C:\Users\boriss\source\repos\my-csharp-project
#>
param(
    [Parameter(Position = 0)]
    [string]$ProjectPath = (Get-Location).Path
)

$ErrorActionPreference = "Stop"

# Resolve to absolute path
$ProjectPath = (Resolve-Path $ProjectPath).Path.TrimEnd('\', '/')

$claudeJsonPath = "$env:USERPROFILE\.claude.json"
if (-not (Test-Path $claudeJsonPath)) {
    Write-Error "Claude Code config not found at $claudeJsonPath. Is Claude Code installed?"
    exit 1
}

# Determine which csharp-lsp-mcp binary to use
$globalTool = Get-Command "csharp-lsp-mcp" -ErrorAction SilentlyContinue
if ($globalTool) {
    $mcpCommand = "csharp-lsp-mcp"
    $mcpArgs    = @()
    Write-Host "Using global tool: csharp-lsp-mcp"
} else {
    # Fall back to the debug build next to this script
    $repoRoot = Split-Path $PSCommandPath
    $dllPath  = Join-Path $repoRoot "csharp-lsp-mcp\src\CSharpLspMcp\bin\Debug\net8.0\csharp-lsp-mcp.dll"
    if (-not (Test-Path $dllPath)) {
        Write-Error @"
csharp-lsp-mcp not found as a global tool and the local debug build is missing.

Install the global tool:
    dotnet tool install --global csharp-lsp-mcp

Or build locally first:
    dotnet build $repoRoot\csharp-lsp-mcp\src\CSharpLspMcp
"@
        exit 1
    }
    $mcpCommand = "dotnet"
    $mcpArgs    = @($dllPath)
    Write-Host "Using local build: $dllPath"
}

# Claude Code stores project keys with forward slashes
$projectKey = $ProjectPath.Replace('\', '/')

$json = Get-Content $claudeJsonPath -Raw | ConvertFrom-Json

# Create the project entry if it doesn't exist yet
if ($json.projects.PSObject.Properties.Name -notcontains $projectKey) {
    Write-Host "Creating new project entry for: $projectKey"
    $json.projects | Add-Member -NotePropertyName $projectKey -NotePropertyValue ([PSCustomObject]@{
        allowedTools           = @()
        mcpContextUris         = @()
        mcpServers             = [PSCustomObject]@{}
        enabledMcpjsonServers  = @()
        disabledMcpjsonServers = @()
        hasTrustDialogAccepted = $false
    })
}

$project = $json.projects.$projectKey

if ($project.PSObject.Properties.Name -notcontains 'mcpServers') {
    $project | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([PSCustomObject]@{})
}

$serverConfig = [PSCustomObject]@{
    type    = "stdio"
    command = $mcpCommand
    args    = $mcpArgs
    env     = [PSCustomObject]@{}
}

$project.mcpServers | Add-Member -NotePropertyName "csharp-lsp-mcp" -NotePropertyValue $serverConfig -Force

$json | ConvertTo-Json -Depth 20 | Set-Content $claudeJsonPath -Encoding UTF8

Write-Host ""
Write-Host "Added csharp-lsp-mcp to: $projectKey"
Write-Host "Restart Claude Code or reconnect via /mcp to activate."
