# Interop checks: the protocol framing, judged by somebody else's implementation
#
# Every other suite in tests\ speaks JSON-RPC that this repo's author wrote by hand. If a framing
# detail were misread - the wrong session header, an initialize result shape only this project
# emits, a tool result an agent cannot parse - those hand-written tests would agree with the
# server's own mistake and still pass. This suite removes that blind spot by driving the real
# server with the OFFICIAL @modelcontextprotocol/sdk.
#
# What it does NOT cover: the Dalamud-facing half of the plugin. It only exercises
# src\DalamudMCP\Mcp\*.cs (the transport and protocol), hosted in-process by tests\McpInterop\host.
#
# Usage:
#   pwsh -File tests\McpInterop\run-interop.ps1
#
# Exit code 0 means every interop check and the negative control passed.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# Initialized before use so the summary line can always report a number, even if a phase
# throws before it would have set one.
$interopExit = 1
$negativeExit = 1
$realExit = 1
$loadExit = 1
Push-Location $here
try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        Write-Host 'node was not found on PATH; skipping the interop checks.' -ForegroundColor Yellow
        exit 0
    }

    if (-not (Test-Path (Join-Path $here 'node_modules\@modelcontextprotocol\sdk'))) {
        Write-Host 'installing the official MCP SDK (one time)...'
        & npm install --no-audit --no-fund --loglevel=error
        if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
    }

    Write-Host 'building the interop host...'
    & dotnet build (Join-Path $here 'host\host.csproj') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "host build failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $here 'host\bin\Debug\net10.0\interophost.exe'
    if (-not (Test-Path $exe)) { throw "host executable not found at $exe" }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Resolve-Path $exe)
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $host_ = [System.Diagnostics.Process]::Start($psi)

    try {
        # The host prints "PORT <n>" once it is listening; block until it does.
        $portLine = $host_.StandardOutput.ReadLine()
        if ($portLine -notmatch '^PORT (\d+)$') {
            throw "the host did not report a port (got: '$portLine')"
        }
        $port = [int]$Matches[1]
        Write-Host "host listening on port $port"
        Write-Host ''

        Write-Host '--- interop: the official MCP SDK against the real server ---'
        & node (Join-Path $here 'interop.mjs') $port
        $interopExit = $LASTEXITCODE

        Write-Host ''
        Write-Host '--- negative control: the SDK must REJECT a non-conforming server ---'
        & node (Join-Path $here 'negative.mjs')
        $negativeExit = $LASTEXITCODE

        # The REAL tool payload, validated by the official parser and by the ajv that ships
        # inside the SDK. PluginLoadTest dumps it when DALAMUD_MCP_DUMP_TOOLS is set.
        $realTools = Join-Path $here 'real-tools.json'
        $dumpPath = Join-Path ([System.IO.Path]::GetTempPath()) "dalamud-mcp-real-tools-$PID.json"
        $env:DALAMUD_MCP_DUMP_TOOLS = $dumpPath
        try {
            Write-Host ''
            Write-Host '--- dumping the real tools/list payload out of the shipped plugin ---'
            $env:DOTNET_NOLOGO = '1'
            & dotnet run --project (Join-Path $here '..\PluginLoadTest\PluginLoadTest.csproj') -p:Platform=x64 -v q 2>&1 |
                Select-String -Pattern 'checks passed|\[FAIL\]|wrote tools/list' |
                ForEach-Object { $_ }
            $loadExit = $LASTEXITCODE

            if (Test-Path $dumpPath) {
                Copy-Item $dumpPath $realTools -Force
                Write-Host ''
                Write-Host '--- real-tool schema validation: the official SDK + ajv against every shipped tool ---'
                & node (Join-Path $here 'validate-real-tools.mjs') $realTools
                $realExit = $LASTEXITCODE
            }
            else {
                Write-Host "the plugin did not write $dumpPath (PluginLoadTest exit $loadExit)" -ForegroundColor Yellow
                $realExit = 1
            }
        }
        finally {
            Remove-Item Env:\DALAMUD_MCP_DUMP_TOOLS -ErrorAction SilentlyContinue
            Remove-Item $dumpPath -ErrorAction SilentlyContinue
        }
    }
    finally {
        # Closing stdin is the host's shutdown signal.
        try { $host_.StandardInput.Close() } catch { }
        if (-not $host_.WaitForExit(5000)) { try { $host_.Kill() } catch { } }
    }

    Write-Host ''
    if ($interopExit -ne 0 -or $negativeExit -ne 0 -or $realExit -ne 0) {
        Write-Host "INTEROP FAILED (interop $interopExit, negative control $negativeExit, real schemas $realExit, load test $loadExit)" -ForegroundColor Red
        exit 1
    }

    Write-Host 'INTEROP OK' -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}
