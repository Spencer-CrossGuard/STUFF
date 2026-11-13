using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BH.Native
{
    public sealed class ExposureFinding
    {
        public string Id;
        public string Source;
        public string Target;
        public List<GraphEdge> Steps = new List<GraphEdge>();
        public int Score;
        public string Priority;
        public long DirectMembers;
        public bool MissingObjects;
        public List<string> Reasons = new List<string>();
        public long Sequence;
        public int HopCount;
        public bool PathLimited;
        public string NextPathId;
    }
    public sealed class ExposureAnalysis
    {
        public List<ExposureFinding> Findings = new List<ExposureFinding>();
        public int Visited;
        public bool Truncated;
        public int DepthLimit;
    }
    public sealed class SnapshotComparison
    {
        public long AddedNodes, RemovedNodes, AddedEdges, RemovedEdges, UnchangedEdges;
        public List<GraphEdge> AddedSample = new List<GraphEdge>();
        public List<GraphEdge> RemovedSample = new List<GraphEdge>();
    }
    public sealed partial class GraphIndex
    {
        private static int CapabilityWeight(string type)
        {
            switch(type)
            {
                case "DCSync": case "GenericAll": case "WriteDacl": return 40;
                case "AddMember": case "AddSelf": case "WriteOwner": case "Owns":
                case "ForceChangePassword": case "AddKeyCredentialLink": return 35;
                case "MemberOf": return 0;
                case "HasSession": return 15;
                default: return 25;
            }
        }
        private static string FindingId(List<GraphEdge> steps)
        {
            // Length prefixes prevent ambiguous identifiers; normalize Windows identity casing.
            var text = new StringBuilder();
            foreach(var e in steps)
                foreach(string token in new[] { e.Source, e.Target, e.EdgeType })
                { string value=token.ToUpperInvariant(); text.Append(value.Length).Append(':').Append(value); }
            using(var hash=SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        public long DirectMemberCount(string id)
        {
            using(var s=new Statement(this,"SELECT count(*) FROM edges WHERE target=?1 AND bucket='memberof' AND source<>target"))
            { s.Bind(1,id); s.Read(); return long.Parse(s.Text(0)); }
        }
        public List<string[]> DomainTargets() { return Query("SELECT id,name FROM nodes WHERE kind='domains'",2); }
        // One reverse BFS from all privileged targets. Each identity retains one shortest
        // recorded chain. The visit, depth and finding caps bound memory and report size.
        public ExposureAnalysis AnalyzeExposures(string[] targets, int maxVisited, int maxFindings, int maxDepth)
        {
            if(maxVisited<1 || maxFindings<1 || maxDepth<1) throw new ArgumentOutOfRangeException();
            var result=new ExposureAnalysis { DepthLimit=maxDepth };
            var next=new Dictionary<string,GraphEdge>(StringComparer.OrdinalIgnoreCase);
            var depths=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            var queue=new Queue<string>();
            var allowed=new HashSet<string>(GraphRelationships.FilterTypes(),StringComparer.OrdinalIgnoreCase);
            foreach(string target in targets)
            {
                if(next.ContainsKey(target)) continue;
                if(next.Count>=maxVisited) { result.Truncated=true; break; }
                next.Add(target,new GraphEdge()); depths.Add(target,0); queue.Enqueue(target);
            }
            using(var incoming=Incoming())
            {
                while(queue.Count>0 && !result.Truncated)
                {
                    string current=queue.Dequeue();
                    incoming.Bind(1,current);
                    while(incoming.Read())
                    {
                        var e=ReadEdge(incoming,current);
                        if(!allowed.Contains(e.EdgeType) || next.ContainsKey(e.Source)) continue;
                        if(depths[current]>=maxDepth || next.Count>=maxVisited) { result.Truncated=true; break; }
                        next.Add(e.Source,e);depths.Add(e.Source,depths[current]+1);queue.Enqueue(e.Source);
                        var path=new List<GraphEdge>(); string id=e.Source; int weight=0; bool missing=false;
                        while(next[id].Source!=null) {var step=next[id];path.Add(step);weight=Math.Max(weight,CapabilityWeight(step.EdgeType));id=step.Target;}
                        // Pure memberships are expected context, not a security finding.
                        if(weight==0)continue;
                        foreach(var step in path)if(Node(step.Source)==null)missing=true;
                        var finding=new ExposureFinding {Source=e.Source,Target=id,Steps=path,MissingObjects=missing};
                        finding.Id=FindingId(path);finding.Score=30+weight;
                        finding.Reasons.Add("Recorded chain reaches a recognized privileged group or collected domain (+30).");
                        finding.Reasons.Add("Strongest permission or access capability on this chain (+"+weight+").");
                        var source=Node(e.Source);
                        if(source!=null && source[1]=="groups")
                        {
                            finding.DirectMembers=DirectMemberCount(e.Source);
                            int breadth=finding.DirectMembers>=100?15:finding.DirectMembers>0?5:0;
                            finding.Score+=breadth;
                            if(breadth>0)finding.Reasons.Add(finding.DirectMembers+" recorded direct group members (+"+breadth+").");
                        }
                        if(path.Count<=2){finding.Score+=5;finding.Reasons.Add("At most two recorded hops (+5).");}
                        if(missing){finding.Score-=10;finding.Reasons.Add("One or more source objects are outside the supplied object data (-10; validate identity).");}
                        finding.Priority=finding.Score>=65?"High review priority":"Medium review priority";
                        result.Findings.Add(finding);
                        if(result.Findings.Count>=maxFindings){result.Truncated=true;break;}
                    }
                    incoming.Reset();
                }
            }
            result.Visited=next.Count;
            result.Findings.Sort((a,b)=>{int order=b.Score.CompareTo(a.Score);return order!=0?order:StringComparer.Ordinal.Compare(a.Id,b.Id);});
            return result;
        }
        public bool ContainsPath(List<GraphEdge> steps)
        {
            using(var s=new Statement(this,@"SELECT 1 FROM edges WHERE target=?1 AND source=?2 AND type=?3
                AND bucket IN ('acl','access','memberof') AND source_dn=0 AND target_dn=0
                AND (bucket<>'memberof' OR NOT EXISTS(SELECT 1 FROM nodes WHERE id=?1 AND security_enabled='false')) LIMIT 1"))
                foreach(var e in steps){s.Bind(1,e.Target);s.Bind(2,e.Source);s.Bind(3,e.EdgeType);bool found=s.Read();s.Reset();if(!found)return false;}
            return steps.Count>0;
        }
        public List<string[]> ObjectRelationships(string id, int limit)
        {
            if(limit<1 || limit>1000)throw new ArgumentOutOfRangeException("limit");
            var rows=new List<string[]>();
            using(var s=new Statement(this,"SELECT source,target,type,bucket FROM edges WHERE source=?1 OR target=?1 LIMIT "+limit))
            {s.Bind(1,id);while(s.Read())rows.Add(new[]{s.Text(0),s.Text(1),s.Text(2),s.Text(3)});}
            return rows;
        }
        public SnapshotComparison CompareSnapshot(string baselinePath)
        {
            if(!File.Exists(baselinePath))throw new FileNotFoundException("Baseline graph index was not found.",baselinePath);
            using(var attach=new Statement(this,"ATTACH DATABASE ?1 AS baseline")){attach.Bind(1,Path.GetFullPath(baselinePath));attach.Read();}
            try
            {
                var r=new SnapshotComparison();
                r.AddedNodes=Scalar("SELECT count(*) FROM nodes n WHERE NOT EXISTS(SELECT 1 FROM baseline.nodes b WHERE b.id=n.id)");
                r.RemovedNodes=Scalar("SELECT count(*) FROM baseline.nodes n WHERE NOT EXISTS(SELECT 1 FROM main.nodes b WHERE b.id=n.id)");
                string added=@"FROM main.edges e WHERE NOT EXISTS(SELECT 1 FROM baseline.edges b WHERE
                    b.target=e.target AND b.bucket=e.bucket AND b.source=e.source AND b.type=e.type AND b.source_dn=e.source_dn AND b.target_dn=e.target_dn)";
                string removed=@"FROM baseline.edges e WHERE NOT EXISTS(SELECT 1 FROM main.edges b WHERE
                    b.target=e.target AND b.bucket=e.bucket AND b.source=e.source AND b.type=e.type AND b.source_dn=e.source_dn AND b.target_dn=e.target_dn)";
                r.AddedEdges=Scalar("SELECT count(*) "+added);r.RemovedEdges=Scalar("SELECT count(*) "+removed);
                r.UnchangedEdges=Scalar("SELECT count(*) FROM main.edges")-r.AddedEdges;
                foreach(var row in Query("SELECT source,target,type "+added+" LIMIT 50",3))r.AddedSample.Add(new GraphEdge{Source=row[0],Target=row[1],EdgeType=row[2]});
                foreach(var row in Query("SELECT source,target,type "+removed+" LIMIT 50",3))r.RemovedSample.Add(new GraphEdge{Source=row[0],Target=row[1],EdgeType=row[2]});
                return r;
            }
            finally{Execute("DETACH DATABASE baseline");}
        }
        private long Scalar(string sql){using(var s=new Statement(this,sql)){s.Read();return long.Parse(s.Text(0));}}
    }
}
