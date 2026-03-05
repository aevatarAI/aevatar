using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Aevatar.App.Application.Auth;

namespace Aevatar.App.Application.Tests;

public sealed class TrialAuthTests
{
    private const string Secret = "test-secret-that-is-at-least-32-chars!!";

    private static AppAuthService CreateService(
        string? trialSecret = Secret, bool trialEnabled = true, string? firebaseProjectId = null)
    {
        var options = Options.Create(new AppAuthOptions
        {
            TrialTokenSecret = trialSecret ?? "",
            TrialAuthEnabled = trialEnabled,
            FirebaseProjectId = firebaseProjectId ?? "",
        });
        return new AppAuthService(options, NullLogger<AppAuthService>.Instance);
    }

    private static string GenerateTrialToken(
        string sub, string email, string? secret = Secret, DateTime? expires = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", sub),
            new("email", email),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = credentials,
        };

        if (expires.HasValue)
            descriptor.Expires = expires.Value;

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    [Fact]
    public async Task ValidToken_ReturnsAuthUserInfo()
    {
        var svc = CreateService();
        var token = GenerateTrialToken("user-1", "test@example.com");

        var result = await svc.ValidateTokenAsync(token);

        result.Should().NotBeNull();
        result!.Id.Should().Be("user-1");
        result.Email.Should().Be("test@example.com");
        result.Provider.Should().Be("trial");
        result.ProviderId.Should().Be("user-1");
        result.EmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task NoExpiration_IsAccepted()
    {
        var svc = CreateService();
        var token = GenerateTrialToken("user-2", "noexp@test.com");

        var result = await svc.ValidateTokenAsync(token);

        result.Should().NotBeNull();
        result!.Id.Should().Be("user-2");
    }

    [Fact]
    public async Task WrongSecret_ReturnsNull()
    {
        var svc = CreateService();
        var token = GenerateTrialToken("user-3", "wrong@test.com",
            secret: "different-secret-that-is-also-32-chars!");

        var result = await svc.ValidateTokenAsync(token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MissingSub_ReturnsNull()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("email", "nosub@test.com")]),
            SigningCredentials = credentials,
        };
        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(handler.CreateToken(descriptor));

        var svc = CreateService();
        var result = await svc.ValidateTokenAsync(token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MissingEmail_ReturnsNull()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "user-no-email")]),
            SigningCredentials = credentials,
        };
        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(handler.CreateToken(descriptor));

        var svc = CreateService();
        var result = await svc.ValidateTokenAsync(token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TrialDisabled_ReturnsNull()
    {
        var svc = CreateService(trialEnabled: false);
        var token = GenerateTrialToken("user-4", "disabled@test.com");

        var result = await svc.ValidateTokenAsync(token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task EmptySecret_ReturnsNull()
    {
        var svc = CreateService(trialSecret: "");
        var token = GenerateTrialToken("user-5", "empty@test.com");

        var result = await svc.ValidateTokenAsync(token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task EmailIsLowercased()
    {
        var svc = CreateService();
        var token = GenerateTrialToken("user-6", "Upper.Case@Example.COM");

        var result = await svc.ValidateTokenAsync(token);

        result.Should().NotBeNull();
        result!.Email.Should().Be("upper.case@example.com");
    }

    [Fact]
    public async Task GarbageToken_ReturnsNull()
    {
        var svc = CreateService();

        var result = await svc.ValidateTokenAsync("not.a.jwt");

        result.Should().BeNull();
    }
}
