using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace BH.Native
{
    // Windows supplies SQLite. Keep a single writer transaction and a bounded
    // page cache; graph queries seek to a node's incoming edges on disk.
    public sealed partial class GraphIndex : IDisposable
    {
        private const string Library = "winsqlite3.dll";
        private const int Row = 100;
        private const int Done = 101;
        private IntPtr db;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] path, out IntPtr handle, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg16(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr handle, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr handle, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_reset(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes16(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_blob(IntPtr statement, int index, byte[] bytes, int count, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }

        private void Check(int status)
        {
            if (status != 0 && status != Row && status != Done)
                throw new IOException("SQLite error " + status + ": " + Marshal.PtrToStringUni(sqlite3_errmsg16(db)));
        }

        public GraphIndex(string path, bool create)
            : this(path, create, false) { }

        public GraphIndex(string path, bool create, bool writable)
        {
            try
            {
                Check(sqlite3_open_v2(Utf8(path), out db, create ? 6 : writable ? 2 : 1, IntPtr.Zero));
                Execute("PRAGMA cache_size=-65536; PRAGMA temp_store=FILE;");
                if (create)
                {
                    Execute(@"CREATE TABLE nodes (id TEXT COLLATE NOCASE PRIMARY KEY, name TEXT, kind TEXT, disabled TEXT, record TEXT, security_enabled TEXT) WITHOUT ROWID;
                              CREATE TABLE dns (dn TEXT COLLATE NOCASE PRIMARY KEY, id TEXT) WITHOUT ROWID;
                              CREATE TABLE external (id TEXT COLLATE NOCASE PRIMARY KEY, name TEXT, kind TEXT) WITHOUT ROWID;
                              CREATE TABLE edges (target TEXT COLLATE NOCASE, bucket TEXT, source TEXT COLLATE NOCASE, type TEXT,
                                  target_dn INTEGER, source_dn INTEGER, evidence TEXT,
                                  PRIMARY KEY (target, bucket, source, type)) WITHOUT ROWID;");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (db == IntPtr.Zero) return;
            sqlite3_close(db);
            db = IntPtr.Zero;
        }

        private void Execute(string sql)
        {
            Check(sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        }

        private sealed class Statement : IDisposable
        {
            private readonly GraphIndex owner;
            internal IntPtr handle;

            public Statement(GraphIndex owner, string sql)
            {
                this.owner = owner;
                owner.Check(sqlite3_prepare_v2(owner.db, Utf8(sql), -1, out handle, IntPtr.Zero));
            }

            public void Bind(int index, string value)
            {
                owner.Check(sqlite3_bind_text16(handle, index, value, value == null ? 0 : value.Length * 2, new IntPtr(-1)));
            }

            public bool Read()
            {
                int status = sqlite3_step(handle);
                owner.Check(status);
                return status == Row;
            }

            public void Reset() { owner.Check(sqlite3_reset(handle)); }

            public void BindRecord(int index, string record)
            {
                if (record == null || record.Length <= 512) { Bind(index, record); return; }
                byte[] bytes = Encoding.UTF8.GetBytes(record);
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(bytes, 0, bytes.Length);
                    byte[] compressed = output.ToArray();
                    if (compressed.Length >= bytes.Length) { Bind(index, record); return; }
                    owner.Check(sqlite3_bind_blob(handle, index, compressed, compressed.Length, new IntPtr(-1)));
                }
            }

            public string Record(int column)
            {
                if (sqlite3_column_type(handle, column) != 4) return Text(column);
                int length = sqlite3_column_bytes(handle, column);
                var bytes = new byte[length];
                Marshal.Copy(sqlite3_column_blob(handle, column), bytes, 0, length);
                using (var compressed = new MemoryStream(bytes))
                using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
                using (var text = new StreamReader(gzip, Encoding.UTF8)) return text.ReadToEnd();
            }

            public string Text(int column)
            {
                IntPtr value = sqlite3_column_text16(handle, column);
                return value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value, sqlite3_column_bytes16(handle, column) / 2);
            }

            public void Dispose() { sqlite3_finalize(handle); }
        }

        public void Import(string[] paths, string[] kinds, string[] edgePaths, string[] buckets, string[] externalPaths, bool retainNodes)
        {
            if (paths.Length != kinds.Length || edgePaths.Length != buckets.Length)
                throw new ArgumentException("Every dataset file requires a type.");

            Execute("BEGIN IMMEDIATE");
            try
            {
                using (var node = new Statement(this, "INSERT OR IGNORE INTO nodes VALUES (?1,?2,?3,?4,?5,?6)"))
                using (var alias = new Statement(this, "INSERT OR IGNORE INTO dns VALUES (?1,?2)"))
                {
                    for (int i = 0; i < paths.Length; i++)
                    {
                        foreach (string line in NdjsonReader.Lines(paths[i]))
                        {
                            if (line.Length == 0) continue;
                            string id = Required(line, "Id", paths[i]);
                            string dn = JsonRecordReader.ExtractField(line, "Dn");
                            node.Bind(1, id);
                            node.Bind(2, JsonRecordReader.ExtractField(line, "Name"));
                            node.Bind(3, kinds[i]);
                            node.Bind(4, JsonRecordReader.ExtractField(line, "Disabled"));
                            node.BindRecord(5, retainNodes ? line : null);
                            node.Bind(6, kinds[i] == "groups" ? JsonRecordReader.ExtractField(line, "SecurityEnabled") : null);
                            node.Read();
                            node.Reset();
                            if (!string.IsNullOrEmpty(dn)) WriteAlias(alias, dn.ToUpperInvariant(), id);
                            string template = JsonRecordReader.ExtractField(line, "TemplateAlias");
                            if (template != null) WriteAlias(alias, "TEMPLATE:" + template.ToUpperInvariant(), id);
                            foreach (string spn in JsonRecordReader.StringArray(line, "Spns"))
                            {
                                WriteAlias(alias, "SPN:" + spn.ToUpperInvariant(), id);
                                // Host alias so a constrained-delegation target
                                // (cifs/host, http/host, ...) resolves to whichever
                                // account registered an SPN on that host.
                                string spnHost = DirectoryIdentity.SpnHost(spn);
                                if (spnHost != null) WriteAlias(alias, "SPNHOST:" + spnHost.ToUpperInvariant(), id);
                            }
                            // A computer's own FQDN is the host most delegate targets name.
                            string dns = JsonRecordReader.ExtractField(line, "Dns");
                            if (!string.IsNullOrEmpty(dns)) WriteAlias(alias, "SPNHOST:" + dns.ToUpperInvariant(), id);
                        }
                    }
                }
                using (var directEdge = new Statement(this, "INSERT OR IGNORE INTO edges VALUES (?1,?2,?3,?4,0,0,?7)"))
                using (var dnEdge = new Statement(this, @"INSERT OR IGNORE INTO edges
                    SELECT COALESCE(t.id,?1),?2,COALESCE(s.id,?3),?4,
                           ?5 IS NOT NULL AND t.id IS NULL, ?6 IS NOT NULL AND s.id IS NULL, ?7
                    FROM (SELECT 1) LEFT JOIN dns t ON t.dn=?5
                                    LEFT JOIN dns s ON s.dn=?6"))
                {
                    for (int i = 0; i < edgePaths.Length; i++)
                    {
                        foreach (string line in NdjsonReader.Lines(edgePaths[i]))
                        {
                            if (line.Length == 0) continue;
                            string target = Required(line, "Target", edgePaths[i]);
                            string source = Required(line, "Source", edgePaths[i]);
                            bool targetIsDn = JsonRecordReader.ExtractField(line, "TargetIsDn") == "true";
                            bool sourceIsDn = JsonRecordReader.ExtractField(line, "SourceIsDn") == "true";
                            var edge = targetIsDn || sourceIsDn ? dnEdge : directEdge;
                            edge.Bind(1, target);
                            edge.Bind(2, buckets[i]);
                            edge.Bind(3, source);
                            edge.Bind(4, Required(line, "EdgeType", edgePaths[i]));
                            edge.Bind(7, buckets[i] == "observed" ? line : null);
                            if (edge == dnEdge)
                            {
                                edge.Bind(5, targetIsDn ? target.ToUpperInvariant() : null);
                                edge.Bind(6, sourceIsDn ? source.ToUpperInvariant() : null);
                            }
                            edge.Read();
                            edge.Reset();
                        }
                    }
                }
                using (var external = new Statement(this, "INSERT OR IGNORE INTO external VALUES (?1,?2,?3)"))
                {
                    foreach (string path in externalPaths)
                    {
                        foreach (string line in NdjsonReader.Lines(path))
                        {
                            if (line.Length == 0) continue;
                            external.Bind(1, Required(line, "Id", path));
                            external.Bind(2, JsonRecordReader.ExtractField(line, "Name"));
                            external.Bind(3, JsonRecordReader.ExtractField(line, "Type"));
                            external.Read();
                            external.Reset();
                        }
                    }
                }
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        private static string Required(string line, string field, string path)
        {
            string value = JsonRecordReader.ExtractField(line, field);
            if (string.IsNullOrEmpty(value)) throw new InvalidDataException("Missing " + field + " in " + path);
            return value;
        }

        private static void WriteAlias(Statement statement, string name, string id)
        {
            statement.Bind(1, name);
            statement.Bind(2, id);
            statement.Read();
            statement.Reset();
        }

        private List<string[]> Query(string sql, int columns)
        {
            var result = new List<string[]>();
            using (var statement = new Statement(this, sql))
            {
                while (statement.Read())
                {
                    var row = new string[columns];
                    for (int i = 0; i < columns; i++) row[i] = statement.Text(i);
                    result.Add(row);
                }
            }
            return result;
        }

        public List<string[]> NodeCounts() { return Query("SELECT kind,count(*) FROM nodes GROUP BY kind", 2); }
        public List<string[]> EdgeCounts() { return Query("SELECT bucket,count(*) FROM edges GROUP BY bucket", 2); }
        public List<string[]> UnresolvedCounts() { return Query("SELECT bucket,count(*) FROM edges WHERE target_dn OR source_dn GROUP BY bucket", 2); }
        public List<string[]> UserCounts() { return Query("SELECT disabled,count(*) FROM nodes WHERE kind='users' GROUP BY disabled", 2); }

        // A bounded evidence sample, including relationships outside the graph preview.
        // Each bucket stops at its limit without sorting the full edge table.
        public List<string[]> ReviewRelationships()
        {
            return ReviewRelationships(new[] { "acl", "access", "observed" });
        }

        public List<string[]> ReviewRelationships(string[] buckets)
        {
            var result = new List<string[]>();
            foreach (string bucket in buckets)
            {
                if (bucket != "acl" && bucket != "access" && bucket != "observed")
                    throw new ArgumentException("Invalid review relationship bucket.");
                result.AddRange(Query(@"SELECT e.source,e.target,e.type,s.name,t.name,t.kind
                    FROM edges e LEFT JOIN nodes s ON s.id=e.source LEFT JOIN nodes t ON t.id=e.target
                    WHERE e.bucket='" + bucket + "' LIMIT 40", 6));
            }
            return result;
        }

        public long WriteMerged(string directory, string[] nodeKinds, string[] edgeBuckets)
        {
            return WriteMerged(directory, nodeKinds, edgeBuckets, false);
        }

        public long WriteMerged(string directory, string[] nodeKinds, string[] edgeBuckets, bool compress)
        {
            string suffix = compress ? ".ndjson.gz" : ".ndjson";
            using (var nodes = new Statement(this, "SELECT record FROM nodes WHERE kind=?1"))
            {
                foreach (string kind in nodeKinds)
                {
                    using (var writer = new NdjsonWriter(Path.Combine(directory, "nodes_" + kind + suffix), false))
                    {
                        nodes.Bind(1, kind);
                        while (nodes.Read()) writer.WriteLine(nodes.Record(0));
                        nodes.Reset();
                    }
                }
            }
            var json = new JsonRecordWriter();
            using (var edges = new Statement(this, "SELECT source,target,type,source_dn,target_dn,evidence FROM edges WHERE bucket=?1"))
            {
                foreach (string bucket in edgeBuckets)
                {
                    using (var writer = new NdjsonWriter(Path.Combine(directory, "edges_" + bucket + suffix), false))
                    {
                        edges.Bind(1, bucket);
                        while (edges.Read())
                        {
                            string evidence = edges.Text(5);
                            if (evidence != null)
                            {
                                writer.WriteLine(JsonRecordReader.NormalizeEdge(evidence, edges.Text(0), edges.Text(1),
                                    edges.Text(3) == "1", edges.Text(4) == "1"));
                                continue;
                            }
                            json.Begin().Prop("Source", edges.Text(0)).Prop("Target", edges.Text(1)).Prop("EdgeType", edges.Text(2));
                            if (edges.Text(3) == "1") json.Prop("SourceIsDn", true);
                            if (edges.Text(4) == "1") json.Prop("TargetIsDn", true);
                            writer.WriteLine(json.End());
                        }
                        edges.Reset();
                    }
                }
            }
            long count = 0;
            using (var external = new Statement(this, "SELECT id,name,kind FROM external"))
            using (var writer = new NdjsonWriter(Path.Combine(directory, "principals_external" + suffix), false))
            {
                while (external.Read())
                {
                    writer.WriteLine(json.Begin().Prop("Id", external.Text(0)).Prop("Name", external.Text(1)).Prop("Type", external.Text(2)).End());
                    count++;
                }
            }
            return count;
        }
        public List<string[]> TopAclHolders()
        {
            return Query(@"SELECT a.source,n.name,n.kind,a.rights FROM
                           (SELECT source,count(*) rights FROM edges WHERE bucket='acl' GROUP BY source
                            ORDER BY rights DESC,source LIMIT 50) a LEFT JOIN nodes n ON n.id=a.source
                           ORDER BY a.rights DESC,a.source", 4);
        }
        public List<string[]> Groups()
        {
            return Query(@"SELECT id,name FROM nodes WHERE kind='groups' AND security_enabled IS NOT 'false' AND
                           ((id GLOB 'S-1-5-21-*' AND
                             (id LIKE '%-512' OR id LIKE '%-516' OR id LIKE '%-518' OR id LIKE '%-519' OR
                              id LIKE '%-521' OR id LIKE '%-526' OR id LIKE '%-527')) OR
                            id LIKE '%S-1-5-32-544' OR id LIKE '%S-1-5-32-548' OR id LIKE '%S-1-5-32-549' OR
                            id LIKE '%S-1-5-32-550' OR id LIKE '%S-1-5-32-551')", 2);
        }
        public List<string[]> SearchSample(int limit)
        {
            return Query("SELECT id,name,kind FROM nodes WHERE name IS NOT NULL LIMIT " + limit, 3);
        }

        public string[] Node(string id)
        {
            using (var statement = new Statement(this, "SELECT name,kind FROM nodes WHERE id=?1 UNION ALL SELECT name,kind FROM external WHERE id=?1 LIMIT 1"))
            {
                statement.Bind(1, id);
                return statement.Read() ? new[] { statement.Text(0), statement.Text(1) } : null;
            }
        }

        public string NodeRecord(string id)
        {
            using (var statement = new Statement(this, "SELECT record FROM nodes WHERE id=?1"))
            {
                statement.Bind(1, id);
                return statement.Read() ? statement.Record(0) : null;
            }
        }

        public List<string[]> Search(string name)
        {
            var result = new List<string[]>();
            using (var statement = new Statement(this, "SELECT id,name,kind FROM nodes WHERE instr(lower(name),lower(?1))>0 LIMIT 100"))
            {
                statement.Bind(1, name);
                while (statement.Read()) result.Add(new[] { statement.Text(0), statement.Text(1), statement.Text(2) });
            }
            return result;
        }

        public EgoGraphResult Neighborhood(string[] seeds, int depthCap, int nodeCap, int edgeCap)
        {
            var result = new EgoGraphResult();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<KeyValuePair<string, int>>();
            foreach (string seed in seeds)
            {
                if (visited.Count == nodeCap) { result.Truncated = true; break; }
                if (visited.Add(seed)) pending.Enqueue(new KeyValuePair<string, int>(seed, 0));
            }
            using (var incoming = Incoming())
            {
                while (pending.Count > 0 && !result.Truncated)
                {
                    var current = pending.Dequeue();
                    if (current.Value >= depthCap) continue;
                    incoming.Bind(1, current.Key);
                    while (incoming.Read())
                    {
                        var edge = ReadEdge(incoming, current.Key);
                        if (result.Edges.Count == edgeCap || (visited.Count == nodeCap && !visited.Contains(edge.Source)))
                        {
                            result.Truncated = true;
                            break;
                        }
                        result.Edges.Add(edge);
                        if (visited.Add(edge.Source)) pending.Enqueue(new KeyValuePair<string, int>(edge.Source, current.Value + 1));
                    }
                    incoming.Reset();
                }
            }
            result.NodeIds.AddRange(visited);
            return result;
        }

        public EgoGraphResult ShortestPath(string source, string target, int maxVisited)
        {
            return ShortestPath(source, target, maxVisited, null);
        }

        public EgoGraphResult ShortestPath(string source, string target, int maxVisited, string[] allowedTypes)
        {
            var result = new EgoGraphResult();
            var allowed = allowedTypes == null ? null : new HashSet<string>(allowedTypes, StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, GraphEdge>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>();
            next.Add(target, new GraphEdge());
            pending.Enqueue(target);
            using (var incoming = Incoming())
            {
                while (pending.Count > 0 && !next.ContainsKey(source))
                {
                    string current = pending.Dequeue();
                    incoming.Bind(1, current);
                    while (incoming.Read())
                    {
                        var edge = ReadEdge(incoming, current);
                        if (allowed != null && !allowed.Contains(edge.EdgeType)) continue;
                        if (next.ContainsKey(edge.Source)) continue;
                        if (next.Count == maxVisited)
                        {
                            result.Truncated = true;
                            return result;
                        }
                        next.Add(edge.Source, edge);
                        pending.Enqueue(edge.Source);
                        if (StringComparer.OrdinalIgnoreCase.Equals(edge.Source, source)) break;
                    }
                    incoming.Reset();
                }
            }
            if (!next.ContainsKey(source)) return result;
            string id = source;
            result.NodeIds.Add(id);
            while (!StringComparer.OrdinalIgnoreCase.Equals(id, target))
            {
                GraphEdge edge = next[id];
                result.Edges.Add(edge);
                id = edge.Target;
                result.NodeIds.Add(id);
            }
            return result;
        }

        private Statement Incoming()
        {
            return new Statement(this, @"SELECT source,type FROM edges
                WHERE target=?1 AND bucket IN ('acl','memberof','access') AND source<>target AND source_dn=0 AND target_dn=0
                AND (bucket<>'memberof' OR NOT EXISTS (
                    SELECT 1 FROM nodes WHERE id=?1 AND security_enabled='false'))");
        }

        private static GraphEdge ReadEdge(Statement row, string target)
        {
            return new GraphEdge { Source = row.Text(0), Target = target, EdgeType = row.Text(1) };
        }
    }
}
