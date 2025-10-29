using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Notifications.Api.Data;

public sealed class NpgsqlConnectionFactory : INpgsqlConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Notifications")
            ?? throw new InvalidOperationException("Connection string 'Notifications' is not configured.");
    }

    public async ValueTask<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
