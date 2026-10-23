# Builds a Linux version of the app on this machine and installs it on the home server.
# The server needs no .NET install. Run from the repo root:
#   ./deploy/deploy.ps1
#   ./deploy/deploy.ps1 -Server lauge@192.168.1.50

param([string]$Server = "lauge@homeserver")

$ErrorActionPreference = "Stop"
$out = "publish/linux-x64"
$archive = "publish/nettomarkdowns.tar.gz"

function Check([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed" }
}

dotnet publish NettoMarkdowns.csproj -c Release -r linux-x64 --self-contained -o $out
Check "dotnet publish"

tar -czf $archive -C $out .
Check "tar"

ssh $Server "mkdir -p apps/nettomarkdowns"
Check "ssh"

scp $archive "${Server}:nettomarkdowns.tar.gz"
Check "Upload"

scp deploy/install-on-server.sh "${Server}:apps/nettomarkdowns/install-on-server.sh"
Check "Upload"

# -t so sudo can ask for the password when it restarts the service.
ssh -t $Server "bash apps/nettomarkdowns/install-on-server.sh"
Check "Install on server"
