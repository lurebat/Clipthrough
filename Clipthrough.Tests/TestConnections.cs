using Clipthrough.Database;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Tests;

/// <summary>
/// How tests get a connection to a database file they arrange or inspect by
/// hand: through the app's factory, so it carries the cipher recipe.
/// </summary>
/// <remarks>
/// A test that built its own connection would create an encrypted library in
/// SQLite3 Multiple Ciphers' default cipher instead of SQLCipher 4. Such a test
/// reads its own file back and stays green while the app cannot open it - the
/// one failure the cipher recipe exists to prevent, hidden by the test that
/// should show it. ConnectionConstructionGuardTests therefore rejects a
/// hand-built connection in tests too. Tests keep describing a
/// connection with a connection string; only the data source, key, mode,
/// pooling and foreign-keys settings are honoured, because those are the ones
/// the factory takes.
/// </remarks>
internal static class TestConnections
{
    public static SqliteConnection Create(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return SqliteConnectionFactory.Create(
            builder.DataSource,
            string.IsNullOrEmpty(builder.Password) ? null : builder.Password,
            builder.Mode,
            builder.Pooling,
            builder.ForeignKeys);
    }
}