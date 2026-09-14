using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Ai;
using N4Sentinel.Infrastructure.Security;
using System.Security.Claims;
using System.Threading.RateLimiting;

namespace N4Sentinel.Web.Voice;

public static class VoiceSpeechEndpoints
{
    public static IServiceCollection AddVoiceSpeech(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VoiceSpeechOptions>(configuration.GetSection(VoiceSpeechOptions.SectionName));
        services.AddHttpClient<VoiceSpeechService>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddRateLimiter(options => options.AddPolicy("voice-speech", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                })));
        return services;
    }

    public static void MapVoiceSpeech(this WebApplication app)
    {
        var group = app.MapGroup("/api/voice").RequireAuthorization(N4Policies.PeutConsulter);

        group.MapGet("/settings", (HttpContext context, IAntiforgery antiforgery, IOptions<VoiceSpeechOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var settings = options.Value;
            return Results.Ok(new
            {
                neuralVoiceConfigured = settings.IsConfigured,
                voiceLabel = settings.VoiceLabel,
                locale = settings.Locale,
                antiforgeryToken = antiforgery.GetAndStoreTokens(context).RequestToken
            });
        });

        group.MapPost("/speech", async (VoiceSpeechRequest input, HttpContext context,
            IAntiforgery antiforgery, VoiceSpeechService speech, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { message = "Recharge la page avant de relancer la lecture." });
            }

            if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > VoiceSpeechService.MaxTextLength)
                return Results.BadRequest(new { message = "La réponse vocale est vide ou trop longue." });

            var audio = await speech.SynthesizeAsync(input.Text, ct);
            return audio is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.File(audio, "audio/mpeg");
        }).RequireRateLimiting("voice-speech");
    }
}

public sealed record VoiceSpeechRequest(string? Text);
