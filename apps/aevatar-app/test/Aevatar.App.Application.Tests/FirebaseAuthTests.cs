using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Aevatar.App.Application.Auth;

namespace Aevatar.App.Application.Tests;

public sealed class FirebaseAuthTests
{
    private static AppAuthService CreateService(string projectId = "test-project")
    {
        var options = Options.Create(new AppAuthOptions
        {
            FirebaseProjectId = projectId,
            TrialTokenSecret = "",
            TrialAuthEnabled = false,
        });
        return new AppAuthService(options, NullLogger<AppAuthService>.Instance);
    }

    [Fact]
    public async Task EmptyProjectId_SkipsFirebaseValidation()
    {
        var options = Options.Create(new AppAuthOptions
        {
            FirebaseProjectId = "",
            TrialTokenSecret = "",
            TrialAuthEnabled = false,
        });
        var svc = new AppAuthService(options, NullLogger<AppAuthService>.Instance);

        var result = await svc.ValidateTokenAsync("any.token.here");

        result.Should().BeNull("no providers configured, should return null");
    }

    [Fact]
    public async Task InvalidToken_ReturnsNull()
    {
        var svc = CreateService();

        var result = await svc.ValidateTokenAsync("not.a.valid.jwt.token");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredToken_ReturnsNull()
    {
        var svc = CreateService();

        var result = await svc.ValidateTokenAsync(
            "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ0ZXN0IiwiZW1haWwiOiJ0ZXN0QHRlc3QuY29tIiwiZXhwIjoxMDAwMDAwMDAwfQ.invalid-sig");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Firebase_FallsThrough_ToTrialWhenEnabled()
    {
        var trialSecret = "test-secret-that-is-at-least-32-chars!!";
        var options = Options.Create(new AppAuthOptions
        {
            FirebaseProjectId = "fake-project",
            TrialTokenSecret = trialSecret,
            TrialAuthEnabled = true,
        });
        var svc = new AppAuthService(options, NullLogger<AppAuthService>.Instance);

        var token = JwtHelper.GenerateHS256Token(trialSecret, "user-fb", "fb@test.com");

        var result = await svc.ValidateTokenAsync(token);

        result.Should().NotBeNull("should fall through to trial validation");
        result!.Provider.Should().Be("trial");
    }
}

internal static class JwtHelper
{
    public static string GenerateHS256Token(string secret, string sub, string email)
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", sub),
                new System.Security.Claims.Claim("email", email),
            ]),
            SigningCredentials = creds,
        };

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}
