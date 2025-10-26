using Npgsql;

namespace Notifications.Api.Data;

public interface INpgsqlConnectionFactory
{
    ValueTask<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken);
}
