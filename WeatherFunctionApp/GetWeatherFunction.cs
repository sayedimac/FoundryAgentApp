using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WeatherFunctionApp;

/// <summary>
/// HTTP-triggered function that returns a simple weather forecast for a location,
/// using the OpenWeatherMap current-weather API.
/// </summary>
public class GetWeatherFunction
{
    private static readonly HttpClient HttpClient = new();

    private readonly ILogger<GetWeatherFunction> _logger;
    private readonly IConfiguration _configuration;

    public GetWeatherFunction(ILogger<GetWeatherFunction> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    [Function("GetWeather")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var location = query["location"];

        if (string.IsNullOrWhiteSpace(location))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Please provide a 'location' query parameter.");
            return badRequest;
        }

        var apiKey = _configuration["OPENWEATHER_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OPENWEATHER_API_KEY is not configured.");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Weather function is not configured. Set OPENWEATHER_API_KEY.");
            return response;
        }

        var requestUri = $"https://api.openweathermap.org/data/2.5/weather?q={Uri.EscapeDataString(location)}&appid={apiKey}&units=metric";

        try
        {
            using var apiResponse = await HttpClient.GetAsync(requestUri);
            var body = await apiResponse.Content.ReadAsStringAsync();

            if (!apiResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenWeatherMap call failed with status {Status}: {Body}", apiResponse.StatusCode, body);
                var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
                await errorResponse.WriteStringAsync($"Could not retrieve weather for '{location}'.");
                return errorResponse;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var description = root.GetProperty("weather")[0].GetProperty("description").GetString();
            var temperature = root.GetProperty("main").GetProperty("temp").GetDouble();
            var resolvedLocation = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : location;

            var result = req.CreateResponse(HttpStatusCode.OK);
            await result.WriteAsJsonAsync(new
            {
                location = resolvedLocation,
                description,
                temperature
            });
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving weather for {Location}", location);
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync($"Error retrieving weather for '{location}'.");
            return response;
        }
    }
}
