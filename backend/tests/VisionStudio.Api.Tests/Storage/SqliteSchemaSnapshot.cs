using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VisionStudio.Api.Tests.Storage;

internal static class SqliteSchemaSnapshot
{
    public static async Task<SortedDictionary<string, string>> ReadAsync(
        SqliteConnection connection,
        IReadOnlySet<string>? excludedTables = null,
        IReadOnlySet<string>? equivalentUniqueIndexes = null)
    {
        var schema = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var objects = await QueryAsync(connection,
            "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name;");
        foreach (var item in objects.Where(x => x[0] == "table"))
        {
            var table = item[1]!;
            if (table == "sqlite_sequence" || excludedTables?.Contains(table) == true) continue;
            var prefix = $"table:{table}";
            var tokens = Tokens(item[3]!);
            if (tokens.Zip(tokens.Skip(1)).Any(pair => pair.First == "ON" && pair.Second == "CONFLICT"))
                throw new NotSupportedException($"Schema comparison must model the conflict policy on {table}.");
            var open = Array.IndexOf(tokens, "(");
            var close = ClosingParenthesis(tokens, open);
            schema[prefix] = Normalize(tokens[(close + 1)..]).TrimEnd(';');
            schema[$"{prefix}/autoincrement"] = tokens.Contains("AUTOINCREMENT").ToString();

            var definitions = SplitDefinitions(tokens[(open + 1)..close]);
            foreach (var column in await QueryAsync(connection,
                "SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo($name);", table))
            {
                var name = column[0]!;
                var key = $"{prefix}/column:{name}";
                schema[$"{key}/type"] = column[1]!.ToUpperInvariant();
                schema[$"{key}/notnull"] = column[2]!;
                schema[$"{key}/default"] = column[3] is { } value ? Normalize(Tokens(value)) : "<none>";
                schema[$"{key}/pk"] = column[4]!;
                schema[$"{key}/hidden"] = column[5]!;
                var definition = definitions.Single(x => x[0] == name.ToUpperInvariant());
                var collate = TopLevelIndexOf(definition, "COLLATE");
                schema[$"{key}/collation"] = collate < 0 ? "BINARY" : definition[collate + 1];
                if (column[5] != "0")
                {
                    var expression = Array.IndexOf(definition, "AS");
                    schema[$"{key}/generated"] = Normalize(definition[(expression + 1)..]);
                }
            }

            var checks = new List<string>();
            for (var i = open + 1; i < close; i++)
            {
                if (tokens[i] != "CHECK" || tokens[i + 1] != "(") continue;
                var end = ClosingParenthesis(tokens, i + 1);
                checks.Add(Normalize(tokens[(i + 2)..end]));
                i = end;
            }
            schema[$"{prefix}/checks"] = string.Join(" | ", checks.Order(StringComparer.Ordinal));

            var foreignKeys = await QueryAsync(connection,
                "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete, match FROM pragma_foreign_key_list($name);", table);
            schema[$"{prefix}/foreignKeys"] = string.Join(" | ", foreignKeys.GroupBy(x => x[0])
                .Select(group => string.Join(";", group.OrderBy(x => int.Parse(x[1]!, CultureInfo.InvariantCulture))
                    .Select(x => string.Join(",", x.Skip(1)))))
                .Order(StringComparer.Ordinal));
            // SQLite's FK pragma omits deferred enforcement; associate timing with the child columns.
            schema[$"{prefix}/deferrability"] = string.Join(" | ", definitions
                .Where(x => x.Contains("REFERENCES"))
                .Select(x =>
                {
                    var foreignKey = Array.IndexOf(x, "FOREIGN");
                    var columns = foreignKey < 0 ? x[0] :
                        Normalize(x[(foreignKey + 3)..ClosingParenthesis(x, foreignKey + 2)]);
                    var index = Array.IndexOf(x, "DEFERRABLE");
                    var timing = index < 0 ? "IMMEDIATE" : Normalize(x[(x[index - 1] == "NOT" ? index - 1 : index)..]);
                    return columns + ":" + timing;
                }).Order(StringComparer.Ordinal));

            foreach (var index in await QueryAsync(connection,
                "SELECT name, \"unique\", origin, partial FROM pragma_index_list($name);", table))
            {
                var name = index[0]!;
                var columns = await QueryAsync(connection,
                    "SELECT seqno, cid, name, \"desc\", coll, \"key\" FROM pragma_index_xinfo($name) ORDER BY seqno;", name);
                var keyColumns = columns.Where(x => x[5] == "1").ToList();
                var implicitIndex = index[2] is "pk" or "u";
                var equivalentUnique = equivalentUniqueIndexes?.Contains(name) == true;
                if (equivalentUnique && (index[1] != "1" || index[2] != "c" || index[3] != "0"))
                    throw new InvalidOperationException($"Expected an unfiltered unique index: {name}");
                var identity = implicitIndex || equivalentUnique
                    ? $"{(index[2] == "pk" ? "pk" : "unique")}:{string.Join(",", keyColumns.Select(x => x[2]))}"
                    : name;
                var key = $"{prefix}/index:{identity}";
                schema.Add(key, $"unique={index[1]};partial={index[3]};" +
                    string.Join(";", keyColumns.Select(x => $"{x[2] ?? "<expression>"}:{x[3]}:{x[4]}")));
                var indexSql = objects.Single(x => x[0] == "index" && x[1] == name)[3];
                var indexTokens = indexSql is null ? [] : Tokens(indexSql);
                var where = Array.IndexOf(indexTokens, "WHERE");
                schema[$"{key}/filter"] = where < 0 ? "<none>" : Normalize(indexTokens[(where + 1)..]).TrimEnd(';');
                if (keyColumns.Any(x => x[1] == "-2"))
                    schema[$"{key}/expression"] = Normalize(indexTokens[(Array.IndexOf(indexTokens, "ON") + 2)..]).TrimEnd(';');
            }
        }
        foreach (var item in objects.Where(x => x[0] is "view" or "trigger"))
        {
            if (excludedTables?.Contains(item[2]!) == true) continue;
            schema[$"{item[0]}:{item[1]}"] = Normalize(Tokens(item[3]!)).TrimEnd(';');
        }
        return schema;
    }

    public static string[] Differences(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) =>
        expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal)
            .Where(key => !expected.TryGetValue(key, out var left) || !actual.TryGetValue(key, out var right) || left != right)
            .Select(key => $"{key}: SQL={expected.GetValueOrDefault(key, "<missing>")}; EF={actual.GetValueOrDefault(key, "<missing>")}")
            .ToArray();

    private static async Task<List<string?[]>> QueryAsync(SqliteConnection connection, string sql, string? name = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (name is not null) command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await reader.ReadAsync())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
    }

    private static string[] Tokens(string sql) =>
        Regex.Matches(sql, """'(?:''|[^'])*'|"(?:[^"]|"")*"|`(?:``|[^`])*`|\[[^\]]*\]|--[^\r\n]*|/\*[\s\S]*?\*/|[\w]+|[^\s]""")
            .Select(match => match.Value)
            .Where(token => !token.StartsWith("--", StringComparison.Ordinal) && !token.StartsWith("/*", StringComparison.Ordinal))
            .Select(token => token[0] switch
            {
                '\'' => token,
                '"' => token[1..^1].Replace("\"\"", "\"").ToUpperInvariant(),
                '`' => token[1..^1].Replace("``", "`").ToUpperInvariant(),
                '[' => token[1..^1].ToUpperInvariant(),
                _ => token.ToUpperInvariant()
            }).ToArray();

    private static string Normalize(IEnumerable<string> tokens) => string.Join(" ", tokens);

    private static int TopLevelIndexOf(string[] tokens, string value)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == value) return i;
            if (tokens[i] == "(") i = ClosingParenthesis(tokens, i);
        }
        return -1;
    }

    private static int ClosingParenthesis(string[] tokens, int open)
    {
        if (open < 0 || tokens[open] != "(") throw new InvalidOperationException("Expected parenthesized SQLite DDL.");
        var depth = 0;
        for (var i = open; i < tokens.Length; i++)
        {
            if (tokens[i] == "(") depth++;
            if (tokens[i] == ")" && --depth == 0) return i;
        }
        throw new InvalidOperationException("Unbalanced SQLite DDL.");
    }

    private static List<string[]> SplitDefinitions(string[] tokens)
    {
        var definitions = new List<string[]>();
        var start = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "(") i = ClosingParenthesis(tokens, i);
            else if (tokens[i] == ",")
            {
                definitions.Add(tokens[start..i]);
                start = i + 1;
            }
        }
        definitions.Add(tokens[start..]);
        return definitions;
    }
}
