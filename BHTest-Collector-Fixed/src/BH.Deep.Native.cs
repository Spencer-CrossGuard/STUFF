using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace BH.Native
{
    public sealed class DiskAnalysisProgress
    {
        public long Visited, Processed, Findings, Pending;
        public string Status, StopReason;
        public double ElapsedSeconds;
        public int Depth;
    }
    public sealed partial class GraphIndex
    {
        private string DeepValue(string key)
        {
            using(var s=new Statement(this,"SELECT value FROM analysis_meta WHERE key=?1"))
            {s.Bind(1,key);return s.Read()?s.Text(0):null;}
        }
        private void SetDeepValue(string key,string value)
        {
            using(var s=new Statement(this,"INSERT OR REPLACE INTO analysis_meta VALUES(?1,?2)"))
            {s.Bind(1,key);s.Bind(2,value);s.Read();}
        }
        public bool HasDiskAnalysis()
        {
            return Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='analysis_meta'")>0;
        }
        public string DiskSourceFingerprint(){return HasDiskAnalysis()?DeepValue("source"):null;}
        public void PrepareDiskAnalysis(string[] targets,string fingerprint)
        {if(!HasDiskAnalysis())InitializeDiskAnalysis(targets,fingerprint);}
        public DiskAnalysisProgress DiskAnalysisState()
        {
            if(!HasDiskAnalysis())return new DiskAnalysisProgress {Status="NotStarted",StopReason="No saved analysis"};
            return new DiskAnalysisProgress {
                Visited=long.Parse(DeepValue("visited")),Processed=long.Parse(DeepValue("processed")),
                Findings=long.Parse(DeepValue("findings")),Status=DeepValue("status"),StopReason=DeepValue("reason"),
                Pending=long.Parse(DeepValue("visited"))-long.Parse(DeepValue("processed")),Depth=int.Parse(DeepValue("depth"))
            };
        }
        private void InitializeDiskAnalysis(string[] targets,string fingerprint)
        {
            Execute("BEGIN IMMEDIATE");
            try {
            Execute(@"CREATE TABLE analysis_meta(key TEXT PRIMARY KEY,value TEXT) WITHOUT ROWID;
                CREATE TABLE analysis_nodes(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT COLLATE NOCASE UNIQUE,
                next_id TEXT,edge_type TEXT,root TEXT,depth INTEGER,weight INTEGER,missing INTEGER,
                members INTEGER,score INTEGER,processed INTEGER DEFAULT 0,
                cursor_bucket TEXT DEFAULT '',cursor_source TEXT DEFAULT '',cursor_type TEXT DEFAULT '');
                CREATE INDEX analysis_queue ON analysis_nodes(seq) WHERE processed=0;
                CREATE INDEX analysis_rank ON analysis_nodes(score DESC,seq) WHERE weight>0;");
                using(var insert=new Statement(this,@"INSERT OR IGNORE INTO analysis_nodes
                    (id,root,depth,weight,missing,members,score) VALUES(?1,?1,0,0,0,0,0)"))
                    foreach(string id in targets){insert.Bind(1,id);insert.Read();insert.Reset();}
                SetDeepValue("version","1");SetDeepValue("source",fingerprint);
                SetDeepValue("queue_format","partial-v1");
                var canonical=(string[])targets.Clone();Array.Sort(canonical,StringComparer.OrdinalIgnoreCase);
                SetDeepValue("targets",string.Join("\n",canonical).ToUpperInvariant());
                SetDeepValue("visited",Scalar("SELECT count(*) FROM analysis_nodes").ToString(CultureInfo.InvariantCulture));
                SetDeepValue("processed","0");SetDeepValue("findings","0");SetDeepValue("depth","0");
                SetDeepValue("status","Ready");SetDeepValue("reason","Ready to analyze");Execute("COMMIT");
            }catch{Execute("ROLLBACK");throw;}
        }
        private void SaveDiskState(DiskAnalysisProgress p)
        {
            SetDeepValue("visited",p.Visited.ToString(CultureInfo.InvariantCulture));
            SetDeepValue("processed",p.Processed.ToString(CultureInfo.InvariantCulture));
            SetDeepValue("findings",p.Findings.ToString(CultureInfo.InvariantCulture));
            SetDeepValue("depth",p.Depth.ToString(CultureInfo.InvariantCulture));
            SetDeepValue("status",p.Status);SetDeepValue("reason",p.StopReason);
        }
        // A fixed-size batch is read before writes. Both the FIFO frontier and predecessor
        // links live in SQLite. Edge cursors are committed with discovered identities, so a
        // stopped or interrupted batch is replayed without skipping or duplicating work.
        public DiskAnalysisProgress AnalyzeDisk(string[] targets,string fingerprint,int depthLimit,long visitLimit,
            int secondsLimit,string cancelFile,Action<DiskAnalysisProgress> progress)
        {
            if(depthLimit<0 || visitLimit<0 || secondsLimit<0)throw new ArgumentOutOfRangeException();
            if(!HasDiskAnalysis())InitializeDiskAnalysis(targets,fingerprint);
            var canonical=(string[])targets.Clone();Array.Sort(canonical,StringComparer.OrdinalIgnoreCase);
            if(DeepValue("version")!="1" || DeepValue("source")!=fingerprint ||
                DeepValue("targets")!=string.Join("\n",canonical).ToUpperInvariant())
                throw new InvalidDataException("Saved analysis belongs to a different snapshot or target set. Use a new output folder.");
            if(DeepValue("queue_format")!="partial-v1")
            {
                Execute("BEGIN IMMEDIATE");
                try{Execute("DROP INDEX analysis_queue; CREATE INDEX analysis_queue ON analysis_nodes(seq) WHERE processed=0;");SetDeepValue("queue_format","partial-v1");Execute("COMMIT");}
                catch{Execute("ROLLBACK");throw;}
            }
            var state=DiskAnalysisState();var clock=Stopwatch.StartNew();double lastUpdate=-2;
            if(state.Status=="Completed")return state;
            state.Status="Running";state.StopReason="Search in progress";
            var allowed=new HashSet<string>(GraphRelationships.FilterTypes(),StringComparer.OrdinalIgnoreCase);
            bool transaction=false;int batches=0;double lastCheckpoint=0;
            using(var frontier=new Statement(this,@"SELECT seq,id,root,depth,weight,missing,cursor_bucket,cursor_source,cursor_type
                FROM analysis_nodes WHERE processed=0 ORDER BY seq LIMIT 1"))
            using(var incoming=new Statement(this,@"SELECT source,type,bucket FROM edges WHERE target=?1
                AND bucket IN ('acl','memberof','access') AND source<>target AND source_dn=0 AND target_dn=0
                AND (bucket<>'memberof' OR NOT EXISTS(SELECT 1 FROM nodes WHERE id=?1 AND security_enabled='false'))
                AND (bucket,source,type)>(?2,?3,?4)
                ORDER BY bucket,source,type LIMIT 256"))
            using(var exists=new Statement(this,"SELECT 1 FROM analysis_nodes WHERE id=?1"))
            using(var insert=new Statement(this,@"INSERT INTO analysis_nodes
                (id,next_id,edge_type,root,depth,weight,missing,members,score) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)"))
            using(var cursor=new Statement(this,@"UPDATE analysis_nodes SET cursor_bucket=?2,cursor_source=?3,cursor_type=?4,
                processed=?5 WHERE seq=?1"))
            {
                while(true)
                {
                    state.ElapsedSeconds=clock.Elapsed.TotalSeconds;state.Pending=state.Visited-state.Processed;
                    if(cancelFile!=null && File.Exists(cancelFile)){state.Status="Cancelled";state.StopReason="Cancellation requested; resume is available";break;}
                    if(secondsLimit>0 && clock.Elapsed.TotalSeconds>=secondsLimit){state.Status="TimeLimit";state.StopReason="Time budget reached; resume is available";break;}
                    if(!frontier.Read()){frontier.Reset();state.Status="Completed";state.StopReason="Frontier exhausted for supported relationships and the selected targets";break;}
                    string sequence=frontier.Text(0),id=frontier.Text(1),root=frontier.Text(2);
                    int depth=int.Parse(frontier.Text(3)),weight=int.Parse(frontier.Text(4)),missing=int.Parse(frontier.Text(5));
                    string lastBucket=frontier.Text(6),lastSource=frontier.Text(7),lastType=frontier.Text(8);frontier.Reset();
                    state.Depth=depth;
                    incoming.Bind(1,id);incoming.Bind(2,lastBucket);incoming.Bind(3,lastSource);incoming.Bind(4,lastType);
                    var batch=new List<string[]>();while(incoming.Read())batch.Add(new[]{incoming.Text(0),incoming.Text(1),incoming.Text(2)});incoming.Reset();
                    if(!transaction){Execute("BEGIN IMMEDIATE");transaction=true;}
                    try {
                        bool blocked=false;
                        foreach(var edge in batch)
                        {
                            bool found;
                            exists.Bind(1,edge[0]);found=exists.Read();exists.Reset();
                            if(allowed.Contains(edge[1]) && !found)
                            {
                                if(depthLimit>0 && depth>=depthLimit){state.Status="DepthLimit";state.StopReason="Depth budget reached; raise it or select Deep analysis to resume";blocked=true;break;}
                                if(visitLimit>0 && state.Visited>=visitLimit){state.Status="VisitLimit";state.StopReason="Identity budget reached; raise it to resume";blocked=true;break;}
                                int childWeight=Math.Max(weight,CapabilityWeight(edge[1]));
                                var node=Node(edge[0]);int childMissing=missing!=0 || node==null?1:0;
                                long members=node!=null && node[1]=="groups" && childWeight>0?DirectMemberCount(edge[0]):0;
                                int score=childWeight==0?0:30+childWeight+(depth+1<=2?5:0)+(members>=100?15:members>0?5:0)-(childMissing!=0?10:0);
                                insert.Bind(1,edge[0]);insert.Bind(2,id);insert.Bind(3,edge[1]);insert.Bind(4,root);
                                insert.Bind(5,(depth+1).ToString(CultureInfo.InvariantCulture));insert.Bind(6,childWeight.ToString(CultureInfo.InvariantCulture));
                                insert.Bind(7,childMissing.ToString());insert.Bind(8,members.ToString(CultureInfo.InvariantCulture));insert.Bind(9,score.ToString());
                                insert.Read();insert.Reset();state.Visited++;if(childWeight>0)state.Findings++;
                            }
                            lastBucket=edge[2];lastSource=edge[0];lastType=edge[1];
                        }
                        bool done=!blocked && batch.Count<256;
                        cursor.Bind(1,sequence);cursor.Bind(2,lastBucket);cursor.Bind(3,lastSource);cursor.Bind(4,lastType);cursor.Bind(5,done?"1":"0");cursor.Read();cursor.Reset();
                        if(done)state.Processed++;batches++;
                        if(blocked || batches>=128 || clock.Elapsed.TotalSeconds-lastCheckpoint>=1)
                        {SaveDiskState(state);Execute("COMMIT");transaction=false;batches=0;lastCheckpoint=clock.Elapsed.TotalSeconds;}
                        if(blocked)break;
                    }catch{if(transaction){Execute("ROLLBACK");transaction=false;}throw;}
                    if(progress!=null && clock.Elapsed.TotalSeconds-lastUpdate>=1){lastUpdate=clock.Elapsed.TotalSeconds;state.ElapsedSeconds=clock.Elapsed.TotalSeconds;state.Pending=state.Visited-state.Processed;progress(state);}
                }
            }
            state.Pending=state.Visited-state.Processed;state.ElapsedSeconds=clock.Elapsed.TotalSeconds;
            if(!transaction)Execute("BEGIN IMMEDIATE");try{SaveDiskState(state);Execute("COMMIT");}catch{Execute("ROLLBACK");throw;}
            if(progress!=null)progress(state);return state;
        }
        public List<ExposureFinding> DiskFindingPage(int afterScore,long afterSequence,int limit)
        {
            if(limit<1 || limit>200)throw new ArgumentOutOfRangeException("limit");
            var result=new List<ExposureFinding>();if(!HasDiskAnalysis())return result;
            var entries=new List<string[]>();
            // Two range seeks handle mixed score-descending/sequence-ascending order
            // without rescanning previously viewed candidates through a large OR filter.
            ReadDiskPage(entries,"score=?1 AND seq>?2",afterScore,afterSequence,limit);
            if(entries.Count<limit)ReadDiskPage(entries,"score<?1",afterScore,afterSequence,limit-entries.Count);
            foreach(var row in entries)
            {
                var f=new ExposureFinding {Source=row[0],Target=row[1],HopCount=int.Parse(row[2]),MissingObjects=row[4]=="1",
                    DirectMembers=long.Parse(row[5]),Score=int.Parse(row[6]),Sequence=long.Parse(row[7])};
                f.Priority=f.Score>=65?"High review priority":"Medium review priority";
                f.Reasons.Add("Recorded chain reaches a recognized privileged group or collected domain (+30).");
                f.Reasons.Add("Strongest permission or access capability on this chain (+"+row[3]+").");
                if(f.DirectMembers>0)f.Reasons.Add(f.DirectMembers+" recorded direct group members (+"+(f.DirectMembers>=100?15:5)+").");
                if(f.HopCount<=2)f.Reasons.Add("At most two recorded hops (+5).");
                if(f.MissingObjects)f.Reasons.Add("One or more source objects are outside the supplied object data (-10; validate identity).");
                string current=f.Source;
                using(var next=new Statement(this,"SELECT next_id,edge_type FROM analysis_nodes WHERE id=?1"))
                    while(f.Steps.Count<256)
                    {next.Bind(1,current);bool found=next.Read();string target=found?next.Text(0):null,type=found?next.Text(1):null;next.Reset();if(target==null)break;f.Steps.Add(new GraphEdge{Source=current,Target=target,EdgeType=type});current=target;}
                f.PathLimited=f.Steps.Count<f.HopCount;f.NextPathId=f.PathLimited?current:null;
                // Stable path IDs are retained for fully embedded chains; long chains use identity+target.
                f.Id=f.PathLimited?FindingId(new List<GraphEdge>{new GraphEdge{Source=f.Source,Target=f.Target,EdgeType="LongRecordedChain"}}):FindingId(f.Steps);
                result.Add(f);
            }
            return result;
        }
        private void ReadDiskPage(List<string[]> rows,string predicate,int score,long sequence,int limit)
        {
            using(var page=new Statement(this,@"SELECT id,root,depth,weight,missing,members,score,seq FROM analysis_nodes
                WHERE weight>0 AND "+predicate+" ORDER BY score DESC,seq LIMIT "+limit))
            {
                page.Bind(1,score.ToString());if(predicate.IndexOf("?2",StringComparison.Ordinal)>=0)page.Bind(2,sequence.ToString());
                while(page.Read()){var row=new string[8];for(int i=0;i<8;i++)row[i]=page.Text(i);rows.Add(row);}
            }
        }
        public List<GraphEdge> DiskPathPage(string start,int limit)
        {
            if(limit<1 || limit>1000)throw new ArgumentOutOfRangeException("limit");
            var rows=new List<GraphEdge>();
            using(var next=new Statement(this,"SELECT next_id,edge_type FROM analysis_nodes WHERE id=?1"))
                while(rows.Count<limit){next.Bind(1,start);bool found=next.Read();string target=found?next.Text(0):null,type=found?next.Text(1):null;next.Reset();if(target==null)break;rows.Add(new GraphEdge{Source=start,Target=target,EdgeType=type});start=target;}
            return rows;
        }
    }
}
