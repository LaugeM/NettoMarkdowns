# Opens the dashboard on the home server through an SSH tunnel and starts the browser.
# The app only listens on 127.0.0.1 on the server. Close this window to close the tunnel.
# Run from the repo root:  ./deploy/panel.ps1
param(
    [string]$Server = "lauge@homeserver",
    [int]$LocalPort = 5062,
    [int]$RemotePort = 5062
)
$ErrorActionPreference = "Stop"

$url = "http://127.0.0.1:$LocalPort"
Write-Host "Tunnel to $Server, dashboard on $url. Press Ctrl+C to stop."

# Open the browser once the tunnel is up.
Start-Job -ScriptBlock {
    param($Port, $Url)
    for ($i = 0; $i -lt 30; $i++) {
        if ((Test-NetConnection 127.0.0.1 -Port $Port -WarningAction SilentlyContinue).TcpTestSucceeded) {
            Start-Process $Url
            return
        }
        Start-Sleep -Milliseconds 500
    }
} -ArgumentList $LocalPort, $url | Out-Null

ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -L "${LocalPort}:127.0.0.1:${RemotePort}" $Server
