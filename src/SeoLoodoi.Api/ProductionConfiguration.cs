namespace SeoLoodoi.Api;

/// <summary>
/// Fail-fast checks for settings that would make a production deployment unsafe
/// or non-functional. Development and test hosts deliberately do not call this.
/// </summary>
public static class ProductionConfiguration
{
    public static void Validate(IConfiguration configuration)
    {
        var errors = new List<string>();
        var provider = configuration["DatabaseProvider"];
        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
            errors.Add("DatabaseProvider cannot be InMemory in Production.");

        var connection = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connection) || connection.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            errors.Add("ConnectionStrings:Postgres must be supplied from a production secret and cannot contain CHANGE_ME.");

        RequireHttpsOrigin(configuration, "Application:PublicBaseUrl", errors);
        RequireHttpsOrigin(configuration, "Application:WebBaseUrl", errors);

        var origins = configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0)
            errors.Add("AllowedOrigins must contain at least one explicit HTTPS origin.");
        foreach (var origin in origins)
        {
            if (!TryHttpsOrigin(origin))
                errors.Add($"AllowedOrigins contains an invalid production origin: '{origin}'.");
        }

        if (string.IsNullOrWhiteSpace(configuration["DataProtection:KeysPath"]))
            errors.Add("DataProtection:KeysPath must point to durable storage in Production.");

        var emailEnabled = configuration.GetValue<bool>("Email:Enabled");
        if (configuration.GetValue<bool>("Identity:RequireConfirmedEmail") && !emailEnabled)
            errors.Add("Email must be enabled when confirmed email is required.");
        if (emailEnabled)
        {
            Require(configuration, "Email:Host", errors);
            Require(configuration, "Email:FromAddress", errors);
            if (configuration.GetValue<int>("Email:Port") is < 1 or > 65535)
                errors.Add("Email:Port must be between 1 and 65535.");
        }

        if (configuration.GetValue<bool>("AI:Enabled"))
        {
            RequireHttpsUrl(configuration, "AI:Endpoint", errors);
            Require(configuration, "AI:ApiKey", errors);
            Require(configuration, "AI:Model", errors);
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid production configuration:" + Environment.NewLine + "- " + string.Join(Environment.NewLine + "- ", errors));
    }

    private static void Require(IConfiguration configuration, string key, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configuration[key])) errors.Add($"{key} is required.");
    }

    private static void RequireHttpsOrigin(IConfiguration configuration, string key, ICollection<string> errors)
    {
        if (!TryHttpsOrigin(configuration[key])) errors.Add($"{key} must be an absolute HTTPS origin without a path, query, or fragment.");
    }

    private static void RequireHttpsUrl(IConfiguration configuration, string key, ICollection<string> errors)
    {
        if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            errors.Add($"{key} must be an absolute HTTPS URL without embedded credentials.");
    }

    private static bool TryHttpsOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(uri.Host)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo);
}
