using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;

namespace BH.Native
{
    public sealed class GpoSecuritySummary
    {
        public bool Known;
        public string Reason;
        public string[] Readers=new string[0], Appliers=new string[0];
    }
    public static class GpoSecurity
    {
        private static readonly Guid Apply=new Guid("edacfd8f-ffb3-11d1-b41d-00a0c968f939");
        private static readonly Guid GpoClass=new Guid("f30e3bc2-9ff0-11d1-b603-0000f80367c1");
        public static GpoSecuritySummary Analyze(byte[] bytes)
        {
            var result=new GpoSecuritySummary{Reason="Missing or unsupported GPO security descriptor"};
            if(bytes==null)return result;
            try
            {
                var sd=new RawSecurityDescriptor(bytes,0);
                if(sd.DiscretionaryAcl==null||(sd.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0)return result;
                var read=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase);var apply=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(GenericAce ace in sd.DiscretionaryAcl)
                {
                    if((ace.AceFlags&AceFlags.InheritOnly)!=0)continue;
                    var qualified=ace as QualifiedAce;
                    if(qualified==null||qualified.IsCallback||(qualified.AceQualifier!=AceQualifier.AccessAllowed&&qualified.AceQualifier!=AceQualifier.AccessDenied))return result;
                    var objectAce=ace as ObjectAce;bool typed=false;Guid objectType=Guid.Empty;
                    if(objectAce!=null)
                    {
                        if((objectAce.ObjectAceFlags&~(ObjectAceFlags.ObjectAceTypePresent|ObjectAceFlags.InheritedObjectAceTypePresent))!=0)return result;
                        if((objectAce.ObjectAceFlags&ObjectAceFlags.InheritedObjectAceTypePresent)!=0&&objectAce.InheritedObjectAceType!=GpoClass)return result;
                        typed=(objectAce.ObjectAceFlags&ObjectAceFlags.ObjectAceTypePresent)!=0;
                        objectType=objectAce.ObjectAceType;
                    }
                    uint mask=unchecked((uint)qualified.AccessMask);
                    if((mask&0x10000000)!=0)mask|=0x000f01ff;
                    if((mask&0x80000000)!=0)mask|=0x00020094;
                    bool canApply=(mask&0x100)!=0&&(!typed||objectType==Guid.Empty||objectType==Apply);
                    uint readMask=!typed||objectType==Guid.Empty?mask&0x20094u:0;
                    // A relevant deny anywhere makes this conservative summary unusable.
                    // Do not pretend same-trustee subtraction is an effective token check.
                    if(qualified.AceQualifier==AceQualifier.AccessDenied&&((mask&0x20094)!=0||canApply))
                    {result.Reason="Relevant deny requires full effective-token evaluation";return result;}
                    if(qualified.AceQualifier!=AceQualifier.AccessAllowed)continue;
                    string sid=qualified.SecurityIdentifier.Value;
                    uint previous;read.TryGetValue(sid,out previous);read[sid]=previous|readMask;
                    if(canApply)apply.Add(sid);
                }
                var readers=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var pair in read)if((pair.Value&0x20094)==0x20094)readers.Add(pair.Key);
                result.Readers=new string[readers.Count];readers.CopyTo(result.Readers);result.Appliers=new string[apply.Count];apply.CopyTo(result.Appliers);
                result.Known=true;result.Reason="Simple allow-only read/apply summary; effective access unverified";
            }
            catch(ArgumentException){return result;}
            catch(InvalidDataException){return result;}
            return result;
        }
    }

    public sealed partial class GraphIndex
    {
        public string GpoScopeStatus="Not evaluated";
        private readonly JsonRecordWriter gpoSignature=new JsonRecordWriter();
        private static readonly string[] EmptyGpoSids=new string[0];
        private void InitializeGpoInputs()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS derivation_meta(key TEXT PRIMARY KEY,value TEXT) WITHOUT ROWID;
                DROP TABLE IF EXISTS temp.gpo_meta;DROP TABLE IF EXISTS temp.gpo_grants;DROP TABLE IF EXISTS temp.gpo_links;
                CREATE TEMP TABLE gpo_meta(id TEXT COLLATE NOCASE PRIMARY KEY,kind TEXT,dn TEXT COLLATE NOCASE,parent_dn TEXT COLLATE NOCASE,
                    signature TEXT,conflict INTEGER,known INTEGER,blocked INTEGER,enabled INTEGER,security_known INTEGER,wmi TEXT,som TEXT) WITHOUT ROWID;
                CREATE TEMP TABLE gpo_grants(gpo TEXT COLLATE NOCASE,trustee TEXT COLLATE NOCASE,permission INTEGER,PRIMARY KEY(gpo,trustee,permission)) WITHOUT ROWID;
                CREATE TEMP TABLE gpo_links(gpo TEXT COLLATE NOCASE,root TEXT COLLATE NOCASE,signature TEXT,conflict INTEGER,known INTEGER,enforced INTEGER,
                    PRIMARY KEY(gpo,root)) WITHOUT ROWID;");
            Execute("CREATE INDEX gpo_meta_conflicts ON gpo_meta(id) WHERE conflict<>0;CREATE INDEX gpo_link_conflicts ON gpo_links(gpo,root) WHERE conflict<>0;");
        }
        private void PersistGpoConflicts()
        {
            long objects=Scalar("SELECT count(*) FROM gpo_meta WHERE conflict<>0"),links=Scalar("SELECT count(*) FROM gpo_links WHERE conflict<>0");
            if(objects>0)
            using(var rows=new Statement(this,"SELECT n.id,n.record FROM gpo_meta g JOIN nodes n ON n.id=g.id WHERE g.conflict<>0 AND n.record IS NOT NULL"))
            using(var update=new Statement(this,"UPDATE nodes SET record=?1 WHERE id=?2"))
            {while(rows.Read()){update.BindRecord(1,GpoConflictRecord(rows.Record(1)));update.Bind(2,rows.Text(0));update.Read();update.Reset();}}
            if(links>0)
            using(var rows=new Statement(this,"SELECT e.target,e.bucket,e.source,e.type,e.evidence FROM gpo_links l JOIN edges e ON e.source=l.gpo AND e.target=l.root WHERE l.conflict<>0 AND e.type='GPLink' AND e.evidence IS NOT NULL"))
            using(var update=new Statement(this,"UPDATE edges SET evidence=?1 WHERE target=?2 AND bucket=?3 AND source=?4 AND type=?5"))
            {while(rows.Read()){update.Bind(1,GpoConflictRecord(rows.Text(4),"GpoLinkEvidenceConflict"));for(int i=0;i<4;i++)update.Bind(i+2,rows.Text(i));update.Read();update.Reset();}}
            GpoScopeStatus+="; conflicting GPO-scope objects: "+objects+", links: "+links;
        }
        private void StoreGpoScopeStatus()
        {using(var write=new Statement(this,"INSERT OR REPLACE INTO derivation_meta VALUES('gpo_scope',?1)")){write.Bind(1,GpoScopeStatus);write.Read();}}
        public string GpoScopeNote()
        {
            if(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='derivation_meta'")==0)return "Legacy index: no GPO-scope evaluation metadata";
            using(var read=new Statement(this,"SELECT value FROM derivation_meta WHERE key='gpo_scope'")){return read.Read()?read.Text(0):"GPO scope has not been evaluated";}
        }
        private void RegisterGpoNode(string line,string id,string kind,Statement insert,Statement conflict,Statement grant)
        {
            if(kind!="gpos"&&kind!="ous"&&kind!="domains"&&kind!="computers")return;
            string dn=JsonRecordReader.ExtractField(line,"Dn");
            string version=JsonRecordReader.ExtractField(line,"GpoMetadataVersion");
            string flags=kind=="gpos"?JsonRecordReader.ExtractField(line,"GpoFlags"):null,functionality=kind=="gpos"?JsonRecordReader.ExtractField(line,"GpoFunctionalityVersion"):null;
            string options=kind=="ous"||kind=="domains"?JsonRecordReader.ExtractField(line,"GpOptions"):null,wmi=kind=="gpos"?JsonRecordReader.ExtractField(line,"GpoWmiFilter"):null;
            string som=kind=="ous"||kind=="domains"?JsonRecordReader.ExtractField(line,"GpoSomType"):null;
            string security=kind=="gpos"?JsonRecordReader.ExtractField(line,"GpoSecurityKnown"):null,disabled=kind=="computers"?JsonRecordReader.ExtractField(line,"Disabled"):null;
            string[] readers=EmptyGpoSids,appliers=EmptyGpoSids;
            if(kind=="gpos")
            {
                readers=new List<string>(JsonRecordReader.StringArray(line,"GpoReaders")).ToArray();appliers=new List<string>(JsonRecordReader.StringArray(line,"GpoAppliers")).ToArray();
                Array.Sort(readers,StringComparer.OrdinalIgnoreCase);Array.Sort(appliers,StringComparer.OrdinalIgnoreCase);
            }
            var signature=gpoSignature;signature.Begin().Prop("Kind",kind).PropS("Dn",dn==null?null:dn.ToUpperInvariant()).PropS("Version",version)
                .PropS("Flags",flags).PropS("Functionality",functionality).PropS("Options",options).PropS("Wmi",wmi).PropS("Som",som)
                .PropS("Security",security).PropS("Disabled",disabled).PropArray("Readers",readers).PropArray("Appliers",appliers);
            string proof=signature.End();int parsedOptions,parsedFlags;
            bool known=version=="1"&&dn!=null&&(kind=="gpos"?functionality=="2"&&int.TryParse(flags,out parsedFlags)&&parsedFlags>=0&&parsedFlags<=3:
                kind=="computers"?disabled=="false"&&id.StartsWith("S-1-5-21-",StringComparison.OrdinalIgnoreCase):int.TryParse(options,out parsedOptions)&&(parsedOptions==0||parsedOptions==1)&&(som=="OU"||som=="Domain"||som=="Container"));
            bool blocked=som=="OU"&&options=="1",enabled=kind!="gpos"||(flags=="0"||flags=="1");
            insert.Bind(1,id);insert.Bind(2,kind);insert.Bind(3,dn);insert.Bind(4,DirectoryIdentity.ParentDn(dn));insert.Bind(5,proof);
            insert.Bind(6,JsonRecordReader.ExtractField(line,"GpoEvidenceConflict")!=null?"1":"0");insert.Bind(7,known?"1":"0");
            insert.Bind(8,blocked?"1":"0");insert.Bind(9,enabled?"1":"0");insert.Bind(10,security=="true"?"1":"0");insert.Bind(11,wmi);insert.Bind(12,som);insert.Read();insert.Reset();
            conflict.Bind(1,id);conflict.Bind(2,proof);conflict.Read();conflict.Reset();
            if(kind=="gpos")for(int permission=1;permission<=2;permission++)foreach(string trustee in permission==1?readers:appliers)
            {grant.Bind(1,id);grant.Bind(2,trustee);grant.Bind(3,permission.ToString(CultureInfo.InvariantCulture));grant.Read();grant.Reset();}
        }
        private void RegisterGpoLink(string line,string source,string target,bool sourceDn,bool targetDn,Statement insert,Statement conflict)
        {
            string options=JsonRecordReader.ExtractField(line,"LinkOptions");int value;
            bool known=JsonRecordReader.ExtractField(line,"GpoLinkEvidenceConflict")==null&&JsonRecordReader.ExtractField(line,"GpoLinkMetadataVersion")=="1"&&int.TryParse(options,out value)&&(value==0||value==2);
            string signature=(known?"known:":"unknown:")+options;
            insert.Bind(1,source);insert.Bind(2,target);insert.Bind(3,sourceDn?source.ToUpperInvariant():null);insert.Bind(4,targetDn?target.ToUpperInvariant():null);
            insert.Bind(5,signature);insert.Bind(6,known?"1":"0");insert.Bind(7,options=="2"?"1":"0");insert.Read();insert.Reset();
            conflict.Bind(1,source);conflict.Bind(2,target);conflict.Bind(3,sourceDn?source.ToUpperInvariant():null);conflict.Bind(4,targetDn?target.ToUpperInvariant():null);conflict.Bind(5,signature);conflict.Read();conflict.Reset();
        }
        private void DeriveGpoScope()
        {
            GpoScopeStatus="No complete native GPO metadata";
            if(Scalar("SELECT count(*) FROM gpo_meta WHERE kind='gpos' AND known=1 AND conflict=0")==0)return;
            Execute(@"DROP TABLE IF EXISTS temp.gpo_parent;DROP TABLE IF EXISTS temp.gpo_scope;DROP TABLE IF EXISTS temp.gpo_tokens;
                CREATE TEMP TABLE gpo_parent(parent TEXT COLLATE NOCASE,child TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;
                CREATE INDEX gpo_parent_parent ON gpo_parent(parent,child);
                INSERT INTO gpo_parent SELECT e.source,e.target FROM edges e JOIN gpo_meta c ON c.id=e.target JOIN gpo_meta p ON p.id=e.source
                WHERE e.bucket='contains' AND e.type='Contains' AND e.source_dn=0 AND e.target_dn=0 AND c.conflict=0 AND p.conflict=0
                    AND p.kind IN ('domains','ous') AND c.kind IN ('ous','computers') AND c.parent_dn=p.dn
                    AND (SELECT count(*) FROM edges x WHERE x.target=e.target AND x.bucket='contains' AND x.type='Contains')=1;
                CREATE TEMP TABLE gpo_scope(gpo TEXT COLLATE NOCASE,root TEXT COLLATE NOCASE,node TEXT COLLATE NOCASE,parent TEXT COLLATE NOCASE,
                    depth INTEGER,enforced INTEGER,PRIMARY KEY(gpo,root,node)) WITHOUT ROWID;
                CREATE INDEX gpo_scope_depth ON gpo_scope(depth);
                INSERT INTO gpo_scope SELECT l.gpo,l.root,l.root,NULL,0,l.enforced FROM gpo_links l JOIN gpo_meta g ON g.id=l.gpo JOIN gpo_meta r ON r.id=l.root
                WHERE l.known=1 AND l.conflict=0 AND g.kind='gpos' AND g.known=1 AND g.conflict=0 AND g.enabled=1 AND g.security_known=1
                    AND (g.wmi IS NULL OR g.wmi='') AND r.kind IN ('domains','ous') AND r.som IN ('Domain','OU') AND r.known=1 AND r.conflict=0 LIMIT 1000000;");
            for(int depth=0;Scalar("SELECT count(*) FROM gpo_scope WHERE depth="+depth)>0;depth++)
            {
                long count=Scalar("SELECT count(*) FROM gpo_scope");
                if((depth&7)==0)Report("GPO scope: containment depth "+depth,count,1000000,0);
                if(depth>=128||count>=1000000){GpoScopeStatus="Incomplete scope: conservative depth/pair limit; no GPO capabilities emitted";return;}
                Execute(@"INSERT OR IGNORE INTO gpo_scope SELECT s.gpo,s.root,p.child,s.node,s.depth+1,s.enforced FROM gpo_scope s
                    JOIN gpo_parent p ON p.parent=s.node JOIN gpo_meta c ON c.id=p.child
                    WHERE s.depth="+depth+@" AND c.conflict=0 AND c.known=1 AND (c.kind='computers' OR s.enforced=1 OR c.blocked=0)
                    LIMIT "+(1000000-count));
            }
            if(Scalar("SELECT count(DISTINCT s.node) FROM gpo_scope s JOIN gpo_meta c ON c.id=s.node WHERE c.kind='computers'")>333333)
            {GpoScopeStatus="Incomplete token closure: computer/token-pair budget; no GPO capabilities emitted";return;}
            Execute(@"DROP TABLE IF EXISTS temp.gpo_members;
                CREATE TEMP TABLE gpo_members(source TEXT COLLATE NOCASE,target TEXT COLLATE NOCASE,PRIMARY KEY(source,target)) WITHOUT ROWID;
                INSERT OR IGNORE INTO gpo_members SELECT m.source,m.target FROM edges m JOIN nodes g ON g.id=m.target
                    WHERE m.bucket='memberof' AND m.type='MemberOf' AND m.source_dn=0 AND m.target_dn=0 AND g.kind='groups' AND g.security_enabled='true';
                CREATE TEMP TABLE gpo_tokens(computer TEXT COLLATE NOCASE,sid TEXT COLLATE NOCASE,previous TEXT COLLATE NOCASE,depth INTEGER,
                    PRIMARY KEY(computer,sid)) WITHOUT ROWID;CREATE INDEX gpo_token_depth ON gpo_tokens(depth);
                INSERT OR IGNORE INTO gpo_tokens SELECT s.node,s.node,NULL,0 FROM gpo_scope s JOIN gpo_meta c ON c.id=s.node WHERE c.kind='computers';
                INSERT OR IGNORE INTO gpo_tokens SELECT computer,'S-1-1-0',computer,1 FROM gpo_tokens WHERE depth=0;
                INSERT OR IGNORE INTO gpo_tokens SELECT computer,'S-1-5-11',computer,1 FROM gpo_tokens WHERE depth=0;");
            for(int depth=0;Scalar("SELECT count(*) FROM gpo_tokens WHERE depth="+depth)>0;depth++)
            {
                long count=Scalar("SELECT count(*) FROM gpo_tokens");
                if((depth&7)==0)Report("GPO scope: membership depth "+depth,count,1000000,0);
                if(depth>=128||count>=1000000){GpoScopeStatus="Incomplete token closure: conservative depth/pair limit; no GPO capabilities emitted";return;}
                Execute(@"INSERT OR IGNORE INTO gpo_tokens SELECT t.computer,m.target,t.sid,t.depth+1 FROM gpo_tokens t JOIN gpo_members m ON m.source=t.sid
                    WHERE t.depth="+depth+@" LIMIT "+(1000000-count));
            }
            var json=new JsonRecordWriter();long emitted=0;
            using(var candidates=new Statement(this,@"SELECT s.gpo,s.root,s.node,s.enforced,s.depth,
                (SELECT t.sid FROM gpo_tokens t JOIN gpo_grants a ON a.trustee=t.sid AND a.gpo=s.gpo AND a.permission=1 WHERE t.computer=s.node ORDER BY t.sid LIMIT 1),
                (SELECT t.sid FROM gpo_tokens t JOIN gpo_grants a ON a.trustee=t.sid AND a.gpo=s.gpo AND a.permission=2 WHERE t.computer=s.node ORDER BY t.sid LIMIT 1)
                FROM gpo_scope s JOIN gpo_meta c ON c.id=s.node WHERE c.kind='computers' ORDER BY s.gpo,s.node,s.root"))
            using(var write=new Statement(this,"INSERT OR IGNORE INTO edges VALUES(?1,'access',?2,'GPOAppliesTo',0,0,?3)"))
            {
                while(candidates.Read())
                {
                    string reader=candidates.Text(5),applier=candidates.Text(6);if(reader==null||applier==null)continue;
                    string gpo=candidates.Text(0),computer=candidates.Text(2);
                    json.Begin().Prop("Source",gpo).Prop("Target",computer).Prop("EdgeType","GPOAppliesTo").Prop("Derivation","GpoComputerScopeV1")
                        .Prop("EvidenceSource","Collected LDAP GPO/container metadata, DACL read/apply grants and security-group membership")
                        .Prop("LinkRoot",candidates.Text(1)).Prop("Enforced",candidates.Text(3)=="1").Prop("ContainmentDepth",int.Parse(candidates.Text(4)))
                        .PropArray("ContainmentChain",GpoContainmentPath(gpo,candidates.Text(1),computer))
                        .PropArray("ReadMembership",GpoTokenPath(computer,reader)).PropArray("ApplyMembership",GpoTokenPath(computer,applier))
                        .Prop("TokenModel","Computer SID, known security-enabled group memberships, Everyone and Authenticated Users")
                        .Prop("ComputerPolicy",true).Prop("WmiFilterPresent",false).Prop("PolicyContentsCollected",false)
                        .Prop("AttributeVisibilityVerified",false)
                        .Prop("EffectiveAccessVerified",false).Prop("SysvolAccessVerified",false).Prop("ResultantPolicyVerified",false);
                    write.Bind(1,computer);write.Bind(2,gpo);write.Bind(3,json.End());write.Read();write.Reset();emitted++;
                }
            }
            emitted=Scalar("SELECT count(*) FROM edges WHERE bucket='access' AND type='GPOAppliesTo'");
            GpoScopeStatus="Derived computer-policy scope candidates: "+emitted+"; SYSVOL, endpoint processing and resultant policy unverified";
        }
        private string[] GpoTokenPath(string computer,string sid)
        {
            var path=new List<string>();
            using(var next=new Statement(this,"SELECT previous FROM gpo_tokens WHERE computer=?1 AND sid=?2"))
            {for(int i=0;sid!=null&&i<130;i++){path.Add(sid);next.Bind(1,computer);next.Bind(2,sid);sid=next.Read()?next.Text(0):null;next.Reset();}}
            path.Reverse();return path.ToArray();
        }
        private string[] GpoContainmentPath(string gpo,string root,string node)
        {
            var path=new List<string>();
            using(var next=new Statement(this,"SELECT parent FROM gpo_scope WHERE gpo=?1 AND root=?2 AND node=?3"))
            {for(int i=0;node!=null&&i<130;i++){path.Add(node);next.Bind(1,gpo);next.Bind(2,root);next.Bind(3,node);node=next.Read()?next.Text(0):null;next.Reset();}}
            path.Reverse();return path.ToArray();
        }
        private static string GpoConflictRecord(string record){return GpoConflictRecord(record,"GpoEvidenceConflict");}
        private static string GpoConflictRecord(string record,string field)
        {
            if(record==null)return null;record=record.TrimEnd();
            if(JsonRecordReader.ExtractField(record,field)!=null)return record;
            return record.Substring(0,record.Length-1)+",\""+field+"\":true}";
        }
    }
}
