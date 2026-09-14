using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;

namespace SeoLoodoi.Api.Tests;

/// <summary>
/// The product ships in 11 languages; both registration and profile update must
/// accept every supported language code and reject unknown ones.
/// </summary>
[Collection("api")]
public sealed class LocalizationContractTests(ApiFixture fixture)
{
    public static readonly TheoryData<string> Supported = new()
    {
        "fa", "en", "ar", "zh", "es", "fr", "de", "ru", "pt", "tr", "hi",
    };

    [Theory]
    [MemberData(nameof(Supported))]
    public async Task UpdateProfile_AcceptsEverySupportedLanguage(string language)
    {
        var me = await fixture.Owner.Client.GetFromJsonAsync<ProfileEnvelope>("/api/account/me");

        var response = await fixture.Owner.Client.PutAsJsonAsync("/api/account/me", new
        {
            displayName = me!.DisplayName,
            companyName = me.CompanyName,
            preferredLanguage = language,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"language '{language}' is a supported product language");
        var updated = await response.Content.ReadFromJsonAsync<ProfileEnvelope>();
        updated!.PreferredLanguage.Should().Be(language);
    }

    [Fact]
    public async Task UpdateProfile_RejectsUnknownLanguages()
    {
        var me = await fixture.Owner.Client.GetFromJsonAsync<ProfileEnvelope>("/api/account/me");

        var response = await fixture.Owner.Client.PutAsJsonAsync("/api/account/me", new
        {
            displayName = me!.DisplayName,
            companyName = me.CompanyName,
            preferredLanguage = "xx",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_AcceptsNonEnglishSupportedLanguages()
    {
        var anonymous = new SeoLoodoiFactory().CreateClient();
        var email = $"lang-{Guid.NewGuid():N}@test.loodoi.example";

        var response = await anonymous.PostAsJsonAsync("/api/account/register", new
        {
            fullName = "Test Lang",
            email,
            companyName = (string?)null,
            password = "Secure@2026x",
            confirmPassword = "Secure@2026x",
            acceptTerms = true,
            preferredLanguage = "tr",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<RegisterEnvelope>();
        body!.PreferredLanguage.Should().Be("tr", "registration must keep any supported language, not collapse it to fa/en");
    }

    private sealed record ProfileEnvelope(Guid Id, string Email, string DisplayName, string? CompanyName, string PreferredLanguage);
    private sealed record RegisterEnvelope(Guid Id, string PreferredLanguage);
}
