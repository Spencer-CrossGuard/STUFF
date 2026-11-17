using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BH.Native
{
    // All viewer connections open SQLITE_OPEN_READONLY. Requests never run supplied SQL.
    public sealed partial class GraphIndex
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ViewerProgress(IntPtr unused);
        [DllImport(Library, CallingConvention=CallingConvention.Cdecl)]
        private static extern void sqlite3_progress_handler(IntPtr handle,int instructions,ViewerProgress callback,IntPtr unused);
        private ViewerProgress viewerProgress;
        private Stopwatch viewerClock;
        private volatile bool viewerCancelled;
        public void ViewerCancel(){viewerCancelled=true;}
        public void ViewerBudget()
        {
            Execute("PRAGMA query_only=ON; PRAGMA cache_size=-8192;");
            viewerClock=Stopwatch.StartNew();
            viewerProgress=delegate(IntPtr unused){return viewerCancelled||viewerClock.ElapsedMilliseconds>10000?1:0;};
            sqlite3_progress_handler(db,1000,viewerProgress,IntPtr.Zero);
        }
        private void ViewerCheck(){if(viewerCancelled)throw new OperationCanceledException("Query cancelled; results are incomplete.");if(viewerClock.ElapsedMilliseconds>10000)throw new IOException("Query time limit reached. Narrow the query or choose a closer source/target.");}
        private Dictionary<string,object> VNode(string id)
        {var n=Node(id);return ViewerJson.Map("i",id,"n",n==null?id:n[0],"t",n==null?"?":n[1]);}
        private Dictionary<string,object> VEdge(GraphEdge e)
        {var s=VNode(e.Source);var t=VNode(e.Target);return ViewerJson.Map("s",e.Source,"t",e.Target,"ty",e.EdgeType,"sn",s["n"],"tn",t["n"],"st",s["t"],"tt",t["t"],"Evidence",e.Evidence);}
        private List<object> VEdges(List<GraphEdge> edges)
        {var result=new List<object>();foreach(var e in edges){ViewerCheck();result.Add(VEdge(e));}return result;}
        public object ViewerSummary()
        {return ViewerJson.Map("Nodes",NodeCounts(),"Edges",EdgeCounts(),"Analysis",DiskAnalysisState(),"GpoScope",GpoScopeNote(),"OwnershipScope",OwnershipScopeNote(),"AdcsScope",AdcsScopeNote(),"PrerequisiteGaps",AdcsPrerequisiteRows(30,null),"PrerequisiteGapCount",AdcsPrerequisiteCount());}
        // Scan bounded windows in primary-key order: no index creation or unbounded name-sort.
        // The returned cursor is the last INSPECTED row, including nonmatching rows.
        public object ViewerSearch(string query,string after)
        {
            var rows=new List<object>();string cursor=after;int scanned=0;bool more=false;
            using(var s=new Statement(this,"SELECT id,name,kind FROM nodes WHERE id>?1 ORDER BY id LIMIT 50001"))
            {
                s.Bind(1,after);
                while(s.Read())
                {
                    if(scanned==50000||rows.Count==50){more=true;break;}
                    ViewerCheck();cursor=s.Text(0);scanned++;string name=s.Text(1)??cursor;
                    if(query.Length==0||cursor.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0||name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)
                        rows.Add(ViewerJson.Map("i",cursor,"n",name,"t",s.Text(2)));
                }
            }
            return ViewerJson.Map("Rows",rows,"After",cursor,"More",more,"Scanned",scanned);
        }
        public object ViewerFindings(int score,long sequence,string query)
        {
            var rows=new List<object>();int scanned=0,lastScore=score;long lastSequence=sequence;bool more=false;
            if(!HasDiskAnalysis())return ViewerJson.Map("Rows",rows,"More",false,"Scanned",0,"AfterScore",score,"AfterSequence",sequence);
            RequireCurrentAnalysis();
            // Two indexed range seeks preserve score DESC / sequence ASC ordering.
            foreach(string predicate in new string[]{"a.score=?1 AND a.seq>?2","a.score<?1"})
            {
                using(var s=new Statement(this,@"SELECT a.id,a.root,a.depth,a.score,a.seq,n.name,t.name FROM analysis_nodes a
                    LEFT JOIN nodes n ON n.id=a.id LEFT JOIN nodes t ON t.id=a.root
                    WHERE a.weight>0 AND "+predicate+" ORDER BY a.score DESC,a.seq LIMIT 10001"))
                {
                    s.Bind(1,score.ToString(CultureInfo.InvariantCulture));if(predicate.IndexOf("?2",StringComparison.Ordinal)>=0)s.Bind(2,sequence.ToString(CultureInfo.InvariantCulture));
                    while(s.Read())
                    {
                        if(scanned==10000||rows.Count==50){more=true;break;}
                        ViewerCheck();scanned++;lastScore=int.Parse(s.Text(3));lastSequence=long.Parse(s.Text(4));
                        string source=s.Text(0),target=s.Text(1),sn=s.Text(5)??source,tn=s.Text(6)??target;
                        if(query.Length==0||(sn+" "+tn+" "+source+" "+target).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)
                            rows.Add(ViewerJson.Map("Source",ViewerJson.Map("i",source,"n",sn),"Target",ViewerJson.Map("i",target,"n",tn),"HopCount",int.Parse(s.Text(2)),"Score",lastScore));
                    }
                }
                if(more)break;
            }
            return ViewerJson.Map("Rows",rows,"More",more,"Scanned",scanned,"AfterScore",lastScore,"AfterSequence",lastSequence);
        }
        public object ViewerRecordedPath(string start)
        {
            if(!HasDiskAnalysis())throw new IOException("This index has no saved deep analysis. Generate a Deep report first, or use Find a path.");
            string root;int depth,score,weight;long members;bool missing;
            using(var s=new Statement(this,"SELECT root,depth,score,weight,missing,members FROM analysis_nodes WHERE id=?1"))
            {s.Bind(1,start);if(!s.Read())throw new IOException("No saved path for this object.");root=s.Text(0);depth=int.Parse(s.Text(1));score=int.Parse(s.Text(2));weight=int.Parse(s.Text(3));missing=s.Text(4)=="1";members=long.Parse(s.Text(5));}
            var reasons=new List<string>();reasons.Add("Recorded chain reaches a selected review target (+30).");reasons.Add("Strongest recorded permission or access capability on this chain (+"+weight+").");
            if(members>0)reasons.Add(members+" recorded direct group members (+"+(members>=100?15:5)+").");if(depth<=2)reasons.Add("At most two recorded hops (+5).");if(missing)reasons.Add("A source identity is outside the supplied object records (-10; validate identity).");
            var edges=DiskPathPage(start,128);string next=edges.Count==0?null:edges[edges.Count-1].Target;
            return ViewerJson.Map("Source",VNode(start),"Target",VNode(root),"HopCount",depth,"Score",score,
                "Priority",score>=65?"High review priority":"Medium review priority","Observation","Recorded path",
                "Reasons",reasons,
                "Evidence","Recorded relationships from the selected index; effective access requires validation.","MissingObjects",missing,
                "Steps",VEdges(edges),"PathLimited",edges.Count<depth,"NextPathId",edges.Count<depth?next:null);
        }
        public object ViewerObject(string id)
        {
            var node=VNode(id);string record=null;bool limited=false;
            using(var s=new Statement(this,"SELECT record FROM nodes WHERE id=?1"))
            {
                s.Bind(1,id);
                if(s.Read())
                {
                    // Cap compressed input and decompressed output independently.
                    int bytes=sqlite3_column_bytes(s.handle,0);
                    if(bytes>2097152)limited=true;
                    else if(sqlite3_column_type(s.handle,0)!=4){record=s.Text(0);if(record!=null&&record.Length>1048576){record=null;limited=true;}}
                    else
                    {
                        var data=new byte[bytes];Marshal.Copy(sqlite3_column_blob(s.handle,0),data,0,bytes);
                        using(var input=new MemoryStream(data))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(gzip,Encoding.UTF8))
                        {var buffer=new char[4096];var b=new StringBuilder();int count;while((count=reader.Read(buffer,0,buffer.Length))>0){if(b.Length+count>1048576){limited=true;break;}b.Append(buffer,0,count);}if(!limited)record=b.ToString();}
                    }
                }
            }
            node["Record"]=record;var gaps=AdcsPrerequisiteRows(50,id);AppendGroupClassificationGap(gaps,id,record);node["PathPrerequisites"]=gaps;node["Limited"]=limited;return node;
        }
        // Incoming relationships use the index's target key. Outgoing browsing uses bounded
        // primary-key windows; the scanner does not yet create a source-side index.
        public object ViewerRelationships(string id,string direction,string target,string bucket,string source,string type)
        {
            var rows=new List<object>();int scanned=0;bool more=false;
            string sql=direction=="in"?"SELECT target,bucket,source,type,evidence FROM edges WHERE target=?1 AND (bucket,source,type)>(?2,?3,?4) ORDER BY bucket,source,type LIMIT 51":
                "SELECT target,bucket,source,type,evidence FROM edges WHERE (target,bucket,source,type)>(?1,?2,?3,?4) ORDER BY target,bucket,source,type LIMIT 50001";
            using(var s=new Statement(this,sql))
            {
                s.Bind(1,direction=="in"?id:target);s.Bind(2,bucket);s.Bind(3,source);s.Bind(4,type);
                while(s.Read())
                {
                    if(scanned==50000||rows.Count==50){more=true;break;}ViewerCheck();scanned++;
                    target=s.Text(0);bucket=s.Text(1);source=s.Text(2);type=s.Text(3);
                    if(direction=="in"||string.Equals(source,id,StringComparison.OrdinalIgnoreCase))rows.Add(VEdge(new GraphEdge{Source=source,Target=target,EdgeType=type,Evidence=s.Text(4)}));
                }
            }
            return ViewerJson.Map("Rows",rows,"More",more,"Scanned",scanned,"Target",target,"Bucket",bucket,"Source",source,"Type",type);
        }
        public object ViewerWaypointPaths(string[] queries,int maxVisited,int maxEdges,string[] allowedTypes)
        {
            if(queries==null||queries.Length<2||queries.Length>4)throw new ArgumentException("Use two to four ordered objects.");
            var ids=new string[queries.Length];
            for(int i=0;i<ids.Length;i++)
            {
                try{ids[i]=ViewerResolveObject(queries[i]);}
                catch(ArgumentException error){throw new ArgumentException("Object "+(i+1)+" in the ordered route: "+error.Message);}
            }
            var result=WaypointPaths(ids,maxVisited,maxEdges,allowedTypes,ViewerCheck);
            var nodes=new List<object>();foreach(var pair in result.Depths){var node=VNode(pair.Key);node["Depth"]=pair.Value;nodes.Add(node);}
            var legs=new List<object>();for(int i=0;i<result.Legs.Count;i++)
            {
                var leg=result.Legs[i];legs.Add(ViewerJson.Map("Number",i+1,"Source",VNode(ids[i]),"Target",VNode(ids[i+1]),
                    "Found",leg.Found,"Status",leg.Status,"Limited",leg.Limited,"HopCount",leg.HopCount,"Visited",leg.Visited,"Relationships",leg.Edges.Count));
            }
            var waypoints=new List<object>();foreach(string id in ids)waypoints.Add(VNode(id));
            string failure=result.FailedLeg==0?null:"No complete ordered route: leg "+result.FailedLeg+" ("+VNode(ids[result.FailedLeg-1])["n"]+" → "+VNode(ids[result.FailedLeg])["n"]+") "+(result.Legs[result.FailedLeg-1].Limited?"reached its search budget; its result is incomplete.":"has no path through the selected relationships.");
            return ViewerJson.Map("Status",result.Found?"Found":result.Status,"GraphStatus",result.Status,"Limited",result.Limited,
                "Visited",result.Visited,"Edges",VEdges(result.Edges),"Nodes",nodes,"Steps",VEdges(result.Edges),"HopCount",result.HopCount,
                "PathLimited",false,"NextPathId",null,"MultiLeg",true,"Legs",legs,"Waypoints",waypoints,"FailedLeg",result.FailedLeg,"Failure",failure,
                "Source",VNode(ids[0]),"Target",VNode(ids[ids.Length-1]));
        }
        public object ViewerAllShortestPaths(string source,string target,int maxVisited,int maxEdges,string[] allowedTypes)
        {
            source=ViewerResolveObject(source);target=ViewerResolveObject(target);
            var result=AllShortestPaths(source,target,maxVisited,maxEdges,allowedTypes,ViewerCheck);
            var nodes=new List<object>();foreach(var pair in result.Depths){var node=VNode(pair.Key);node["Depth"]=pair.Value;nodes.Add(node);}
            var preview=result.Representative.GetRange(0,Math.Min(128,result.Representative.Count));
            return ViewerJson.Map("Status",result.Found?"Found":result.Status,"GraphStatus",result.Status,"Limited",result.Limited,
                "Visited",result.Visited,"Edges",VEdges(result.Edges),"Nodes",nodes,"Steps",VEdges(preview),"HopCount",result.HopCount,
                "PathLimited",result.HopCount>preview.Count,"NextPathId",result.HopCount>preview.Count?preview[preview.Count-1].Target:null,
                "Source",VNode(source),"Target",VNode(target));
        }
        public object ViewerShortestPath(string source,string target,int maxVisited)
        {return ViewerShortestPath(source,target,maxVisited,null);}
        public object ViewerShortestPath(string source,string target,int maxVisited,string[] allowedTypes)
        {
            if(maxVisited<1||maxVisited>250000)throw new ArgumentOutOfRangeException("maxVisited");
            source=ViewerResolveObject(source);target=ViewerResolveObject(target);
            var allowed=allowedTypes==null?null:new HashSet<string>(allowedTypes,StringComparer.OrdinalIgnoreCase);
            var visited=new Dictionary<string,GraphEdge>(StringComparer.OrdinalIgnoreCase);var queue=new Queue<string>();
            visited.Add(target,new GraphEdge());queue.Enqueue(target);string status="NoPath";bool found=string.Equals(source,target,StringComparison.OrdinalIgnoreCase);
            while(queue.Count>0&&!found)
            {
                ViewerCheck();string current=queue.Dequeue();
                using(var s=Incoming())
                {
                    s.Bind(1,current);
                    while(s.Read())
                    {
                        ViewerCheck();if(allowed!=null&&!allowed.Contains(s.Text(1)))continue;string from=s.Text(0);if(visited.ContainsKey(from))continue;
                        if(visited.Count>=maxVisited){status="VisitLimit";queue.Clear();break;}
                        visited.Add(from,new GraphEdge{Source=from,Target=current,EdgeType=GraphRelationships.CanonicalType(s.Text(1)),Evidence=s.Text(2)});
                        if(string.Equals(from,source,StringComparison.OrdinalIgnoreCase)){found=true;break;}queue.Enqueue(from);
                    }
                }
            }
            var edges=new List<GraphEdge>();int hops=0;string next=source;
            if(found){status="Found";while(!string.Equals(next,target,StringComparison.OrdinalIgnoreCase)){var e=visited[next];hops++;if(edges.Count<128)edges.Add(e);next=e.Target;}}
            return ViewerJson.Map("Status",status,"Visited",visited.Count,"Steps",VEdges(edges),"HopCount",hops,"PathLimited",hops>edges.Count,
                "NextPathId",hops>edges.Count?edges[edges.Count-1].Target:null,"Source",VNode(source),"Target",VNode(target));
        }
        public string ViewerResolveObject(string query)
        {
            query=(query??"").Trim();if(query.Length==0)throw new ArgumentException("Enter an object name, ID, or DA for Domain Admins.");
            if(Node(query)!=null)return query;
            bool da=string.Equals(query,"DA",StringComparison.OrdinalIgnoreCase);
            var exact=new List<string>();var matches=new List<string>();var labels=new List<string>();
            string sql=da?"SELECT id,name FROM nodes WHERE kind='groups' AND id LIKE 'S-1-5-21-%-512' ORDER BY id LIMIT 21":
                "SELECT id,name FROM nodes WHERE name=?1 COLLATE NOCASE ORDER BY id LIMIT 21";
            using(var s=new Statement(this,sql)){if(!da)s.Bind(1,query);while(s.Read()){ViewerCheck();exact.Add(s.Text(0));labels.Add((s.Text(1)??s.Text(0))+" ["+s.Text(0)+"]");}}
            if(exact.Count==1)return exact[0];
            if(exact.Count==0&&!da){using(var s=new Statement(this,"SELECT id,name FROM nodes WHERE instr(lower(name),lower(?1))>0 OR instr(lower(id),lower(?1))>0 ORDER BY id LIMIT 21")){s.Bind(1,query);while(s.Read()){ViewerCheck();matches.Add(s.Text(0));labels.Add((s.Text(1)??s.Text(0))+" ["+s.Text(0)+"]");}}if(matches.Count==1)return matches[0];}
            if(labels.Count==0)throw new ArgumentException("No collected object matches '"+query+"'. Search Objects and choose Use as target, or enter its exact ID.");
            throw new ArgumentException("Ambiguous object '"+query+"'. Enter an exact ID from Objects. Matches: "+string.Join("; ",labels.ToArray()));
        }
        // Return the branching reverse-reachable subgraph, retaining alternative edges and
        // cycles. This does not enumerate an infinite number of cyclic path permutations.
        public object ViewerTargetGraph(string target,int maxVisited,int maxEdges)
        {return ViewerTargetGraph(target,maxVisited,maxEdges,false,null);}
        public object ViewerTargetGraph(string target,int maxVisited,int maxEdges,bool shortest,string[] allowedTypes)
        {
            if(maxVisited<1||maxVisited>250000||maxEdges<1||maxEdges>10000)throw new ArgumentOutOfRangeException();
            target=ViewerResolveObject(target);
            var allowed=allowedTypes==null?null:new HashSet<string>(allowedTypes,StringComparer.OrdinalIgnoreCase);
            var depths=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);var queue=new Queue<string>();var edges=new List<object>();
            var emitted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            depths.Add(target,0);queue.Enqueue(target);string status="Complete";bool stopped=false;
            while(queue.Count>0&&!stopped){ViewerCheck();string current=queue.Dequeue();using(var s=Incoming()){s.Bind(1,current);while(s.Read()){
                ViewerCheck();if(allowed!=null&&!allowed.Contains(s.Text(1)))continue;string from=s.Text(0);
                int depth;if(shortest&&depths.TryGetValue(from,out depth)&&depth!=depths[current]+1)continue;
                string edgeType=GraphRelationships.CanonicalType(s.Text(1));string key=from.Length+":"+from+current.Length+":"+current+edgeType;
                if(!emitted.Add(key))continue;
                if(edges.Count>=maxEdges){status="EdgeLimit";stopped=true;break;}
                if(!depths.ContainsKey(from)){if(depths.Count>=maxVisited){status="VisitLimit";stopped=true;break;}depths.Add(from,depths[current]+1);queue.Enqueue(from);}
                edges.Add(VEdge(new GraphEdge{Source=from,Target=current,EdgeType=GraphRelationships.CanonicalType(s.Text(1)),Evidence=s.Text(2)}));
            }}}
            var nodes=new List<object>();foreach(var pair in depths){var n=VNode(pair.Key);n["Depth"]=pair.Value;nodes.Add(n);}
            return ViewerJson.Map("Status",status,"Limited",status!="Complete","Nodes",nodes,"Edges",edges,"Target",VNode(target),"Visited",depths.Count);
        }
    }

    public static class ViewerJson
    {
        public static Dictionary<string,object> Map(params object[] pairs)
        {var result=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)result.Add((string)pairs[i],pairs[i+1]);return result;}
        public static string Encode(object value){var b=new StringBuilder();Write(b,value,0);return b.ToString();}
        private static void Write(StringBuilder b,object value,int depth)
        {
            if(depth>24)throw new IOException("Response nesting limit");
            if(value==null){b.Append("null");return;}
            var text=value as string;if(text!=null)
            {b.Append('"');foreach(char c in text){if(c=='"'||c=='\\')b.Append('\\').Append(c);else if(c<32||c=='<'||c=='>'||c=='&'||c=='\u2028'||c=='\u2029')b.Append("\\u").Append(((int)c).ToString("x4"));else b.Append(c);}b.Append('"');return;}
            if(value is bool){b.Append((bool)value?"true":"false");return;}
            var map=value as IDictionary;if(map!=null){b.Append('{');bool first=true;foreach(DictionaryEntry e in map){if(!first)b.Append(',');first=false;Write(b,Convert.ToString(e.Key,CultureInfo.InvariantCulture),depth+1);b.Append(':');Write(b,e.Value,depth+1);}b.Append('}');return;}
            var list=value as IEnumerable;if(list!=null){b.Append('[');bool first=true;foreach(object item in list){if(!first)b.Append(',');first=false;Write(b,item,depth+1);}b.Append(']');return;}
            if(value is IFormattable){b.Append(((IFormattable)value).ToString(null,CultureInfo.InvariantCulture));return;}
            var fields=new Dictionary<string,object>();foreach(var field in value.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public))fields[field.Name]=field.GetValue(value);Write(b,fields,depth+1);
        }
    }

    public sealed class ViewerServer : IDisposable
    {
        private readonly string path,html,token;
        private readonly TcpListener listener;
        private readonly FileStream fileLock;
        private readonly Semaphore slots=new Semaphore(4,4);
        private Thread acceptThread;
        private volatile bool running;
        public string Url { get; private set; }
        public bool IsRunning { get { return running; } }
        public ViewerServer(string indexPath,string page)
        {
            path=Path.GetFullPath(indexPath);html=page;
            fileLock=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            try
            {
                var header=new byte[20];if(fileLock.Read(header,0,header.Length)!=20||Encoding.ASCII.GetString(header,0,16)!="SQLite format 3\0")throw new IOException("Select a BHTest SQLite graph index.");
                if(header[18]!=1||header[19]!=1||File.Exists(path+"-wal")||File.Exists(path+"-journal"))throw new IOException("Select a completed, standalone graph.sqlite snapshot, without a WAL or recovery journal.");
                using(var db=new GraphIndex(path,false)){db.ViewerBudget();db.Node("__viewer_schema_check__");}
                var secret=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(secret);token=Convert.ToBase64String(secret);
                listener=new TcpListener(IPAddress.Loopback,0);listener.Start(16);int port=((IPEndPoint)listener.LocalEndpoint).Port;
                Url="http://127.0.0.1:"+port+"/#"+token;running=true;acceptThread=new Thread(Accept);acceptThread.IsBackground=true;acceptThread.Start();
            }
            catch{fileLock.Dispose();throw;}
        }
        private void Accept()
        {
            while(running)
            {
                TcpClient client=null;
                try
                {
                    client=listener.AcceptTcpClient();
                    if(!slots.WaitOne(0)){client.Close();continue;}
                    var owned=client;ThreadPool.QueueUserWorkItem(delegate(object unused){try{Handle(owned);}catch{}finally{owned.Close();slots.Release();}});
                }
                catch{if(client!=null)client.Close();if(!running)break;}
            }
        }
        private void Respond(NetworkStream stream,int status,string type,string body)
        {
            byte[] payload=Encoding.UTF8.GetBytes(body);string reason=status==200?"OK":status==403?"Forbidden":status==404?"Not Found":status==400?"Bad Request":"Query Error";
            byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" "+reason+"\r\nContent-Type: "+type+"; charset=utf-8\r\nContent-Length: "+payload.Length+"\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n\r\n");
            stream.Write(header,0,header.Length);stream.Write(payload,0,payload.Length);stream.Flush();
        }
        private bool Authorized(string supplied)
        {string expected="Bearer "+token;int difference=(supplied??"").Length^expected.Length;for(int i=0;i<expected.Length;i++)difference|=expected[i]^(supplied!=null&&i<supplied.Length?supplied[i]:0);return difference==0;}
        private static string Arg(Dictionary<string,string> args,string key)
        {string value;return args.TryGetValue(key,out value)?value:"";}
        private static long Number(Dictionary<string,string> args,string key,long fallback,long min,long max)
        {string value=Arg(args,key);long number;if(value.Length==0)return fallback;if(!long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)||number<min||number>max)throw new ArgumentException("Invalid "+key);return number;}
        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout=3000;client.SendTimeout=10000;using(var stream=client.GetStream())
            {
                var clock=Stopwatch.StartNew();var b=new StringBuilder();int c;
                while(b.Length<16384&&clock.ElapsedMilliseconds<3000&&(c=stream.ReadByte())>=0){if(c>127||c==0)return;b.Append((char)c);if(b.Length>=4&&b.ToString(b.Length-4,4)=="\r\n\r\n")break;}
                if(!b.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)){Respond(stream,400,"text/plain","Invalid request headers");return;}
                var lines=b.ToString().Split(new string[]{"\r\n"},StringSplitOptions.None);var request=lines[0].Split(' ');var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                if(request.Length!=3||request[2]!="HTTP/1.1"||!request[1].StartsWith("/",StringComparison.Ordinal)){Respond(stream,400,"text/plain","Invalid request");return;}
                foreach(string line in lines){if(line==lines[0]||line.Length==0)continue;int colon=line.IndexOf(':');if(colon<1||headers.ContainsKey(line.Substring(0,colon))){Respond(stream,400,"text/plain","Invalid headers");return;}headers.Add(line.Substring(0,colon),line.Substring(colon+1).Trim());}
                string origin="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;
                if(Arg(headers,"Host")!=origin.Substring(7)||(Arg(headers,"Origin").Length>0&&Arg(headers,"Origin")!=origin)||Arg(headers,"Sec-Fetch-Site")=="cross-site")
                {Respond(stream,403,"text/plain","Local origin required");return;}
                if(headers.ContainsKey("Transfer-Encoding")||(headers.ContainsKey("Content-Length")&&Arg(headers,"Content-Length")!="0"))
                {Respond(stream,400,"text/plain","Request bodies are not accepted");return;}
                if(request[0]=="GET"&&request[1]=="/"){Respond(stream,200,"text/html",html);return;}
                if(!Authorized(Arg(headers,"Authorization"))){Respond(stream,403,"text/plain","Viewer session required");return;}
                if(request[0]=="POST"&&request[1]=="/api/stop"){Respond(stream,200,"application/json","{\"Stopped\":true}");Stop();return;}
                if(request[0]!="GET"){Respond(stream,400,"text/plain","Unsupported method");return;}
                try
                {
                    int question=request[1].IndexOf('?');string route=question<0?request[1]:request[1].Substring(0,question);var args=new Dictionary<string,string>();
                    if(question>=0)foreach(string pair in request[1].Substring(question+1).Split('&')){int equals=pair.IndexOf('=');if(equals<0)throw new ArgumentException("Invalid query");string key=Uri.UnescapeDataString(pair.Substring(0,equals).Replace('+',' ')),value=Uri.UnescapeDataString(pair.Substring(equals+1).Replace('+',' '));if(value.Length>2048||args.ContainsKey(key))throw new ArgumentException("Invalid query");args.Add(key,value);}
                    object result;
                    using(var db=new GraphIndex(path,false))
                    {
                        db.ViewerBudget();
                        switch(route)
                        {
                            case "/api/summary":result=db.ViewerSummary();break;
                            case "/api/search":result=db.ViewerSearch(Arg(args,"q"),Arg(args,"after"));break;
                            case "/api/findings":result=db.ViewerFindings((int)Number(args,"score",int.MaxValue,0,int.MaxValue),Number(args,"sequence",0,0,long.MaxValue),Arg(args,"q"));break;
                            case "/api/recorded":result=db.ViewerRecordedPath(Arg(args,"id"));break;
                            case "/api/object":result=db.ViewerObject(Arg(args,"id"));break;
                            case "/api/relationships":if(Arg(args,"direction")!="in"&&Arg(args,"direction")!="out")throw new ArgumentException("Select incoming or outgoing");result=db.ViewerRelationships(Arg(args,"id"),Arg(args,"direction"),Arg(args,"target"),Arg(args,"bucket"),Arg(args,"source"),Arg(args,"type"));break;
                            case "/api/path":if(Arg(args,"source").Length==0||Arg(args,"target").Length==0)throw new ArgumentException("Source and target IDs required");result=db.ViewerShortestPath(Arg(args,"source"),Arg(args,"target"),(int)Number(args,"limit",50000,1,250000));break;
                            default:Respond(stream,404,"text/plain","Unknown route");return;
                        }
                    }
                    Respond(stream,200,"application/json",ViewerJson.Encode(result));
                }
                catch(ArgumentException e){Respond(stream,400,"application/json",ViewerJson.Encode(ViewerJson.Map("Error",e.Message)));}
                catch(Exception e){Respond(stream,422,"application/json",ViewerJson.Encode(ViewerJson.Map("Error",e.Message.IndexOf("SQLite error 9",StringComparison.Ordinal)>=0?"Query time limit reached. Narrow the query and retry.":e.Message)));}
            }
        }
        public void Stop(){running=false;listener.Stop();}
        public void Dispose(){Stop();if(acceptThread!=null)acceptThread.Join(4000);for(int i=0;i<4;i++)slots.WaitOne();fileLock.Dispose();slots.Release(4);}
    }
}
