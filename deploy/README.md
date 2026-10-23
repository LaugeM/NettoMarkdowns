# Running on the home server

The app runs on the Debian home server as a systemd service. One process serves the
dashboard and runs the markdown logger, which polls the Food Waste API every 15
minutes and keeps the history in SQLite for statistics later.

The app is built on the Windows machine as a self-contained Linux program, so the
server needs no .NET install. Like bilalarm, everything lives in one folder:

| Path on the server | What it is |
| --- | --- |
| `~/apps/nettomarkdowns/app` | The program. `app.old` is the previous version |
| `~/apps/nettomarkdowns/data/markdowns.db` | The SQLite history |
| `~/apps/nettomarkdowns/data/products.db` | Saved pack sizes and today's API call count |
| `~/apps/nettomarkdowns/nettomarkdowns.env` | API key and postcodes |

## One-time setup

### 1. Packages (server)

ICU is needed for Danish number formatting. `sqlite3` is for looking at the data by
hand.

```bash
sudo apt install -y libicu76 sqlite3
```

### 2. Upload the app (Windows)

From the repo root, in PowerShell:

```powershell
./deploy/deploy.ps1
```

If `homeserver` doesn't resolve, pass the address: `./deploy/deploy.ps1 -Server lauge@192.168.x.x`.

### 3. API key and postcodes (server)

```bash
cd ~/apps/nettomarkdowns
nano nettomarkdowns.env
chmod 600 nettomarkdowns.env
```

Paste in the contents of [nettomarkdowns.env.example](nettomarkdowns.env.example) and
fill in the key and postcodes.

### 4. The service (Windows, then server)

```powershell
scp deploy/nettomarkdowns.service lauge@homeserver:
```

```bash
sudo mv ~/nettomarkdowns.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now nettomarkdowns
```

## Checking it works

```bash
systemctl status nettomarkdowns
journalctl -u nettomarkdowns -f
```

Every poll writes one line like `Logged 4 stores, 37 offers: 3 new, 1 gone`.

The dashboard is at `http://homeserver:5062`, or over Tailscale from the phone. It
has no login, so it should only be reachable on the home network and the tailnet,
not forwarded from the router.

The latest polls, including failed ones:

```bash
sqlite3 ~/apps/nettomarkdowns/data/markdowns.db "SELECT * FROM polls ORDER BY id DESC LIMIT 10"
```

## Updating

Run `./deploy/deploy.ps1` again. It installs the new version next to the old one,
swaps them and restarts the service, which asks for the sudo password. The previous
version is kept in `~/apps/nettomarkdowns/app.old`.

## What gets stored

All times in the database are UTC.

| Table | One row per |
| --- | --- |
| `stores` | Store seen in the postcodes |
| `offers` | Markdown: store, barcode and the API's `start_time`. Has `first_seen`, `last_seen` and `gone_at` |
| `offer_changes` | Time an offer's price or stock changed, including when it was first seen |
| `polls` | Poll, with counts or the error message |

`start_time` is when Salling says the markdown began. `first_seen` is when the logger
first saw it, so it is at most one interval late and can be used to check
`start_time`. When an offer disappears, `gone_at` is set to the first poll it was
missing from. It sold out or was pulled somewhere between `last_seen` and `gone_at`.

A failed poll is stored with its error and never marks anything gone, so gaps in
the data can be told apart from quiet periods.

To back it up while the service is running:

```bash
sqlite3 ~/apps/nettomarkdowns/data/markdowns.db ".backup /home/lauge/markdowns-backup.db"
```
