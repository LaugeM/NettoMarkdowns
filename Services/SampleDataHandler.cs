using System.Net;
using System.Text;
using System.Text.Json;

namespace NettoMarkdowns.Services;

/// <summary>
/// Development-only: short-circuits the HttpClient pipeline and answers with canned
/// data shaped exactly like the real Food Waste response, so the UI can be worked on
/// without an API key (and without spending rate limit). Enabled by Salling:UseSampleData.
/// </summary>
public sealed class SampleDataHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = Build();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }

    private static string Build()
    {
        var now = DateTimeOffset.Now;
        DateTimeOffset Today(int hour) => new(DateTime.Today.AddHours(hour), now.Offset);

        object Item(string name, string category, decimal was, decimal now_, double stock,
                    string unit, DateTimeOffset ends) => new
        {
            offer = new
            {
                currency = "DKK",
                originalPrice = was,
                newPrice = now_,
                discount = was - now_,
                percentDiscount = Math.Round((double)((was - now_) / was) * 100, 1),
                stock,
                stockUnit = unit,
                startTime = Today(7),
                endTime = ends,
                lastUpdate = now
            },
            product = new
            {
                description = name,
                ean = Random.Shared.NextInt64(5_700_000_000_000, 5_799_999_999_999).ToString(),
                image = (string?)null,
                categories = new Dictionary<string, string> { ["da"] = category }
            }
        };

        object Store(string id, string name, string street, string zip, string city, object[] items) => new
        {
            store = new
            {
                id,
                name,
                brand = "netto",
                address = new { street, zip, city, country = "DK" },
                hours = new[]
                {
                    new { date = DateTime.Today.ToString("yyyy-MM-dd"), type = "Store",
                          open = Today(7), close = Today(22), closed = false }
                }
            },
            clearances = items
        };

        var payload = new[]
        {
            Store("sample-1", "Netto Nørrebrogade", "Nørrebrogade 155", "2200", "København N",
            [
                Item("Hakket oksekød 8-12% 400 g", "Kød", 42.00m, 15.00m, 4, "each", Today(22)),
                Item("Økologisk minimælk 1 l", "Mejeri", 13.50m, 7.00m, 11, "each", Today(20)),
                Item("Kyllingebryst 700 g", "Kød", 65.00m, 32.50m, 2, "each", Today(22)),
                Item("Rugbrød groft 950 g", "Brød", 18.95m, 9.00m, 6, "each", Today(21)),
                Item("Jordbær 400 g", "Frugt & grønt", 25.00m, 8.00m, 3, "each", Today(19)),
                Item("Lagret cheddar", "Mejeri", 55.00m, 38.50m, 1.4, "kg", Today(22))
            ]),
            Store("sample-2", "Netto Frederikssundsvej", "Frederikssundsvej 62", "2400", "København NV",
            [
                Item("Laksefilet 250 g", "Fisk", 49.95m, 20.00m, 5, "each", Today(20)),
                Item("Skyr vanilje 450 g", "Mejeri", 22.00m, 11.00m, 8, "each", Today(22)),
                Item("Bananer 1 kg", "Frugt & grønt", 16.00m, 6.00m, 9, "each", Today(21)),
                Item("Pålægschokolade", "Kolonial", 24.50m, 17.00m, 12, "each", now.AddDays(2)),
                Item("Frisk pasta tagliatelle", "Køl", 27.00m, 10.00m, 3, "each", Today(22))
            ]),
            Store("sample-3", "Netto Jagtvej", "Jagtvej 111", "2200", "København N",
            [
                Item("Kalkunbryst 400 g", "Kød", 39.95m, 18.00m, 3, "each", Today(21)),
                Item("Æblejuice 1 l", "Kolonial", 19.00m, 9.50m, 7, "each", now.AddDays(1)),
                Item("Cherrytomater 250 g", "Frugt & grønt", 14.00m, 5.00m, 4, "each", Today(20)),
                // Path-shaped label, to exercise CategoryTree's separator handling.
                Item("Vaniljeis 1 l", "Frost > Is", 34.00m, 15.00m, 2, "each", now.AddDays(3)),
                Item("Kyllingelår 1 kg", "Kød", 45.00m, 22.00m, 2, "each", Today(21)),
                // Multi-word categories inside one group — these are what broke when
                // category values were being split on spaces.
                Item("Pilsner 6-pak", "Øl & vin", 55.00m, 30.00m, 4, "each", now.AddDays(4)),
                Item("Formalet kaffe 400 g", "Kaffe & te", 45.00m, 25.00m, 3, "each", now.AddDays(5))
            ])
        };

        return JsonSerializer.Serialize(payload);
    }
}
