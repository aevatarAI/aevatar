using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Errors;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.App.Host.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users");

        group.MapGet("/me", async (
            [FromServices] IAppAuthContextAccessor authAccessor,
            [FromServices] IUserAppService userService) =>
        {
            var auth = authAccessor.RequireAuthContext();
            var userInfo = await userService.GetUserInfoAsync(auth.UserId);
            var user = userInfo.User;
            var profile = userInfo.Profile;

            return Results.Ok(new
            {
                user = new
                {
                    id = user.Id,
                    email = user.Email,
                    createdAt = user.CreatedAt?.ToDateTimeOffset().ToString("O")
                },
                profile = profile is not null
                    ? new
                    {
                        firstName = profile.FirstName,
                        lastName = profile.LastName,
                        interests = profile.Interests.ToList(),
                        purpose = profile.Purpose,
                        timezone = profile.Timezone,
                        notificationsEnabled = profile.NotificationsEnabled,
                        reminderTime = string.IsNullOrEmpty(profile.ReminderTime) ? null : profile.ReminderTime
                    }
                    : null,
                onboardingComplete = profile is not null
            });
        });

        group.MapPost("/me/profile", async (
            CreateProfileRequest request,
            [FromServices] IAppAuthContextAccessor authAccessor,
            [FromServices] IUserAppService userService) =>
        {
            var auth = authAccessor.RequireAuthContext();
            var purpose = request.PurposeStr ?? (request.Purpose is not null ? string.Join(", ", request.Purpose) : string.Empty);

            var profile = await userService.CreateProfileAsync(
                auth.UserId,
                request.FirstName,
                request.LastName,
                request.Gender,
                request.DateOfBirth,
                request.Timezone,
                request.Interests,
                purpose,
                request.NotificationsEnabled,
                request.ReminderTime);

            return Results.Created($"/api/users/me/profile", BuildProfileResponse(profile));
        });

        group.MapPatch("/me/profile", async (
            UpdateProfileRequest request,
            [FromServices] IAppAuthContextAccessor authAccessor,
            [FromServices] IUserAppService userService) =>
        {
            var auth = authAccessor.RequireAuthContext();
            var purpose = request.PurposeStr
                ?? (request.Purpose is not null ? string.Join(", ", request.Purpose) : null);
            var profile = await userService.UpdateProfileAsync(
                auth.UserId,
                request.FirstName,
                request.LastName,
                request.Gender,
                request.DateOfBirth,
                request.Interests,
                purpose,
                request.Timezone,
                request.NotificationsEnabled,
                request.ReminderTime);

            return Results.Ok(BuildProfileResponse(profile));
        });

        group.MapDelete("/me", async (
            [FromServices] IAppAuthContextAccessor authAccessor,
            [FromServices] IUserAppService userService,
            bool hard = false) =>
        {
            var auth = authAccessor.RequireAuthContext();
            await userService.DeleteAccountAsync(auth.UserId, hard);

            return Results.Ok(new
            {
                success = true,
                mode = hard ? "hard" : "soft",
                deletedAt = DateTimeOffset.UtcNow.ToString("O"),
                message = hard
                    ? "Account and all data permanently deleted"
                    : "Account anonymized and deactivated"
            });
        });
    }

    private static object BuildProfileResponse(Profile profile) => new
    {
        firstName = profile.FirstName,
        lastName = profile.LastName,
        interests = profile.Interests.ToList(),
        purpose = profile.Purpose,
        timezone = profile.Timezone,
        notificationsEnabled = profile.NotificationsEnabled,
        reminderTime = string.IsNullOrEmpty(profile.ReminderTime) ? null : profile.ReminderTime
    };
}

public sealed record CreateProfileRequest(
    string? FirstName = null, string? LastName = null, string? Gender = null,
    string? DateOfBirth = null,
    [property: JsonConverter(typeof(StringOrStringArrayConverter))] string[]? Interests = null,
    [property: JsonConverter(typeof(StringOrStringArrayConverter))] string[]? Purpose = null,
    string? PurposeStr = null, string? Timezone = null, bool? NotificationsEnabled = null,
    string? ReminderTime = null);

public sealed record UpdateProfileRequest(
    string? FirstName = null, string? LastName = null, string? Gender = null,
    string? DateOfBirth = null,
    [property: JsonConverter(typeof(StringOrStringArrayConverter))] string[]? Interests = null,
    [property: JsonConverter(typeof(StringOrStringArrayConverter))] string[]? Purpose = null,
    string? PurposeStr = null,
    string? Timezone = null, bool? NotificationsEnabled = null, string? ReminderTime = null);

internal sealed class StringOrStringArrayConverter : JsonConverter<string[]?>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                    list.Add(reader.GetString()!);
            }
            return list.ToArray();
        }

        throw new JsonException($"Expected string or string array, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
