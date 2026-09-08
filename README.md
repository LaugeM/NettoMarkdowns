# Netto markdowns

A local ASP.NET Core (Razor Pages) dashboard over Salling Group's public
**Food Waste API** — the same clearance data the Netto app shows: what each
store has marked down right now, the original price, the reduced price, the
discount, the stock count and how long the markdown is valid.

Live view only — nothing is stored between requests.

## Setup

1. Register an app at <https://developer.sallinggroup.com> and enable the
   **Food Waste** API. You get a bearer token.
2. Store the key outside source control. The first command adds a
   `UserSecretsId` to the `.csproj` and only needs running once:

```bash
dotnet user-secrets init
dotnet user-secrets set "Salling:ApiKey" "<your key>"
```

   The key lands in `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`, well
   outside the repo. User secrets are only loaded in the **Development**
   environment — which is what `dotnet run` uses via
   `Properties/launchSettings.json`. If you ever publish this, supply the key
   as a `Salling__ApiKey` environment variable instead.

3. Set the postcodes you care about in `appsettings.json`:

```json
"Salling": {
  "DefaultZips": [ "2200", "2400" ],
  "Brands": [ "netto" ]
}
```

4. Run it:

```bash
dotnet run
```

## Using it

- **Postcodes** — comma-separated. One API lookup per postcode, run in
  parallel, results merged and de-duplicated by store id. That is how you
  cover several stores near you.
- **Stores** — every store found in those postcodes is listed as a chip. Tick
  the one or two you actually shop at and the rest disappear; leave them all
  clear to see everything. The choice is saved in a `netto.stores` cookie for
  180 days, so it survives restarts without needing a database.
- **Categories** — a two-level tree built from each product's `categories`
  field (Danish label, falling back to English). Tick a group to take all of
  it, or expand it to pick individual categories; the group box shows a
  half-ticked state when only some children are selected. Counts are derived
  from what the current stores actually have in stock, so ticking a box always
  leaves exactly that many items and dead options never appear. Products with
  no category are grouped under *Uncategorised* rather than silently dropped.
  Not remembered between visits, unlike the store choice.
- **Min. discount / Max. price / Search** — server-side filters; every view is
  a shareable URL, so you can bookmark e.g. `?MinDiscount=50&Sort=expiry`.
- **One combined list** — flattens all stores into a single ranked table
  instead of one card per store.
- `Brands` in config also accepts `bilka` and `foetex` if you want those too.

## Last sale date

`offer.endTime` is when the markdown stops. Across a full fetch of 276 offers it was
`23:59:59` local **every single time** — never part-way through a day — so it is a
date, not a deadline, and the column shows it as one: "Today, 8 Sep", "Tomorrow,
9 Sep", "Thu 10 Sep". Today's items are highlighted, past ones struck through.

For short-dated goods this is effectively the product's last sale date, which is the
useful reading: it's the store's own guard against selling something past its date.
A time is still appended if an offer ever ends mid-day, so that case can't pass
unnoticed.

Dates are formatted in en-GB to match the interface language; prices deliberately
follow the machine's culture, so they stay Danish (`12,00 kr.`).

## Price per kilo

The Food Waste API carries no weight and no unit price, so kr./kg is established
three ways, in descending order of confidence:

1. **Sold loose.** When `stockUnit` is `kg` the API's prices are already per kilo,
   so they're shown as-is (green). Note that `stock` on these rows is ~0.19 for
   every product regardless of type — it is *not* a weight, and dividing by it
   produces nonsense. Those rows show "by weight" instead of a fake quantity.
2. **A pack size in the description** — "TRIO DIP 210G" → 71,43 kr./kg. Only about
   5% of Netto descriptions carry one; the rest are till names like
   "HK GRIS 3-7% MESTERHAKKET".
3. **The kr./kg button**, on every remaining row. It calls the Products EAN API for
   that one barcode.

That third one is a button rather than automatic because the Products EAN API allows
**100 requests a day**. One press is one call. Results are cached for a week (pack
weights don't change), failures refund the counter, and the header shows how much of
the budget is gone. `Salling:ProductsDailyLimit` and `Salling:ProductCacheDuration`
tune it.

`GET /v2/products/{ean}?storeId=…` — the store id is required, since prices differ
between stores. It answers with an in-store and a webshop record, either of which may
be null:

```json
{ "instore": { "contents": 250, "contentsUnit": "g",
               "price": 30, "unit": "kg", "unitPrice": 120 }, "webshop": null }
```

Note that `unitPrice` is the **normal** price per kilo, not the marked-down one, so it
can't be shown as-is. The markdown price per kilo is `newPrice / contents`, and the
API's `unitPrice` is reported alongside as the "normally …" comparison. If `contents`
is missing, the pack size is recovered as `price / unitPrice`. Products sold by count
say so instead of inventing a weight.

## How categories are grouped

`CategoryTree.Resolve` tries three things in order:

1. **A path in the label.** If the API ever returns something like
   `"Frost > Is"`, that is used directly — group `Frost`, category `Is`.
   `>`, `/` and `|` are all recognised as separators.
2. **The `Salling:CategoryGroups` map** in `appsettings.json`, which folds flat
   labels into groups (`Kød`, `Fisk` → `Kød & fisk`).
3. **Neither.** The label becomes its own top-level entry.

That third rule is the important one: an unrecognised category is never swept
into an "Other" bucket where you'd stop noticing it. The shipped map is a
guess at Netto's Danish category names, so once you have real data, extend it
with whatever your stores actually return — it is pure configuration, no code
change needed.

Only the leaf checkboxes are named `Categories`. The group checkbox is a
UI-only control driven by `wwwroot/js/site.js`, so the server still receives a
flat list and doesn't need to know the tree exists.

## Layout

| Path | What it is |
| --- | --- |
| `Program.cs` | DI wiring: options binding, memory cache, typed `HttpClient` |
| `Services/SallingClient.cs` | The API calls, caching, and error mapping |
| `Services/SallingOptions.cs` | Bound to the `Salling` config section |
| `Services/CategoryTree.cs` | Resolves a category label into group + leaf |
| `Services/SampleDataHandler.cs` | Dev-only canned data (see below) |
| `Models/SallingModels.cs` | The JSON response shape |
| `Pages/Index.cshtml(.cs)` | Filters, grouping, sorting, and the view |

## Working without a key

Set `"UseSampleData": true` under `Salling` to serve built-in demo data. It
works by adding a `DelegatingHandler` to the `HttpClient` pipeline that
answers every request itself, so no code outside `Program.cs` knows the
difference — useful for styling work and for staying off the rate limit.

## Notes

- Responses are cached in memory for 60 seconds (`Salling:CacheDuration`), so
  hitting Refresh repeatedly won't burn through the rate limit.
- Stores typically reduce in the morning and again before closing, so an empty
  list at midday is normal rather than a bug.
- `radius` on the geo variant of the endpoint (`SallingClient.GetNearbyAsync`)
  is documented in metres; the UI doesn't use it yet, postcodes cover the same
  ground more predictably.
