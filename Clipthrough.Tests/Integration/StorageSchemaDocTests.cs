using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Keeps the agent-facing storage reference honest about which indexes exist.
/// </summary>
/// <remarks>
/// Agents are told to read `clipthrough-storage-schema.md` before touching
/// persistence, so a stale claim there is worse than no claim. It had drifted in
/// four places at once - an index renamed (`idx_clips_embedding_status` ->
/// `idx_clips_embedding_backlog`) and wrongly called partial, five indexes never
/// added, the FTS tokenizer still described as unicode61 long after it became
/// trigram, and a collation paragraph that said BINARY where the code says
/// NOCASE - because nothing compared the prose to the schema.
///
/// This checks the mechanically checkable half. Prose still has to be written
/// carefully; a name list does not.
/// </remarks>
public sealed class StorageSchemaDocTests
{
    private static string DocPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "copilot-cli-skills", "clipthrough-storage-schema.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "clipthrough-storage-schema.md was not found above " + AppContext.BaseDirectory +
            "; this test cannot silently pass without it.");
    }

    private static string[] IndexNames(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // sqlite_autoindex_* are created implicitly for UNIQUE/PK constraints and
        // are not ours to document.
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_autoindex%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    [Fact]
    public async Task EveryIndexTheSchemaCreates_IsNamedInTheStorageDoc()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var doc = await File.ReadAllTextAsync(DocPath());
        var actual = IndexNames(scope);
        Assert.NotEmpty(actual);

        var undocumented = actual
            .Where(name => !doc.Contains(name, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "These indexes exist but the storage doc does not mention them: " + string.Join(", ", undocumented));
    }

    [Fact]
    public async Task EveryIndexTheStorageDocNames_IsAnIndexTheSchemaCreates()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var doc = await File.ReadAllTextAsync(DocPath());
        var actual = IndexNames(scope).ToHashSet(StringComparer.Ordinal);

        var claimed = Regex
            .Matches(doc, @"\bidx_[a-z0-9_]+\b", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(claimed);

        // The doc names two indexes it says were deliberately dropped, so the
        // reader knows not to reintroduce them. Their absence is the point.
        string[] documentedAsRemoved = ["idx_clips_paste_count", "idx_clips_byte_size", "idx_clips_hash"];

        var phantom = claimed
            .Where(name => !actual.Contains(name) && !documentedAsRemoved.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            phantom.Length == 0,
            "The storage doc names indexes that do not exist: " + string.Join(", ", phantom));

        // Guards the exemption list above from outliving the claim it excuses:
        // if a dropped index comes back, the exemption is hiding a real match.
        var resurrected = documentedAsRemoved.Where(actual.Contains).ToArray();
        Assert.True(
            resurrected.Length == 0,
            "The doc says these were dropped, but they exist: " + string.Join(", ", resurrected));
    }

    /// <summary>
    /// The tokenizer is the one schema fact the search code reasons about
    /// directly - token length is measured against trigram's 3-character
    /// shingles - so a doc describing a different tokenizer misleads about
    /// behaviour, not just about a name.
    /// </summary>
    [Fact]
    public async Task TheStorageDoc_DescribesTheTokenizerTheSchemaActuallyUses()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        string definition;
        using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='clips_fts';";
            definition = command.ExecuteScalar() as string ?? string.Empty;
        }

        Assert.Contains("trigram", definition, StringComparison.OrdinalIgnoreCase);

        var doc = await File.ReadAllTextAsync(DocPath());
        Assert.Contains("tokenize='trigram'", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenize='unicode61", doc, StringComparison.Ordinal);
    }
}
