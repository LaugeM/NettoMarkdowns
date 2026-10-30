#!/bin/bash
# Runs on the server, called by deploy.ps1 after it has uploaded nettomarkdowns.tar.gz.
# Unpacks next to the running copy and then swaps folders, so a failed upload never
# leaves the service half replaced. The previous version is kept as app.old.
set -e
cd ~/apps/nettomarkdowns
mkdir -p data

rm -rf app.new
mkdir app.new
tar -xzf ~/nettomarkdowns.tar.gz -C app.new
rm ~/nettomarkdowns.tar.gz
chmod +x app.new/NettoMarkdowns

rm -rf app.old
if [ -d app ]; then mv app app.old; fi
mv app.new app

unit=/etc/systemd/system/nettomarkdowns.service

if systemctl cat nettomarkdowns >/dev/null 2>&1; then
    # The unit file sets paths like where the databases live, so keep it in step
    # with the repo. Unchanged files are left alone.
    if ! cmp -s nettomarkdowns.service "$unit"; then
        sudo install -m 644 nettomarkdowns.service "$unit"
        sudo systemctl daemon-reload
        echo "Updated the service file."
    fi
    sudo systemctl restart nettomarkdowns
    echo "Restarted nettomarkdowns."
    # Publish the dashboard on the tailnet as http://homeserver:5062. Plain HTTP, since
    # WireGuard already encrypts it. Funnel stays off, so nothing is public. Safe to rerun.
    sudo tailscale serve --bg --http=5062 http://127.0.0.1:5062
else
    echo "Installed in ~/apps/nettomarkdowns/app. The service isn't set up yet, see deploy/README.md."
fi
