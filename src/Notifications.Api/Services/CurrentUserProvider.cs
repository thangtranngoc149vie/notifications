using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Notifications.Api.Infrastructure;

namespace Notifications.Api.Services;

public interface ICurrentUserProvider
{
    Guid GetUserId();
}

public sealed class CurrentUserProvider : ICurrentUserProvider
{
    private readonly IHttpContextAccessor _contextAccessor;

    public CurrentUserProvider(IHttpContextAccessor contextAccessor)
    {
        _contextAccessor = contextAccessor;
    }

    public Guid GetUserId()
    {
        var principal = _contextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("The current HTTP context is not available.");

        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? throw new InvalidOperationException("User identifier claim is missing.");

        if (!Guid.TryParse(userIdValue, out var userId))
        {
            throw new InvalidOperationException("User identifier claim is not a valid GUID.");
        }

        return userId;
    }
}
