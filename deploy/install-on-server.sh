#!/bin/bash
# Runs on the server, called by deploy.ps1 after it has uploaded nettomarkdowns.tar.gz.
# Unpacks next to the running copy and then swaps folders, so a failed upload never
# leaves the service half replaced. The previous version is kept as app.old.
set -e
cd ~/nettomarkdowns

rm -rf app.new
mkdir app.new
tar -xzf ~/nettomarkdowns.tar.gz -C app.new
rm ~/nettomarkdowns.tar.gz
chmod +x app.new/NettoMarkdowns

rm -rf app.old
if [ -d app ]; then mv app app.old; fi
mv app.new app

if systemctl cat nettomarkdowns >/dev/null 2>&1; then
    sudo systemctl restart nettomarkdowns
    echo "Restarted nettomarkdowns."
else
    echo "Installed in ~/nettomarkdowns/app. The service isn't set up yet, see deploy/README.md."
fi
