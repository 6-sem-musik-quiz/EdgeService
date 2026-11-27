using System.IdentityModel.Tokens.Jwt;

namespace EdgeService.Api;

/// <summary>
/// SCRUM-197 (ADR-0005): which rate-limit bucket a request counts against.
/// A request with a JWT counts per user ("user:&lt;sub&gt;"), so many players behind one IP
/// (a classroom, or 50 load-test bots) each get their own quota. Anything else counts per IP.
/// </summary>
/// <remarks>
/// The token is read, not validated: the edge holds no signing key. That is fine because the key only
/// decides which quota is used. A forged sub gets a different quota, never access; the service behind
/// the edge still validates the token.
/// </remarks>
public static class RateLimitKey
{
    private static readonly JwtSecurityTokenHandler Handler = new();

    public static string For(HttpContext context)
    {
        var sub = ReadSub(BearerToken(context.Request));
        return sub is not null
            ? $"user:{sub}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    // REST calls send "Authorization: Bearer"; SignalR WebSockets send "?access_token=".
    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        var query = request.Query["access_token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    private static string? ReadSub(string? token)
    {
        if (string.IsNullOrEmpty(token) || !Handler.CanReadToken(token))
            return null;
        try
        {
            var sub = Handler.ReadJwtToken(token).Subject;
            return string.IsNullOrEmpty(sub) ? null : sub;
        }
        catch (ArgumentException)
        {
            return null; // looks like a JWT but is not valid JSON/base64
        }
    }
}
