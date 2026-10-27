using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlashCardsApp;

public sealed class AppUpdateStatus
{
    public bool UpdateAvailable { get; init; }
    public string? LatestVersionText { get; init; }
    public string? ReleaseUrl { get; init; }
}

public sealed class UpdateService
{
    private static readonly HttpClient HttpClient = new();
    private readonly string _releaseApiUrl;
    private readonly string _releasePageUrl;

    public UpdateService(string releaseApiUrl, string releasePageUrl)
    {
        _releaseApiUrl = releaseApiUrl;
        _releasePageUrl = releasePageUrl;
    }

    public async Task<AppUpdateStatus> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (Connectivity.Current?.NetworkAccess != NetworkAccess.Internet)
            return new AppUpdateStatus();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releaseApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("FlashCardsApp");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new AppUpdateStatus();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            var releaseUrl = root.TryGetProperty("html_url", out var htmlElement)
                ? htmlElement.GetString()
                : _releasePageUrl;

            if (string.IsNullOrWhiteSpace(tagName))
                return new AppUpdateStatus();

            var latestVersion = ParseVersion(tagName);
            var currentVersion = ParseVersion(AppInfo.Current.VersionString);
            if (latestVersion is null || currentVersion is null)
                return new AppUpdateStatus();

            return latestVersion > currentVersion
                ? new AppUpdateStatus
                {
                    UpdateAvailable = true,
                    LatestVersionText = tagName.Trim(),
                    ReleaseUrl = string.IsNullOrWhiteSpace(releaseUrl) ? _releasePageUrl : releaseUrl
                }
                : new AppUpdateStatus
                {
                    UpdateAvailable = false,
                    LatestVersionText = tagName.Trim(),
                    ReleaseUrl = string.IsNullOrWhiteSpace(releaseUrl) ? _releasePageUrl : releaseUrl
                };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            return new AppUpdateStatus();
        }
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var numbers = Regex.Matches(value, @"\d+")
            .Select(match => int.Parse(match.Value))
            .ToArray();

        if (numbers.Length == 0)
            return null;

        var major = numbers[0];
        var minor = numbers.Length > 1 ? numbers[1] : 0;
        var build = numbers.Length > 2 ? numbers[2] : 0;
        var revision = numbers.Length > 3 ? numbers[3] : 0;
        return new Version(major, minor, build, revision);
    }
}
