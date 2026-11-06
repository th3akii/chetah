using System.Text;
using System.Text.Json;
using Chetah.CodeGen;

namespace Chetah.Db;

/// <summary>The identities the schema file declares, plus any this run had to allocate.</summary>
internal sealed record SchemaIds(
    IReadOnlyDictionary<string, long> Tables,
    IReadOnlyDictionary<(string Table, string Column), long> Columns,
    IReadOnlySet<long> Assigned)
{
    /// <summary>Highest id this file knows about, declared or allocated.</summary>
    public long Max => Tables.Values.Concat(Columns.Values).DefaultIfEmpty(0).Max();
}

/// <summary>Maintains the <c>"$id"</c> members in the schema file.
///
/// The id is the only thing that tells Chetah a column was renamed rather than removed while an
/// unrelated one appeared: a rename keeps the id, a replacement does not. Keeping it in the schema
/// file means it travels with the declaration through a rename, a review and a git history, without
/// a side channel that could drift from it.</summary>
internal static class SchemaFile
{
    /// <summary>Resolves an id for every table and column that lacks one and writes back only the
    /// missing members. The file is edited textually so comments and formatting survive; a full
    /// re-serialize would silently delete the documentation users keep inside the schema.</summary>
    /// <param name="registry">Recorded identities. An object with no <c>"$id"</c> in the file keeps
    /// the id the registry already holds for its name (adoption, so a schema written before ids
    /// existed does not look like a wholesale replacement); only a genuinely new object allocates.</param>
    public static async Task<SchemaIds> EnsureIdsAsync(
        string path,
        Schema schema,
        IdentityRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var tables = new Dictionary<string, long>(StringComparer.Ordinal);
        var columns = new Dictionary<(string Table, string Column), long>();
        var assigned = new HashSet<long>();

        var highest = Math.Max(DeclaredMax(schema), registry.MaxId);
        long Allocate()
        {
            var id = ++highest;
            assigned.Add(id);
            return id;
        }

        var knownTables = registry.Live("table").ToDictionary(row => row.Name, row => row.Id, StringComparer.Ordinal);
        var knownColumns = registry.Live("column")
            .Where(row => row.TableId is not null)
            .ToDictionary(row => (row.TableId!.Value, row.Name), row => row.Id);

        foreach (var table in schema.Tables)
            tables[table.Name] = table.Id
                ?? (knownTables.TryGetValue(table.Name, out var known) ? known : Allocate());

        foreach (var table in schema.Tables)
        {
            var tableId = tables[table.Name];
            foreach (var column in table.Columns)
            {
                var key = (tableId, column.Name);
                columns[(table.Name, column.Name)] = column.Id
                    ?? (knownColumns.TryGetValue(key, out var known) ? known : Allocate());
            }
        }

        var ids = new SchemaIds(tables, columns, assigned);
        // Adoption writes too: the file has to record the identity it adopted, or the next run
        // would have to adopt again and could not tell a rename from a replacement.
        var missing = schema.Tables.Sum(table =>
            (table.Id is null ? 1 : 0) + table.Columns.Count(column => column.Id is null));

        if (missing == 0)
            return ids;

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var bom = HasBom(bytes) ? bytes[..3] : [];
        var text = Encoding.UTF8.GetString(bytes, bom.Length, bytes.Length - bom.Length);

        var positions = JsoncPositions.Scan(text);
        var edits = new List<(int Offset, string Member)>();

        foreach (var table in schema.Tables)
        {
            if (table.Id is null)
            {
                var offset = At(positions.Tables, table.Name, path);
                edits.Add((offset, IdMember(text, offset, tables[table.Name])));
            }

            foreach (var column in table.Columns.Where(column => column.Id is null))
            {
                var offset = At(positions.Columns, (table.Name, column.Name), path);
                edits.Add((offset, IdMember(text, offset, columns[(table.Name, column.Name)])));
            }
        }

        var builder = new StringBuilder(text);
        foreach (var (offset, member) in edits.OrderByDescending(edit => edit.Offset))
            builder.Insert(offset, member);

        await File.WriteAllBytesAsync(path, [.. bom, .. Encoding.UTF8.GetBytes(builder.ToString())], cancellationToken);
        return ids;
    }

    private static long DeclaredMax(Schema schema) =>
        schema.Tables
            .SelectMany(table => table.Columns.Select(column => column.Id ?? 0).Append(table.Id ?? 0))
            .DefaultIfEmpty(0)
            .Max();

    private static bool HasBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static int At<TKey>(IReadOnlyDictionary<TKey, int> positions, TKey key, string path)
        where TKey : notnull =>
        positions.TryGetValue(key, out var offset)
            ? offset
            : throw new InvalidOperationException(
                $"Could not locate '{key}' in {path} to record its identity. "
                + "Only plain object members are supported; reformat the file and retry.");

    /// <summary>Inserts the member on its own line, matching the indentation of the object it joins,
    /// so added metadata reads like the rest of the file.</summary>
    private static string IdMember(string text, int offset, long id)
    {
        var start = offset;
        var i = start;
        var lineStart = -1;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            if (text[i] == '\n')
                lineStart = i;
            i++;
        }

        if (i < text.Length && text[i] == '}')
            return $"\"$id\": {id}";

        if (lineStart < 0)
            return $"\"$id\": {id},";

        return $"\n{text[(lineStart + 1)..i]}\"$id\": {id},";
    }

    /// <summary>Locates the offset just after each table and column object's opening brace. A whole
    /// JSON DOM parse cannot report positions, and System.Text.Json exposes none, so a small JSONC
    /// reader walks the text instead (comments are legal in these files).</summary>
    private sealed class JsoncPositions
    {
        private readonly string _text;
        private int _i;

        private JsoncPositions(string text) => _text = text;

        public Dictionary<string, int> Tables { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Table, string Column), int> Columns { get; } = new();

        public static JsoncPositions Scan(string text)
        {
            var positions = new JsoncPositions(text);
            positions.Value([]);
            return positions;
        }

        /// <summary>Walks a value at <paramref name="path"/>, recording object spans it cares about.</summary>
        private void Value(string[] path)
        {
            SkipTrivia();
            switch (Peek())
            {
                case '{':
                    var offset = ++_i;
                    if (path is ["tables", var table] && !table.StartsWith('#'))
                        Tables[table] = offset;
                    else if (path is ["tables", var parent, "columns", var column] && !column.StartsWith('#'))
                        Columns[(parent, column)] = offset;
                    Members(path);
                    return;
                case '[':
                    _i++;
                    SkipTrivia();
                    if (Peek() == ']')
                    {
                        _i++;
                        return;
                    }
                    while (true)
                    {
                        Value([.. path, "#"]);
                        SkipTrivia();
                        if (Peek() == ',')
                        {
                            _i++;
                            continue;
                        }
                        Expect(']');
                        return;
                    }
                case '"':
                    ReadString();
                    return;
                default:
                    SkipScalar();
                    return;
            }
        }

        private void Members(string[] path)
        {
            SkipTrivia();
            if (Peek() == '}')
            {
                _i++;
                return;
            }

            while (true)
            {
                SkipTrivia();
                var key = ReadString();
                Expect(':');
                Value([.. path, key]);
                SkipTrivia();
                if (Peek() == ',')
                {
                    _i++;
                    continue;
                }
                Expect('}');
                return;
            }
        }

        private void SkipTrivia()
        {
            while (_i < _text.Length)
            {
                var c = _text[_i];
                if (char.IsWhiteSpace(c))
                {
                    _i++;
                    continue;
                }

                if (c != '/')
                    return;

                if (_i + 1 < _text.Length && _text[_i + 1] == '/')
                {
                    while (_i < _text.Length && _text[_i] != '\n')
                        _i++;
                    continue;
                }

                if (_i + 1 < _text.Length && _text[_i + 1] == '*')
                {
                    var end = _text.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (end < 0)
                        throw Error("unterminated block comment");
                    _i = end + 2;
                    continue;
                }

                return;
            }
        }

        private void SkipScalar()
        {
            var start = _i;
            while (_i < _text.Length)
            {
                var c = _text[_i];
                if (char.IsWhiteSpace(c) || c is ',' or '}' or ']')
                    return;
                _i++;
            }

            if (_i == start)
                throw Error("expected a value");
        }

        private string ReadString()
        {
            Expect('"');

            var start = _i;
            var escaped = false;
            var builder = new StringBuilder();
            while (_i < _text.Length)
            {
                var c = _text[_i];
                if (c == '\\')
                {
                    escaped = true;
                    builder.Append(c).Append(_text[_i + 1]);
                    _i += 2;
                    continue;
                }

                if (c == '"')
                {
                    var raw = _text[start.._i];
                    _i++;
                    return escaped ? JsonSerializer.Deserialize<string>($"\"{builder}\"")! : raw;
                }

                if (escaped)
                    builder.Append(c);
                _i++;
            }

            throw Error("unterminated string");
        }

        private char Peek() => _i < _text.Length ? _text[_i] : '\0';

        private void Expect(char expected)
        {
            SkipTrivia();
            if (Peek() != expected)
                throw Error($"expected '{expected}'");
            _i++;
        }

        private InvalidOperationException Error(string reason) =>
            new($"Schema file is not readable as JSONC at offset {_i}: {reason}.");
    }
}
