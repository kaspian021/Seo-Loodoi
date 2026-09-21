using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace SeoLoodoi.Api.Tests;

public sealed class ProductionConfigurationTests
{
    [Fact]
    public void Validate_CompleteConfiguration_Succeeds()
    {
        var act = () => ProductionConfiguration.Validate(Configuration());
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("DatabaseProvider", "InMemory", "cannot be InMemory")]
    [InlineData("ConnectionStrings:Postgres", "Host=db;Password=CHANGE_ME", "cannot contain CHANGE_ME")]
    [InlineData("Application:PublicBaseUrl", "http://api.example.test", "absolute HTTPS origin")]
    [InlineData("Application:WebBaseUrl", "https://app.example.test/path", "absolute HTTPS origin")]
    [InlineData("AllowedOrigins:0", "*", "invalid production origin")]
    [InlineData("DataProtection:KeysPath", "", "durable storage")]
    public void Validate_UnsafeCoreSetting_FailsWithActionableMessage(string key, string value, string expected)
    {
        var act = () => ProductionConfiguration.Validate(Configuration(new() { [key] = value }));
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Validate_ConfirmedEmailWithoutDelivery_Fails()
    {
        var configuration = Configuration(new()
        {
            ["Identity:RequireConfirmedEmail"] = "true",
            ["Email:Enabled"] = "false"
        });

        var act = () => ProductionConfiguration.Validate(configuration);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Email must be enabled*");
    }

    [Fact]
    public void Validate_EnabledEmailRequiresCompleteSettings()
    {
        var configuration = Configuration(new()
        {
            ["Email:Enabled"] = "true",
            ["Email:Host"] = "",
            ["Email:FromAddress"] = "",
            ["Email:Port"] = "0"
        });

        var act = () => ProductionConfiguration.Validate(configuration);
        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Email:Host is required").And.Contain("Email:FromAddress is required").And.Contain("Email:Port must be");
    }

    [Fact]
    public void Validate_EnabledAiRequiresHttpsEndpointKeyAndModel()
    {
        var configuration = Configuration(new()
        {
            ["AI:Enabled"] = "true",
            ["AI:Endpoint"] = "http://provider.example.test/v1",
            ["AI:ApiKey"] = "",
            ["AI:Model"] = ""
        });

        var act = () => ProductionConfiguration.Validate(configuration);
        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("AI:Endpoint").And.Contain("AI:ApiKey is required").And.Contain("AI:Model is required");
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "Postgres",
            ["ConnectionStrings:Postgres"] = "Host=db;Database=seo;Username=seo;Password=secret",
            ["Application:PublicBaseUrl"] = "https://app.example.test",
            ["Application:WebBaseUrl"] = "https://app.example.test",
            ["AllowedOrigins:0"] = "https://app.example.test",
            ["DataProtection:KeysPath"] = "/keys",
            ["Identity:RequireConfirmedEmail"] = "false",
            ["Email:Enabled"] = "false",
            ["AI:Enabled"] = "false"
        };
        if (overrides is not null)
            foreach (var pair in overrides) values[pair.Key] = pair.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
