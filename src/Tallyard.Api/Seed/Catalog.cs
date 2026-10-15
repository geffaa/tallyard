namespace Tallyard.Api.Seed;

/// <summary>Word lists for the fictional demo catalog. Brands and companies are invented.</summary>
static class Catalog
{
    public record CategorySpec(string Name, string Prefix, bool Perishable);

    public static readonly CategorySpec[] Categories =
    [
        new("Beverages", "BEV", false),
        new("Snacks", "SNK", false),
        new("Dairy & Chilled", "DRY", true),
        new("Pantry", "PAN", false),
        new("Household", "HHD", false),
        new("Personal Care", "PRC", false),
    ];

    static readonly Dictionary<string, (string Item, string[] Sizes, string Unit, int[] PerCase, decimal Low, decimal High)[]> ItemsByCategory = new()
    {
        ["Beverages"] =
        [
            ("Sparkling Water", ["500 ml", "1 L"], "bottle", [12, 24], 0.6m, 1.4m),
            ("Cold Brew Coffee", ["250 ml", "330 ml"], "can", [12, 24], 1.6m, 2.8m),
            ("Orange Juice", ["1 L", "2 L"], "carton", [6, 12], 2.2m, 4.5m),
            ("Green Tea", ["500 ml"], "bottle", [24], 0.9m, 1.6m),
            ("Ginger Ale", ["355 ml"], "can", [24], 0.5m, 0.9m),
        ],
        ["Snacks"] =
        [
            ("Sea Salt Chips", ["150 g", "250 g"], "bag", [12, 24], 1.8m, 3.4m),
            ("Dark Chocolate Bar", ["100 g"], "bar", [24, 48], 1.5m, 2.9m),
            ("Trail Mix", ["200 g", "400 g"], "bag", [12], 3.2m, 6.5m),
            ("Rice Crackers", ["120 g"], "pack", [24], 1.2m, 2.1m),
            ("Oat Cookies", ["300 g"], "pack", [12], 2.4m, 3.9m),
        ],
        ["Dairy & Chilled"] =
        [
            ("Greek Yogurt", ["500 g", "1 kg"], "tub", [6, 12], 2.6m, 5.2m),
            ("Cheddar Block", ["250 g", "500 g"], "pack", [12], 3.5m, 6.8m),
            ("Whole Milk", ["1 L", "2 L"], "carton", [6, 12], 1.1m, 2.4m),
            ("Salted Butter", ["250 g"], "pack", [20], 2.9m, 4.1m),
        ],
        ["Pantry"] =
        [
            ("Basmati Rice", ["1 kg", "5 kg"], "bag", [10, 4], 2.5m, 11.0m),
            ("Penne Pasta", ["500 g"], "pack", [20], 0.9m, 1.7m),
            ("Olive Oil", ["500 ml", "1 L"], "bottle", [12, 6], 5.5m, 11.5m),
            ("Tomato Passata", ["700 g"], "jar", [12], 1.4m, 2.6m),
            ("Rolled Oats", ["1 kg"], "bag", [10], 2.1m, 3.6m),
        ],
        ["Household"] =
        [
            ("Dish Soap", ["500 ml", "1 L"], "bottle", [12], 1.6m, 3.4m),
            ("Laundry Pods", ["32 ct", "60 ct"], "box", [6, 4], 7.5m, 14.0m),
            ("Paper Towels", ["6 roll"], "pack", [4], 5.2m, 7.9m),
            ("Trash Bags", ["30 ct"], "box", [12], 4.1m, 6.3m),
        ],
        ["Personal Care"] =
        [
            ("Hand Soap", ["250 ml"], "bottle", [12], 1.9m, 3.1m),
            ("Toothpaste", ["100 ml"], "tube", [24], 1.7m, 3.3m),
            ("Shampoo", ["400 ml"], "bottle", [12], 3.4m, 6.4m),
            ("Body Lotion", ["250 ml"], "bottle", [12], 3.9m, 7.2m),
        ],
    };

    static readonly string[] Brands =
    [
        "Bluecrest", "Fenwick", "Larkspur", "Oakhollow", "Brightmoor", "Calder", "Wrenfield", "Saltmarsh",
        "Northvale", "Ambergate", "Kestrel", "Pinecove", "Hollis", "Redfern", "Marlow", "Silverbrook",
    ];

    /// <summary>Brand × item × size combinations in a shuffled but repeatable order.</summary>
    public static IEnumerable<(string Brand, string Category, string Item, string Size, string Unit, int PerCase, decimal Price)> Items(Random rng)
    {
        var all = (from cat in ItemsByCategory
                   from it in cat.Value
                   from brand in Brands
                   from s in it.Sizes.Select((size, i) => (size, i))
                   select (Brand: brand, Category: cat.Key, it.Item, Size: s.size, it.Unit, PerCase: it.PerCase[Math.Min(s.i, it.PerCase.Length - 1)],
                           // Larger sizes cost more: each size gets its own slice of the price range.
                           Low: it.Low + (it.High - it.Low) * s.i / it.Sizes.Length,
                           High: it.Low + (it.High - it.Low) * (s.i + 1) / it.Sizes.Length))
                  .ToList();
        foreach (var x in all.OrderBy(_ => rng.Next()))
            yield return (x.Brand, x.Category, x.Item, x.Size, x.Unit, x.PerCase,
                Math.Round(x.Low + (x.High - x.Low) * (decimal)rng.NextDouble(), 2));
    }

    public static readonly string[] SupplierNames =
    [
        "Great Lakes Beverage Co.", "Prairie Foods Inc.", "Coastal Dairy Cooperative", "Summit Snack Brands",
        "Riverbend Household Supply", "Evergreen Personal Care", "Midland Pantry Partners", "Copperline Imports",
        "Ridgeway Paper Goods", "Blue Harbor Bottling", "Tallgrass Organics", "Keystone Consumer Goods",
    ];

    public static readonly string[] CustomerNames =
    [
        "Maple Street Market", "Corner Basket Grocers", "Fresh Fields Deli", "Lakeview Convenience", "Union Square Pantry",
        "Hillside Food Mart", "Westgate Café Group", "Bayside Mini Market", "Elm & Oak Grocers", "Sunrise Gas & Go",
        "Parkside Office Supply", "Grand Avenue Bodega", "Greenleaf Natural Foods", "Harborview Hotel", "Midtown Campus Store",
        "Riverside Hospital Café", "Old Town General Store", "Stonebridge Fitness Club", "Canal Street Kitchen", "Northside Daycare",
        "Ironworks Brewing", "Lantern Bakery", "Crescent Pharmacy", "Pioneer Hardware & Home", "Willow Creek Resort",
        "Summit Ridge School District", "Bluebird Diner", "Cobblestone Coffee", "Metro Transit Kiosks", "Highland Senior Living",
        "Sunset Strip Snacks", "Garden Gate Florists", "Brookside Vet Clinic", "Copper Kettle Catering", "Vista Cinemas",
        "Anchor Marina Store", "Redwood Office Park", "Foxglove Spa", "Granite Peak Lodge", "Quarry Lane Market",
    ];
}
