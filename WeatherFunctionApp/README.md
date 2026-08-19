# Weather Function App

An Azure Functions (isolated worker, .NET 8) app that provides a simple weather
forecast for a location, used by the "Weather" agent in the main chat app.

## Endpoint

`GET/POST /api/GetWeather?location={city}`

Returns:

```json
{
  "location": "London",
  "description": "light rain",
  "temperature": 14.2
}
```

## Configuration

Set the following application setting (locally in `local.settings.json`, or in
Azure Function App > Configuration):

| Setting              | Description                                            |
| -------------------- | ------------------------------------------------------- |
| `OPENWEATHER_API_KEY` | API key for https://openweathermap.org/api               |

Copy `local.settings.sample.json` to `local.settings.json` and fill in your key
to run locally. `local.settings.json` is git-ignored because it can contain secrets.

## Wiring it up to the main app

In the main app's configuration (see `azure-appsettings.sample.json` in the repo
root), set:

- `WEATHER_FUNCTION_APP_URL` – the deployed function URL, e.g.
  `https://<your-function-app>.azurewebsites.net/api/GetWeather`
- `WEATHER_FUNCTION_APP_KEY` – the function key, if the function is not anonymous.

## Run locally

```bash
func start
```

(requires the [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local))
