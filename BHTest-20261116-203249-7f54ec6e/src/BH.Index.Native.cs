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
            // SQLite reports a missing file as "unable to open database file" (error 14),
            // which hides the real cause. A relative path is resolved from the process
            // working directory, which PowerShell's Set-Location does not change.
            if (!create && !File.Exists(path))
                throw new FileNotFoundException("Graph index not found: " + Path.GetFullPath(path) +
                    ". Relative paths resolve from the process working directory, not PowerShell's current location; pass the full path to graph.sqlite.", path);
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

        // Optional progress hook: (what is happening, units done, units total, records so far).
        // Import reports bytes of input files finished; WriteMerged reports steps finished.
        // It runs on the calling thread, so a PowerShell scriptblock delegate is safe here.
        public Action<string, long, long, long> Progress;

        private void Report(string label, long done, long total, long records)
        {
            Action<string, long, long, long> hook = Progress;
            if (hook != null) hook(label, done, total, records);
        }

        // "scan-folder/file" so identical file names from different scans stay distinguishable.
        private static string ShortName(string path)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            return string.IsNullOrEmpty(folder) ? Path.GetFileName(path) : folder + "/" + Path.GetFileName(path);
        }

        private static long SizeOf(string[] files)
        {
            long total = 0;
            foreach (string file in files) total += new FileInfo(file).Length;
            return total;
        }

        private static void RegisterSpnHost(Statement hosts,string host,string id){
            if(string.IsNullOrEmpty(host))return;hosts.Bind(1,host.ToUpperInvariant());hosts.Bind(2,id);hosts.Read();hosts.Reset();
        }
        public void Import(string[] paths, string[] kinds, string[] edgePaths, string[] buckets, string[] externalPaths, bool retainNodes)
        {
            if (paths.Length != kinds.Length || edgePaths.Length != buckets.Length)
                throw new ArgumentException("Every dataset file requires a type.");

            long totalBytes = SizeOf(paths) + SizeOf(edgePaths) + SizeOf(externalPaths);
            long doneBytes = 0, records = 0;
            Execute("CREATE TEMP TABLE IF NOT EXISTS bh_spn_hosts(host TEXT COLLATE NOCASE PRIMARY KEY,id TEXT) WITHOUT ROWID;DELETE FROM bh_spn_hosts;");
            InitializeGpoInputs();
            InitializeOwnershipInputs();
            InitializeAdcsInputs();
            int fileCount = paths.Length + edgePaths.Length + externalPaths.Length, fileNumber = 0;
            string label;

            Execute("BEGIN IMMEDIATE");
            try
            {
                Execute("DELETE FROM edges WHERE type COLLATE NOCASE='GPOAppliesTo'");
                using (var spnHosts=new Statement(this,"INSERT OR REPLACE INTO bh_spn_hosts SELECT ?1,CASE WHEN EXISTS(SELECT 1 FROM bh_spn_hosts WHERE host=?1 AND (id IS NULL OR id<>?2 COLLATE NOCASE)) THEN NULL ELSE ?2 END"))
                using (var node = new Statement(this, "INSERT OR IGNORE INTO nodes VALUES (?1,?2,?3,?4,?5,?6)"))
                using (var alias = new Statement(this, "INSERT OR IGNORE INTO dns VALUES (?1,?2)"))
                using (var gpoNode=new Statement(this,"INSERT OR IGNORE INTO gpo_meta VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)"))
                using (var gpoConflict=new Statement(this,"UPDATE gpo_meta SET conflict=1 WHERE id=?1 AND signature<>?2"))
                using (var gpoGrant=new Statement(this,"INSERT OR IGNORE INTO gpo_grants VALUES(?1,?2,?3)"))
                using(var ownerNode=new Statement(this,"INSERT OR IGNORE INTO owner_meta VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)"))
                using(var ownerConflict=new Statement(this,"UPDATE owner_meta SET conflict=1 WHERE id=?1 AND signature<>?2"))
                using(var adcsNode=new Statement(this,"INSERT OR IGNORE INTO adcs_meta VALUES(?1,?2,?3,?4,?5)"))
                using(var adcsConflict=new Statement(this,"UPDATE adcs_meta SET conflict=1 WHERE id=?1 AND signature<>?2"))
                {
                    for (int i = 0; i < paths.Length; i++)
                    {
                        fileNumber++;
                        label = "Loading objects from " + ShortName(paths[i]) + " (file " + fileNumber + " of " + fileCount + ")";
                        Report(label, doneBytes, totalBytes, records);
                        foreach (string line in NdjsonReader.Lines(paths[i]))
                        {
                            if (line.Length == 0) continue;
                            if ((++records & 0xFFFF) == 0) Report(label, doneBytes, totalBytes, records);
                            string id = Required(line, "Id", paths[i]);
                            string dn = JsonRecordReader.ExtractField(line, "Dn");
                            RegisterGpoNode(line,id,kinds[i],gpoNode,gpoConflict,gpoGrant);
                            RegisterOwnershipNode(line,id,kinds[i],ownerNode,ownerConflict);
                            RegisterAdcsNode(line,id,kinds[i],adcsNode,adcsConflict);
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
                            string policyOid = JsonRecordReader.ExtractField(line, "PolicyOid");
                            if (!string.IsNullOrEmpty(policyOid)) WriteAlias(alias, "OID:" + policyOid.ToUpperInvariant(), id);
                            foreach (string spn in JsonRecordReader.StringArray(line, "Spns")) { WriteAlias(alias,"SPN:"+spn.ToUpperInvariant(),id);string host=DirectoryIdentity.SpnHost(spn);RegisterSpnHost(spnHosts,host,id); }
                            string dns=JsonRecordReader.ExtractField(line,"Dns");RegisterSpnHost(spnHosts,dns,id);
                        }
                        doneBytes+=new FileInfo(paths[i]).Length;Report(label,doneBytes,totalBytes,records);
                    }
                    Execute("INSERT OR IGNORE INTO dns SELECT 'SPNHOST:'||host,id FROM bh_spn_hosts WHERE id IS NOT NULL");
                }
                using(var gpoLink=new Statement(this,@"INSERT OR IGNORE INTO gpo_links SELECT COALESCE(s.id,?1),COALESCE(t.id,?2),?5,0,?6,?7
                    FROM (SELECT 1) LEFT JOIN dns s ON s.dn=?3 LEFT JOIN dns t ON t.dn=?4"))
                using(var gpoLinkConflict=new Statement(this,@"UPDATE gpo_links SET conflict=1
                    WHERE gpo=COALESCE((SELECT id FROM dns WHERE dn=?3),?1) AND root=COALESCE((SELECT id FROM dns WHERE dn=?4),?2) AND signature<>?5"))
                using (var directEdge = new Statement(this, "INSERT OR IGNORE INTO edges VALUES (?1,?2,?3,?4,0,0,?7)"))
                using (var dnEdge = new Statement(this, @"INSERT OR IGNORE INTO edges
                    SELECT COALESCE(t.id,?1),?2,COALESCE(s.id,?3),?4,
                           ?5 IS NOT NULL AND t.id IS NULL, ?6 IS NOT NULL AND s.id IS NULL, ?7
                    FROM (SELECT 1) LEFT JOIN dns t ON t.dn=?5
                                    LEFT JOIN dns s ON s.dn=?6"))
                {
                    for (int i = 0; i < edgePaths.Length; i++)
                    {
                        fileNumber++;
                        label = "Loading relationships from " + ShortName(edgePaths[i]) + " (file " + fileNumber + " of " + fileCount + ")";
                        Report(label, doneBytes, totalBytes, records);
                        foreach (string line in NdjsonReader.Lines(edgePaths[i]))
                        {
                            if (line.Length == 0) continue;
                            if ((++records & 0xFFFF) == 0) Report(label, doneBytes, totalBytes, records);
                            string target = Required(line, "Target", edgePaths[i]);
                            string source = Required(line, "Source", edgePaths[i]);
                            bool targetIsDn = JsonRecordReader.ExtractField(line, "TargetIsDn") == "true";
                            bool sourceIsDn = JsonRecordReader.ExtractField(line, "SourceIsDn") == "true";
                            string derivation=JsonRecordReader.ExtractField(line,"Derivation");
                            string edgeType=GraphRelationships.CanonicalType(Required(line,"EdgeType",edgePaths[i]));
                            // Recompute derived capabilities from this input set, never
                            // carry an earlier membership intersection through a merge.
                            if(string.Equals(edgeType,"ADCSESC1",StringComparison.OrdinalIgnoreCase)||derivation=="DCSyncMembershipIntersectionV1"||derivation=="OwnershipControlV1"||string.Equals(edgeType,"GPOAppliesTo",StringComparison.OrdinalIgnoreCase))continue;
                            if((buckets[i]=="gplink"||buckets[i]=="observed")&&edgeType=="GPLink")RegisterGpoLink(line,source,target,sourceIsDn,targetIsDn,gpoLink,gpoLinkConflict);
                            var edge = targetIsDn || sourceIsDn ? dnEdge : directEdge;
                            edge.Bind(1, target);
                            edge.Bind(2, buckets[i]);
                            edge.Bind(3, source);
                            edge.Bind(4, edgeType);
                            edge.Bind(7, buckets[i] == "observed" || buckets[i]=="gplink" || derivation!=null ? line : null);
                            if (edge == dnEdge)
                            {
                                edge.Bind(5, targetIsDn ? target.ToUpperInvariant() : null);
                                edge.Bind(6, sourceIsDn ? source.ToUpperInvariant() : null);
                            }
                            edge.Read();
                            edge.Reset();
                        }
                        doneBytes += new FileInfo(edgePaths[i]).Length;
                        Report(label, doneBytes, totalBytes, records);
                    }
                }
                using (var external = new Statement(this, "INSERT OR IGNORE INTO external VALUES (?1,?2,?3)"))
                {
                    foreach (string path in externalPaths)
                    {
                        fileNumber++;
                        label = "Loading external names from " + ShortName(path) + " (file " + fileNumber + " of " + fileCount + ")";
                        Report(label, doneBytes, totalBytes, records);
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
                Report("Deriving replication capabilities",doneBytes,totalBytes,records);
                DeriveReplicationCapabilities();
                Report("Deriving conservative computer GPO scope",doneBytes,totalBytes,records);
                DeriveGpoScope();
                PersistGpoConflicts();
                StoreGpoScopeStatus();
                Report("Evaluating ownership context",doneBytes,totalBytes,records);
                DeriveOwnershipControl();
                Report("Evaluating imported AD CS prerequisites",doneBytes,totalBytes,records);
                DeriveAdcsEsc1();
                Report(OwnershipScopeStatus,doneBytes,totalBytes,records);
                Report(GpoScopeStatus,doneBytes,totalBytes,records);
                Report("Saving the loaded data to disk (no further updates until commit finishes)",totalBytes,totalBytes,records);
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        private void DeriveReplicationCapabilities()
        {
            Execute(@"DELETE FROM edges WHERE type='DCSync' AND evidence LIKE '%""Derivation"":""DCSyncMembershipIntersectionV1""%';
                DROP TABLE IF EXISTS temp.replication_reach;
                DROP TABLE IF EXISTS temp.replication_candidates;
                DROP TABLE IF EXISTS temp.replication_members;
                CREATE TEMP TABLE replication_reach(
                    domain TEXT COLLATE NOCASE,permission INTEGER,principal TEXT COLLATE NOCASE,
                    next_id TEXT,grant_source TEXT,depth INTEGER,
                    PRIMARY KEY(domain,permission,principal)) WITHOUT ROWID;
                CREATE INDEX replication_depth ON replication_reach(depth);
                INSERT OR IGNORE INTO replication_reach
                SELECT e.target,CASE WHEN e.type COLLATE NOCASE='GetChanges' THEN 1 ELSE 2 END,
                    e.source,NULL,e.source,0
                FROM edges e JOIN nodes d ON d.id=e.target AND d.kind='domains'
                WHERE e.bucket='observed' AND e.type COLLATE NOCASE IN ('GetChanges','GetChangesAll')
                    AND e.source_dn=0 AND e.target_dn=0
                    AND NOT EXISTS(SELECT 1 FROM edges b WHERE b.target=e.target AND b.bucket='observed'
                        AND b.type COLLATE NOCASE='ReplicationAnalysisBlocked')
                ORDER BY e.target,e.type,e.source;");
            if(Scalar("SELECT count(*) FROM replication_reach")==0)
            {Execute("DROP TABLE replication_reach;");return;}
            // Both directions are indexed on a temporary membership-only table.
            // In particular, boundary selection must not scan all graph edges
            // once per candidate when a domain has many principals.
            Execute(@"CREATE TEMP TABLE replication_members(source TEXT COLLATE NOCASE,target TEXT COLLATE NOCASE,
                    PRIMARY KEY(source,target)) WITHOUT ROWID;
                CREATE INDEX replication_member_target ON replication_members(target,source);
                INSERT OR IGNORE INTO replication_members SELECT m.source,m.target FROM edges m
                    JOIN nodes g ON g.id=m.target AND g.kind='groups' AND g.security_enabled='true'
                    JOIN nodes p ON p.id=m.source AND p.kind IN ('users','computers','groups')
                    WHERE m.bucket='memberof' AND m.type COLLATE NOCASE='MemberOf'
                        AND m.source<>m.target AND m.source_dn=0 AND m.target_dn=0;");
            // A disk-backed breadth-first membership closure for each permission.
            // The unique key terminates cycles and keeps one shortest witness.
            for(long depth=0;Scalar("SELECT count(*) FROM replication_reach WHERE depth="+depth)>0;depth++)
                Execute(@"INSERT OR IGNORE INTO replication_reach
                    SELECT r.domain,r.permission,m.source,r.principal,r.grant_source,r.depth+1
                    FROM replication_reach r JOIN replication_members m ON m.target=r.principal
                    WHERE r.depth="+depth+@"
                    ORDER BY r.domain,r.permission,r.principal,m.source;");
            Execute(@"CREATE TEMP TABLE replication_candidates AS
                SELECT a.domain,a.principal,a.grant_source AS changes,b.grant_source AS changes_all
                FROM replication_reach a JOIN replication_reach b
                    ON b.domain=a.domain AND b.principal=a.principal AND b.permission=2
                JOIN nodes p ON p.id=a.principal AND p.kind IN ('users','computers','groups')
                    AND (p.kind<>'groups' OR p.security_enabled='true')
                WHERE a.permission=1
                    AND NOT EXISTS(SELECT 1 FROM edges e WHERE e.source=a.principal AND e.target=a.domain
                        AND e.type='DCSync' AND e.bucket IN ('acl','access') AND e.source_dn=0 AND e.target_dn=0)
                    AND NOT EXISTS(SELECT 1 FROM replication_members m
                        JOIN replication_reach x ON x.principal=m.target AND x.domain=a.domain AND x.permission=1
                        JOIN replication_reach y ON y.principal=m.target AND y.domain=a.domain AND y.permission=2
                        WHERE m.source=a.principal AND x.depth+y.depth<a.depth+b.depth);");
            using(var candidates=new Statement(this,"SELECT domain,principal,changes,changes_all FROM replication_candidates ORDER BY domain,principal"))
            using(var next=new Statement(this,"SELECT next_id FROM replication_reach WHERE domain=?1 AND permission=?2 AND principal=?3"))
            using(var insert=new Statement(this,"INSERT OR IGNORE INTO edges VALUES(?1,'acl',?2,'DCSync',0,0,?3)"))
            {
                var json=new JsonRecordWriter();
                while(candidates.Read())
                {
                    string domain=candidates.Text(0),principal=candidates.Text(1);bool limitedA,limitedB;
                    var changes=ReplicationWitness(next,domain,"1",principal,out limitedA);
                    var all=ReplicationWitness(next,domain,"2",principal,out limitedB);
                    string evidence=json.Begin().Prop("Source",principal).Prop("Target",domain).Prop("EdgeType","DCSync")
                        .Prop("Derivation","DCSyncMembershipIntersectionV1").Prop("EffectiveAccessVerified",false)
                        .Prop("GetChangesTrustee",candidates.Text(2)).Prop("GetChangesAllTrustee",candidates.Text(3))
                        .PropArray("GetChangesMembership",changes).PropArray("GetChangesAllMembership",all)
                        .Prop("WitnessLimited",limitedA || limitedB)
                        .Prop("EvidenceNote","Both grants reach this principal through recorded security-group memberships on this domain. Full-token effective access and collection coverage require validation.").End();
                    insert.Bind(1,domain);insert.Bind(2,principal);insert.Bind(3,evidence);insert.Read();insert.Reset();
                }
            }
            Execute("DROP TABLE replication_candidates; DROP TABLE replication_reach; DROP TABLE replication_members;");
        }

        private static List<string> ReplicationWitness(Statement next,string domain,string permission,string principal,out bool limited)
        {
            var path=new List<string>();limited=false;
            while(principal!=null)
            {
                if(path.Count==64){limited=true;break;}
                path.Add(principal);next.Bind(1,domain);next.Bind(2,permission);next.Bind(3,principal);
                if(!next.Read()){next.Reset();throw new InvalidDataException("Replication membership witness is missing.");}
                principal=next.Text(0);next.Reset();
            }
            return path;
        }

        public string EdgeEvidence(string source,string target,string type)
        {
            using(var s=new Statement(this,"SELECT evidence FROM edges WHERE target=?1 AND source=?2 AND type COLLATE NOCASE=?3 AND evidence IS NOT NULL LIMIT 1"))
            {s.Bind(1,target);s.Bind(2,source);s.Bind(3,type);return s.Read()?s.Text(0):null;}
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
                result.AddRange(Query(@"SELECT e.source,e.target,e.type,s.name,t.name,t.kind,e.evidence
                    FROM edges e LEFT JOIN nodes s ON s.id=e.source LEFT JOIN nodes t ON t.id=e.target
                    WHERE e.bucket='" + bucket + "' LIMIT 40", 7));
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
            int totalSteps = nodeKinds.Length + edgeBuckets.Length + 1, step = 0;
            string label;
            long rows;
            bool gpoMetadata=Scalar("SELECT count(*) FROM sqlite_temp_master WHERE type='table' AND name='gpo_meta'")>0;
            using (var nodes = new Statement(this,gpoMetadata?"SELECT n.record,COALESCE(g.conflict,0) FROM nodes n LEFT JOIN gpo_meta g ON g.id=n.id WHERE n.kind=?1":"SELECT record,0 FROM nodes WHERE kind=?1"))
            {
                foreach (string kind in nodeKinds)
                {
                    // Each type is a full pass over the working index, so announce it first.
                    label = "Writing objects: " + kind + " (step " + (step + 1) + " of " + totalSteps + ")";
                    Report(label, step, totalSteps, 0);
                    rows = 0;
                    using (var writer = new NdjsonWriter(Path.Combine(directory, "nodes_" + kind + suffix), false))
                    {
                        nodes.Bind(1, kind);
                        while (nodes.Read())
                        {
                            string record=nodes.Record(0);writer.WriteLine(nodes.Text(1)=="1"?GpoConflictRecord(record):record);
                            if ((++rows & 0x3FFF) == 0) Report(label, step, totalSteps, rows);
                        }
                        nodes.Reset();
                    }
                    step++;
                }
            }
            var json = new JsonRecordWriter();
            using (var edges = new Statement(this,gpoMetadata?"SELECT e.source,e.target,e.type,e.source_dn,e.target_dn,e.evidence,COALESCE(l.conflict,0) FROM edges e LEFT JOIN gpo_links l ON e.type='GPLink' AND l.gpo=e.source AND l.root=e.target WHERE e.bucket=?1":"SELECT source,target,type,source_dn,target_dn,evidence,0 FROM edges WHERE bucket=?1"))
            {
                foreach (string bucket in edgeBuckets)
                {
                    label = "Writing relationships: " + bucket + " (step " + (step + 1) + " of " + totalSteps + ")";
                    Report(label, step, totalSteps, 0);
                    rows = 0;
                    using (var writer = new NdjsonWriter(Path.Combine(directory, "edges_" + bucket + suffix), false))
                    {
                        edges.Bind(1, bucket);
                        while (edges.Read())
                        {
                            if ((++rows & 0x3FFF) == 0) Report(label, step, totalSteps, rows);
                            string evidence = edges.Text(5);
                            if(edges.Text(6)=="1"&&evidence!=null)evidence=GpoConflictRecord(evidence,"GpoLinkEvidenceConflict");
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
                    step++;
                }
            }
            long count = 0;
            Report("Writing external names (step " + (step + 1) + " of " + totalSteps + ")", step, totalSteps, 0);
            using (var external = new Statement(this, "SELECT id,name,kind FROM external"))
            using (var writer = new NdjsonWriter(Path.Combine(directory, "principals_external" + suffix), false))
            {
                while (external.Read())
                {
                    writer.WriteLine(json.Begin().Prop("Id", external.Text(0)).Prop("Name", external.Text(1)).Prop("Type", external.Text(2)).End());
                    count++;
                }
            }
            Report("Finished writing the merged files", totalSteps, totalSteps, 0);
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
            if(depthCap<0 || nodeCap<1 || edgeCap<1)throw new ArgumentOutOfRangeException();
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
            if(string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))throw new ArgumentException("Select a source and target identity.");
            if(maxVisited<1)throw new ArgumentOutOfRangeException("maxVisited");
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
                        if (next.Count >= maxVisited)
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

        public sealed class ShortestRoutesResult
        {
            public bool Found, Limited;
            public string Status = "NoPath";
            public int Visited, HopCount;
            public List<GraphEdge> Edges = new List<GraphEdge>();
            public List<GraphEdge> Representative = new List<GraphEdge>();
            public Dictionary<string,int> Depths = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        }

        // Like BloodHound's shortest-path endpoint, return the union of ALL equally
        // short directed routes. Keep a DAG, not exponentially many path permutations.
        // Search and returned-graph budgets are independent and never imply completeness.
        public ShortestRoutesResult AllShortestPaths(string source,string target,int maxVisited,int maxEdges,string[] allowedTypes)
        { return AllShortestPaths(source,target,maxVisited,maxEdges,allowedTypes,null); }
        private ShortestRoutesResult AllShortestPaths(string source,string target,int maxVisited,int maxEdges,string[] allowedTypes,Action check)
        {
            if(string.IsNullOrWhiteSpace(source)||string.IsNullOrWhiteSpace(target))throw new ArgumentException("Select a source and target identity.");
            if(maxVisited<1||maxVisited>250000||maxEdges<1||maxEdges>10000)throw new ArgumentOutOfRangeException();
            var result=new ShortestRoutesResult();
            var allowed=allowedTypes==null?null:new HashSet<string>(allowedTypes,StringComparer.OrdinalIgnoreCase);
            var distances=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            var successors=new Dictionary<string,List<GraphEdge>>(StringComparer.OrdinalIgnoreCase);
            var queue=new Queue<string>();distances.Add(target,0);queue.Enqueue(target);
            int foundDepth=string.Equals(source,target,StringComparison.OrdinalIgnoreCase)?0:int.MaxValue;
            int searchEdges=0;bool stopped=false;
            using(var incoming=Incoming())while(queue.Count>0&&!stopped)
            {
                if(check!=null)check();string current=queue.Dequeue();int depth=distances[current];
                if(depth>=foundDepth)break;
                incoming.Bind(1,current);
                while(incoming.Read())
                {
                    if(check!=null)check();var edge=ReadEdge(incoming,current);
                    if(allowed!=null&&!allowed.Contains(edge.EdgeType))continue;
                    // Once the source layer is known, other new nodes in that layer
                    // cannot contribute to a shortest route from this source.
                    if(depth+1==foundDepth&&!string.Equals(edge.Source,source,StringComparison.OrdinalIgnoreCase))continue;
                    int previous;
                    if(!distances.TryGetValue(edge.Source,out previous))
                    {
                        if(distances.Count>=maxVisited){result.Status="VisitLimit";stopped=true;break;}
                        distances.Add(edge.Source,depth+1);queue.Enqueue(edge.Source);previous=depth+1;
                    }
                    if(previous!=depth+1)continue;
                    if(searchEdges>=1000000){result.Status="SearchEdgeLimit";stopped=true;break;}
                    List<GraphEdge> outgoing;
                    if(!successors.TryGetValue(edge.Source,out outgoing)){outgoing=new List<GraphEdge>();successors.Add(edge.Source,outgoing);}
                    outgoing.Add(edge);searchEdges++;
                    if(string.Equals(edge.Source,source,StringComparison.OrdinalIgnoreCase))foundDepth=depth+1;
                }
                incoming.Reset();
            }
            result.Visited=distances.Count;result.Found=foundDepth!=int.MaxValue;result.Limited=stopped;
            if(!result.Found)return result;
            result.HopCount=foundDepth;
            string at=source;
            while(!string.Equals(at,target,StringComparison.OrdinalIgnoreCase))
            {if(check!=null)check();var edge=successors[at][0];result.Representative.Add(edge);at=edge.Target;}
            queue.Clear();queue.Enqueue(source);result.Depths.Add(source,foundDepth);
            var emitted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(queue.Count>0)
            {
                if(check!=null)check();string current=queue.Dequeue();List<GraphEdge> outgoing;
                if(!successors.TryGetValue(current,out outgoing))continue;
                foreach(var edge in outgoing)
                {
                    string key=edge.Source.Length+":"+edge.Source+edge.Target.Length+":"+edge.Target+edge.EdgeType;
                    if(!emitted.Add(key))continue;
                    if(result.Edges.Count>=maxEdges){result.Limited=true;if(!stopped)result.Status="EdgeLimit";queue.Clear();break;}
                    result.Edges.Add(edge);
                    if(!result.Depths.ContainsKey(edge.Target)){result.Depths.Add(edge.Target,distances[edge.Target]);queue.Enqueue(edge.Target);}
                }
            }
            if(!result.Limited)result.Status="Complete";
            return result;
        }

        public sealed class WaypointRoutesResult
        {
            public bool Found, Limited;
            public string Status="Complete";
            public int FailedLeg, HopCount, Visited;
            public string[] Waypoints;
            public List<ShortestRoutesResult> Legs=new List<ShortestRoutesResult>();
            public List<GraphEdge> Edges=new List<GraphEdge>();
            public Dictionary<string,int> Depths=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        }
        public WaypointRoutesResult WaypointPaths(string[] waypoints,int maxVisited,int maxEdges,string[] allowedTypes)
        {return WaypointPaths(waypoints,maxVisited,maxEdges,allowedTypes,null);}
        private WaypointRoutesResult WaypointPaths(string[] waypoints,int maxVisited,int maxEdges,string[] allowedTypes,Action check)
        {
            if(waypoints==null||waypoints.Length<2||waypoints.Length>4)throw new ArgumentException("Use two to four ordered objects, including source and target.");
            foreach(string id in waypoints)if(string.IsNullOrWhiteSpace(id))throw new ArgumentException("Every waypoint must identify an object.");
            var result=new WaypointRoutesResult{Waypoints=(string[])waypoints.Clone(),Found=true};
            var emitted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(int leg=0;leg<waypoints.Length-1;leg++)
            {
                if(check!=null)check();
                var paths=AllShortestPaths(waypoints[leg],waypoints[leg+1],maxVisited,maxEdges,allowedTypes,check);
                result.Legs.Add(paths);result.Visited+=paths.Visited;result.HopCount+=paths.HopCount;
                if(paths.Limited){result.Limited=true;if(result.FailedLeg==0)result.Status=paths.Status;}
                if(!paths.Found&&result.FailedLeg==0){result.Found=false;result.FailedLeg=leg+1;result.Status=paths.Limited?paths.Status:"NoPath";}
                foreach(var edge in paths.Edges)
                {
                    string key=edge.Source.Length+":"+edge.Source+edge.Target.Length+":"+edge.Target+edge.EdgeType;
                    if(!emitted.Add(key))continue;
                    if(result.Edges.Count>=maxEdges){result.Limited=true;if(result.FailedLeg==0)result.Status="EdgeLimit";continue;}
                    result.Edges.Add(edge);
                    if(!result.Depths.ContainsKey(edge.Source))result.Depths.Add(edge.Source,0);
                    if(!result.Depths.ContainsKey(edge.Target))result.Depths.Add(edge.Target,0);
                }
            }
            foreach(string id in waypoints)if(!result.Depths.ContainsKey(id))result.Depths.Add(id,0);
            if(!result.Found)result.HopCount=0;
            // A merged multi-leg graph may revisit a node or edge. It is an ordered
            // sequence of independently shortest legs, not a single simple path.
            return result;
        }

        private Statement Incoming()
        {
            return new Statement(this, "SELECT source,type,evidence FROM edges WHERE target=?1 AND "+TraversalPredicate(""));
        }

        // One policy for path queries, previews, report traversal and path comparison.
        // The vocabulary is internal, so relationship labels never become supplied SQL.
        private static string TraversalPredicate(string prefix)
        {
            var types=new List<string>();
            foreach(string type in GraphRelationships.FilterTypes())
                if(type!="MemberOf")types.Add("'"+type+"'");
            return prefix+"source<>"+prefix+"target AND "+prefix+"source_dn=0 AND "+prefix+"target_dn=0 AND (("+
                prefix+"bucket='memberof' AND "+prefix+"type COLLATE NOCASE='MemberOf') OR ("+
                prefix+"bucket IN ('acl','access') AND "+prefix+"type COLLATE NOCASE IN ("+string.Join(",",types.ToArray())+"))) AND ("+
                prefix+"type COLLATE NOCASE<>'MemberOf' OR NOT EXISTS(SELECT 1 FROM nodes n WHERE n.id="+prefix+"target AND n.security_enabled='false')) AND ("+
                prefix+"type COLLATE NOCASE<>'AllExtendedRights' OR NOT EXISTS(SELECT 1 FROM nodes n WHERE n.id="+prefix+"target AND n.kind NOT IN ('users','computers','domains'))) AND ("+
                prefix+"type COLLATE NOCASE<>'GPOAppliesTo' OR ("+prefix+"bucket='access' AND instr("+prefix+"evidence,'\"Derivation\":\"GpoComputerScopeV1\"')>0 AND EXISTS(SELECT 1 FROM nodes s WHERE s.id="+prefix+"source AND s.kind='gpos') AND EXISTS(SELECT 1 FROM nodes t WHERE t.id="+prefix+"target AND t.kind='computers'))) AND ("+
                prefix+"type COLLATE NOCASE<>'ADCSESC1' OR ("+prefix+"bucket='access' AND instr("+prefix+"evidence,'\"Derivation\":\"AdcsEsc1DirectRootV1\"')>0 AND EXISTS(SELECT 1 FROM nodes t WHERE t.id="+prefix+"target AND t.kind='domains')))";
        }

        private static GraphEdge ReadEdge(Statement row, string target)
        {
            return new GraphEdge { Source = row.Text(0), Target = target, EdgeType = GraphRelationships.CanonicalType(row.Text(1)), Evidence=row.Text(2) };
        }
    }
}
