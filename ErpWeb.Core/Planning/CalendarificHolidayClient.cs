using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public sealed class CalendarificHolidayCandidate
{
    public DateTime DateOff { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? State { get; init; }
    public int Year { get; init; }
}

public interface ICalendarificHolidayClient
{
    bool IsEnabled { get; }
    Task<PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>> FetchAsync(
        int year, string countryCode = "MY", string? state = null, CancellationToken ct = default);
}

/// <summary>
/// Optional Calendarific client. Disabled without Calendarific:ApiKey.
/// Timeout never blocks callers — returns soft failure.
/// </summary>
public sealed class CalendarificHolidayClient : ICalendarificHolidayClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<CalendarificHolidayClient> _logger;

    public CalendarificHolidayClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<CalendarificHolidayClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_config["Calendarific:ApiKey"]);

    public async Task<PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>> FetchAsync(
        int year, string countryCode = "MY", string? state = null, CancellationToken ct = default)
    {
        var apiKey = _config["Calendarific:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>.Fail(
                PlanningErrorCode.ValidationFailed, "Calendarific is not configured.");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var client = _httpClientFactory.CreateClient(nameof(CalendarificHolidayClient));
            var url = $"https://calendarific.com/api/v2/holidays?api_key={Uri.EscapeDataString(apiKey)}&country={Uri.EscapeDataString(countryCode)}&year={year}";
            if (!string.IsNullOrWhiteSpace(state))
                url += $"&location={Uri.EscapeDataString(state)}";

            using var response = await client.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Calendarific HTTP {Status}", response.StatusCode);
                return PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>.Fail(
                    PlanningErrorCode.ValidationFailed, "Calendarific request failed.");
            }

            var payload = await response.Content.ReadFromJsonAsync<CalendarificResponse>(cancellationToken: cts.Token);
            var holidays = payload?.Response?.Holidays ?? [];
            var rows = holidays
                .Where(h => h.Date?.Datetime is not null)
                .Select(h =>
                {
                    var dt = h.Date!.Datetime!;
                    return new CalendarificHolidayCandidate
                    {
                        DateOff = new DateTime(dt.Year, dt.Month, dt.Day),
                        Description = h.Name ?? "Holiday",
                        State = state,
                        Year = year
                    };
                })
                .DistinctBy(x => x.DateOff)
                .OrderBy(x => x.DateOff)
                .ToList();

            return PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>.Ok(rows);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Calendarific timed out for {Year}/{Country}", year, countryCode);
            return PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>.Fail(
                PlanningErrorCode.ValidationFailed, "Calendarific timed out — try again or enter holidays manually.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calendarific fetch failed");
            return PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>.Fail(
                PlanningErrorCode.ValidationFailed, "Calendarific unavailable — enter holidays manually.");
        }
    }

    private sealed class CalendarificResponse
    {
        [JsonPropertyName("response")]
        public CalendarificInner? Response { get; set; }
    }

    private sealed class CalendarificInner
    {
        [JsonPropertyName("holidays")]
        public List<CalendarificHolidayDto>? Holidays { get; set; }
    }

    private sealed class CalendarificHolidayDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("date")]
        public CalendarificDateDto? Date { get; set; }
    }

    private sealed class CalendarificDateDto
    {
        [JsonPropertyName("datetime")]
        public CalendarificDateTimeDto? Datetime { get; set; }
    }

    private sealed class CalendarificDateTimeDto
    {
        [JsonPropertyName("year")] public int Year { get; set; }
        [JsonPropertyName("month")] public int Month { get; set; }
        [JsonPropertyName("day")] public int Day { get; set; }
    }
}
