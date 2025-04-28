using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using CsvHelper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;
using Serilog;

namespace FakeStoreApp
{
    class Product
    {
        public int? Id { get; set; }
        public string? Title { get; set; }
        public double Price { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
    }

    class EnrichedProduct
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public double OriginalPrice { get; set; }
        public double DiscountedPrice { get; set; }
        public int Stock { get; set; }
        public double PopularityScore { get; set; }
    }

    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
        private static readonly Random random = new Random();

        // Environmental variables
        private static readonly string baseUrl = "https://fakestoreapi.com";
        private static readonly int cacheTime = 5;
        private static readonly int retryCount = 3;
        private static readonly int minDiscount = 5;
        private static readonly int maxDiscount = 20;
        private static readonly int minStock = 0;
        private static readonly int maxStock = 100;
        private static readonly string fileName = "grouped_products";


        // Get data, transform it, save to file
        // Run using "dotnet run --format <json OR csv>"
        static async Task Main(string[] args)
        {
            string fileFolder = Path.Combine(Directory.GetCurrentDirectory(), "files");
            string logFolder = Path.Combine(Directory.GetCurrentDirectory(), "logs");

            var formatOption = new Option<string>(
                "--format",
                description: "Output format (json or csv)",
                getDefaultValue: () => "json");

            var rootCommand = new RootCommand("FakeStoreApp fetches product data and saves it to a file.");
            rootCommand.AddOption(formatOption);

            // Create Logger
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .WriteTo.File(Path.Combine(logFolder, "log.txt"))
                .CreateLogger();

            try
            {    
                JArray products = await GetProductsJson();
                var groupedProducts = TransformData(products);

                // Check if directory exists. Create if not
                if (!Directory.Exists(fileFolder))
                {
                    Directory.CreateDirectory(fileFolder);
                }

                // Command line arguments. TODO: Add other arguments. Better way to do this? 
                rootCommand.SetHandler(
                    (formatOption) =>
                    {
                        switch (formatOption)
                        {
                            case "json":
                                SaveToJson(groupedProducts, Path.Combine(fileFolder, $"{fileName}.json"));
                                break;
                            case "csv":
                                SaveToCsv(groupedProducts, Path.Combine(fileFolder, $"{fileName}.csv"));
                                break;
                            // TODO: write XML
                            //case "xml":
                            //    SaveToXml(groupedProducts, filePath);
                            //    break;
                            default:
                                Log.Error("Invalid format specified. Use --format json or --format csv");
                                break;
                        }
                    },
                    formatOption
                );

                await rootCommand.InvokeAsync(args);

                Log.Information("Completed");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred during the execution of the program.");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
        
        // GET all products from API
        // Cache responses
        private static async Task<JArray> GetProductsJson()
        {
            string endpointURL = new Uri(new Uri(baseUrl), "products").ToString();
            string cacheKey = "get_products";

            // Check cache values
            if (cache.TryGetValue(cacheKey, out JArray products) && products != null)
            {
                Log.Information("Products retrieved from cache");
                return products;
            }

            // Re-try if request fails
            var retryPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
                .WaitAndRetryAsync(
                    retryCount,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Add delay after each retry
                    (response, timespan, retryCount, context) =>
                    {
                        Log.Warning($"Retry {retryCount}: HTTP Status {response.Result.StatusCode} after {timespan.TotalSeconds} seconds.");
                    }
                );

            // Send request using re-tries
            HttpResponseMessage response;

            try
            {
                response = await retryPolicy.ExecuteAsync(() =>
                {
                    // Create new request message for each retry attempt
                    var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpointURL);

                    // TODO: Add e.g. API-key
                    requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    return httpClient.SendAsync(requestMessage);
                });
            }
            catch (HttpRequestException ex)
            {
                Log.Error(ex, "HTTP request failed.");
                throw;
            }

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    // Parse response
                    string json = await response.Content.ReadAsStringAsync();
                    JArray productsArr = JArray.Parse(json);

                    // Add result to cache, 5 min
                    cache.Set(cacheKey, productsArr, TimeSpan.FromMinutes(cacheTime));
                    Log.Information("GET Products OK");

                    return productsArr ?? new JArray();
                }
                catch (JsonReaderException ex)
                {
                    Log.Error(ex, "Failed to parse JSON response");
                    throw;
                }
            }
            else
            {
                Log.Error($"API request failed with status code: {response.StatusCode}");
                throw new Exception($"Failed to fetch data from the API. Status code: {response.StatusCode}");
            }
        }

        // Transform product data
        // Add extra fields and group products by category and price
        private static Dictionary<string, List<EnrichedProduct>> TransformData(JArray products)
        {
            var groupedProducts = products
                .GroupBy(p => p["category"]?.ToString() ?? string.Empty)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(p => p["price"]?.Value<double>() ?? 0)
                        .Select(p => new EnrichedProduct
                        {
                            Id = p["id"]?.Value<int>() ?? 0,
                            Title = p["title"]?.ToString() ?? string.Empty,
                            OriginalPrice = p["price"]?.Value<double>() ?? 0,
                            DiscountedPrice = Math.Round((p["price"]?.Value<double>() ?? 0) * (1 - random.Next(minDiscount, maxDiscount + 1) / 100.0), 2),
                            Stock = random.Next(minStock, maxStock + 1),
                            PopularityScore = Math.Round((((p["price"]?.Value<double>() ?? 0) + random.Next(minStock, maxStock + 1)) / 2), 2) // TODO: better way to calculate this
                        })
                        .ToList()
                );
        
            Log.Information("Products transformed successfully");

            return groupedProducts;
        }

        // Write JSON file
        private static void SaveToJson(Dictionary<string, List<EnrichedProduct>> groupedProducts, string filePath)
        {
            try
            {
                string json = JsonConvert.SerializeObject(groupedProducts, Formatting.Indented);
                File.WriteAllText(filePath, json);
                Log.Information($"JSON file saved to {filePath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save JSON file");
                throw;
            }
        }

        // Write CSV file
        private static void SaveToCsv(Dictionary<string, List<EnrichedProduct>> groupedProducts, string filePath)
        {
            try
            {
                using var writer = new StreamWriter(filePath);
                using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

                csv.WriteHeader<EnrichedProduct>();
                csv.NextRecord();

                // Loop Categories and Products. Write each line to CSV
                foreach (var category in groupedProducts)
                {
                    foreach (var product in category.Value)
                    {
                        csv.WriteRecord(product);
                        csv.NextRecord();
                    }
                }

                Log.Information($"CSV file saved to {filePath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save CSV file");
                throw;
            }
        }
    }
}