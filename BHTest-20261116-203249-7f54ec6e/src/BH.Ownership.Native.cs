using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.AccessControl;

namespace BH.Native
{
    public sealed class OwnershipDescriptor
    {
        public bool Known, OwnerRightsPresent, OwnerRightsWriteDacl, WriteDaclDeny;
        public string OwnerSid, Reason="Missing or unsupported ownership descriptor";
        public static OwnershipDescriptor Analyze(byte[] bytes)
        {
            var result=new OwnershipDescriptor();if(bytes==null)return result;
            try
            {
                var sd=new RawSecurityDescriptor(bytes,0);result.OwnerSid=sd.Owner==null?null:sd.Owner.Value;
                if(sd.DiscretionaryAcl==null||(sd.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0)return result;
                bool unsupported=false;
                foreach(GenericAce ace in sd.DiscretionaryAcl)
                {
                    if((ace.AceFlags&AceFlags.InheritOnly)!=0)continue;
                    var rule=ace as QualifiedAce;
                    if(rule==null){unsupported=true;continue;}
                    bool owner=rule.SecurityIdentifier.Value=="S-1-3-4";
                    if(owner)result.OwnerRightsPresent=true;
                    uint mask=unchecked((uint)rule.AccessMask);if((mask&0x10000000)!=0)mask|=0xf01ff;
                    if(rule.IsCallback){if(owner||(mask&0x40000)!=0)unsupported=true;continue;}
                    if(rule.AceQualifier!=AceQualifier.AccessAllowed&&rule.AceQualifier!=AceQualifier.AccessDenied){unsupported=true;continue;}
                    var objectAce=ace as ObjectAce;
                    if(objectAce!=null&&objectAce.ObjectAceFlags!=ObjectAceFlags.None&&(mask&0x40000)!=0){unsupported=true;continue;}
                    if((mask&0x40000)==0)continue;
                    if(rule.AceQualifier==AceQualifier.AccessDenied)result.WriteDaclDeny=true;
                    if(owner&&rule.AceQualifier==AceQualifier.AccessAllowed)result.OwnerRightsWriteDacl=true;
                }
                result.Known=!unsupported;result.Reason=unsupported?"Unsupported or conditional ACE: ownership control requires review":
                    result.WriteDaclDeny?"Relevant WRITE_DAC deny: full token evaluation required":
                    result.OwnerRightsPresent?"OWNER RIGHTS replaces implicit owner permissions":"No applicable OWNER RIGHTS ACE observed";
            }
            catch(ArgumentException){return result;}
            return result;
        }
        public static string PolicyMode(string value)
        {
            char mode=string.IsNullOrEmpty(value)||value.Length<29?'0':value[28];
            switch(mode){case '0':return "Default";case '1':return "Block";case '2':return "Allow";case '3':return "Restricted";default:return "Block";}
        }
    }
    public sealed partial class GraphIndex
    {
        private readonly JsonRecordWriter ownerSignature=new JsonRecordWriter();
        public string OwnershipScopeStatus="Not evaluated";
        private void InitializeOwnershipInputs()
        {
            Execute(@"DROP TABLE IF EXISTS temp.owner_meta;DROP TABLE IF EXISTS temp.owner_admins;
                CREATE TEMP TABLE owner_meta(id TEXT COLLATE NOCASE PRIMARY KEY,version TEXT,known INTEGER,owner_rights INTEGER,owner_wd INTEGER,
                    deny_wd INTEGER,computer INTEGER,policy TEXT,signature TEXT,conflict INTEGER) WITHOUT ROWID;
                CREATE INDEX owner_conflicts ON owner_meta(id) WHERE conflict<>0;");
        }
        private void RegisterOwnershipNode(string line,string id,string kind,Statement insert,Statement conflict)
        {
            string version=JsonRecordReader.ExtractField(line,"OwnershipMetadataVersion");
            string known=JsonRecordReader.ExtractField(line,"OwnershipAnalysisKnown"),present=JsonRecordReader.ExtractField(line,"OwnerRightsPresent"),wd=JsonRecordReader.ExtractField(line,"OwnerRightsWriteDacl"),deny=JsonRecordReader.ExtractField(line,"OwnershipWriteDaclDeny");
            string computer=JsonRecordReader.ExtractField(line,"OwnershipComputerDerived"),policy=JsonRecordReader.ExtractField(line,"OwnerPolicyMode")??"Unknown";
            if(computer==null)
            {if(kind=="computers")computer="true";else foreach(string cls in JsonRecordReader.StringArray(line,"ObjectClasses"))if(cls.Equals("computer",StringComparison.OrdinalIgnoreCase)||cls.Equals("msDS-ManagedServiceAccount",StringComparison.OrdinalIgnoreCase)||cls.Equals("msDS-GroupManagedServiceAccount",StringComparison.OrdinalIgnoreCase))computer="true";}
            string domainPolicy=JsonRecordReader.ExtractField(line,"OwnerDomainPolicyMode");if(domainPolicy!=null)policy=domainPolicy;
            string signature=ownerSignature.Begin().PropS("Version",version).PropS("Known",known).PropS("Present",present).PropS("Wd",wd).PropS("Deny",deny)
                .PropS("Computer",computer).PropS("Policy",policy).PropS("Owner",JsonRecordReader.ExtractField(line,"OwnerSid"))
                .PropS("SecurityEnabled",JsonRecordReader.ExtractField(line,"SecurityEnabled")).End();
            insert.Bind(1,id);insert.Bind(2,version);insert.Bind(3,known=="true"?"1":"0");insert.Bind(4,present=="true"?"1":"0");insert.Bind(5,wd=="true"?"1":"0");
            insert.Bind(6,deny=="true"?"1":"0");insert.Bind(7,computer=="true"?"1":"0");insert.Bind(8,policy);insert.Bind(9,signature);
            insert.Bind(10,JsonRecordReader.ExtractField(line,"OwnershipEvidenceConflict")!=null?"1":"0");insert.Read();insert.Reset();
            conflict.Bind(1,id);conflict.Bind(2,signature);conflict.Read();conflict.Reset();
        }
        private void DeriveOwnershipControl()
        {
            // Only targets with new metadata are recomputed. Legacy canonical edges
            // stay recorded candidates with an explicit conditional annotation.
            Execute(@"DELETE FROM edges WHERE type IN ('Owns','WriteOwner') AND bucket IN ('acl','access')
                AND target IN (SELECT id FROM owner_meta WHERE version='1' OR conflict<>0);
                CREATE TEMP TABLE owner_admins(domain TEXT COLLATE NOCASE,principal TEXT COLLATE NOCASE,previous TEXT COLLATE NOCASE,depth INTEGER,
                    PRIMARY KEY(domain,principal)) WITHOUT ROWID;
                CREATE INDEX owner_admin_depth ON owner_admins(depth);
                INSERT OR IGNORE INTO owner_admins SELECT substr(n.id,1,length(n.id)-4),n.id,NULL,0 FROM nodes n JOIN owner_meta m ON m.id=n.id
                    WHERE n.kind='groups' AND n.security_enabled='true' AND m.conflict=0 AND (n.id LIKE 'S-1-5-21-%-512' OR n.id LIKE 'S-1-5-21-%-519');
                DROP TABLE IF EXISTS temp.owner_members;
                CREATE TEMP TABLE owner_members(source TEXT COLLATE NOCASE,target TEXT COLLATE NOCASE,PRIMARY KEY(target,source)) WITHOUT ROWID;
                INSERT OR IGNORE INTO owner_members SELECT m.source,m.target FROM edges m JOIN nodes g ON g.id=m.target JOIN nodes s ON s.id=m.source
                    JOIN owner_meta gm ON gm.id=g.id JOIN owner_meta sm ON sm.id=s.id
                    WHERE m.bucket='memberof' AND m.type='MemberOf' AND m.source_dn=0 AND m.target_dn=0 AND g.kind='groups' AND g.security_enabled='true'
                        AND gm.conflict=0 AND sm.conflict=0 AND (s.kind IN ('users','computers') OR (s.kind='groups' AND s.security_enabled='true'));");
            bool adminComplete=true;
            for(int depth=0;Scalar("SELECT count(*) FROM owner_admins WHERE depth="+depth)>0;depth++)
            {
                long count=Scalar("SELECT count(*) FROM owner_admins");
                if(depth>=128||count>=1000000){adminComplete=false;break;}
                Execute(@"INSERT OR IGNORE INTO owner_admins SELECT a.domain,m.source,a.principal,a.depth+1 FROM owner_admins a
                    JOIN owner_members m ON m.target=a.principal WHERE a.depth="+depth+" LIMIT "+(1000000-count));
            }
            long supported=0,conditional=0,suppressed=0;
            var json=new JsonRecordWriter();
            long legacy=0;
            Execute("DROP TABLE IF EXISTS temp.owner_legacy_suppressed;CREATE TEMP TABLE owner_legacy_suppressed(target TEXT COLLATE NOCASE,bucket TEXT,source TEXT COLLATE NOCASE,type TEXT,PRIMARY KEY(target,bucket,source,type)) WITHOUT ROWID;");
            using(var rows=new Statement(this,@"SELECT e.target,e.bucket,e.source,e.type,e.evidence,m.computer FROM edges e JOIN owner_meta m ON m.id=e.target
                WHERE (m.version IS NULL OR m.version<>'1') AND m.conflict=0 AND e.bucket IN ('acl','access') AND e.type IN ('Owns','WriteOwner')"))
            using(var update=new Statement(this,"UPDATE edges SET evidence=?1 WHERE target=?2 AND bucket=?3 AND source=?4 AND type=?5"))
            using(var policyLookup=new Statement(this,"SELECT policy,conflict FROM owner_meta WHERE id=?1"))
            using(var stop=new Statement(this,"INSERT OR IGNORE INTO owner_legacy_suppressed VALUES(?1,?2,?3,?4)"))
            {while(rows.Read()){
                string original=rows.Text(4),mode="Unknown";bool block=false;
                if(rows.Text(5)=="1"&&rows.Text(0).StartsWith("S-1-5-21-",StringComparison.OrdinalIgnoreCase))
                {string id=rows.Text(0);policyLookup.Bind(1,id.Substring(0,id.LastIndexOf('-')));if(policyLookup.Read()){mode=policyLookup.Text(0);block=mode=="Block"||mode=="Restricted"||policyLookup.Text(1)=="1";}policyLookup.Reset();}
                if(block){suppressed++;for(int i=0;i<4;i++)stop.Bind(i+1,rows.Text(i));stop.Read();stop.Reset();}else legacy++;
                if(original!=null&&JsonRecordReader.ExtractField(original,"Derivation")=="OwnershipLegacyV1")
                {if(!block)continue;original=JsonRecordReader.ExtractField(original,"OriginalEvidence");}
                json.Begin().Prop("Source",rows.Text(2)).Prop("Target",rows.Text(0)).Prop("EdgeType",rows.Text(3)).Prop("Derivation","OwnershipLegacyV1")
                    .Prop("Assessment",block?"Suppressed projection":"Conditional legacy candidate").Prop("OwnerPolicyMode",mode)
                    .Prop("Reason",block?"Captured blocking/restricted or conflicting policy and missing target ownership context prevent automatic projection":"This collection lacks the new ownership context; OWNER RIGHTS and server policy were not verified")
                    .Prop("EffectiveAccessVerified",false).PropS("OriginalEvidence",original);
                update.Bind(1,json.End());for(int i=0;i<4;i++)update.Bind(i+2,rows.Text(i));update.Read();update.Reset();
            }}
            Execute(@"INSERT OR REPLACE INTO edges SELECT e.target,'observed',e.source,e.type,e.target_dn,e.source_dn,e.evidence FROM edges e
                JOIN owner_legacy_suppressed s ON s.target=e.target AND s.bucket=e.bucket AND s.source=e.source AND s.type=e.type;
                DELETE FROM edges WHERE (target,bucket,source,type) IN (SELECT target,bucket,source,type FROM owner_legacy_suppressed);");
            using(var raw=new Statement(this,@"SELECT e.target,e.source,e.type,m.known,m.owner_rights,m.owner_wd,m.deny_wd,m.computer,m.policy,m.conflict
                FROM edges e JOIN owner_meta m ON m.id=e.target WHERE e.bucket='observed' AND e.type IN ('OwnsRaw','WriteOwnerRaw') AND m.version='1'
                    AND e.source_dn=0 AND e.target_dn=0 ORDER BY e.target,e.source,e.type"))
            using(var admin=new Statement(this,"SELECT 1 FROM owner_admins WHERE domain=?1 AND principal=?2"))
            using(var domainPolicy=new Statement(this,"SELECT policy,conflict FROM owner_meta WHERE id=?1"))
            using(var write=new Statement(this,"INSERT OR REPLACE INTO edges VALUES(?1,?2,?3,?4,0,0,?5)"))
            {
                while(raw.Read())
                {
                    string target=raw.Text(0),source=raw.Text(1),type=raw.Text(2)=="OwnsRaw"?"Owns":"WriteOwner",policy=raw.Text(8);
                    bool computer=raw.Text(7)=="1",present=raw.Text(4)=="1",explicitWd=raw.Text(5)=="1";
                    string[] adminWitness=new string[0];
                    bool policyConflict=false;string policySource="Target snapshot";
                    if(computer&&target.StartsWith("S-1-5-21-",StringComparison.OrdinalIgnoreCase))
                    {
                        int last=target.LastIndexOf('-');domainPolicy.Bind(1,target.Substring(0,last));
                        if(domainPolicy.Read())
                        {string captured=domainPolicy.Text(0);policyConflict=domainPolicy.Text(1)=="1"||(policy!="Unknown"&&captured!="Unknown"&&captured!=policy);if(policy=="Unknown"){policy=captured;policySource="Captured target-domain policy";}}domainPolicy.Reset();
                    }
                    string assessment="Supported candidate",reason="Implicit owner WRITE_DAC is modeled for this non-computer object";bool usable=true;
                    if(raw.Text(9)=="1"){usable=false;reason="Conflicting ownership metadata across input snapshots";}
                    else if(computer&&policyConflict&&!explicitWd){usable=false;reason="Conflicting captured domain ownership policy";}
                    else if(raw.Text(3)!="1"){usable=false;reason="Ownership descriptor context is incomplete or unsupported";}
                    else if(raw.Text(6)=="1"){usable=false;reason="Relevant WRITE_DAC deny requires full effective-token review";}
                    else if(present&&!explicitWd){usable=false;reason="OWNER RIGHTS is present without a supported explicit WRITE_DAC grant";}
                    else if(explicitWd){reason="Applicable OWNER RIGHTS explicitly grants WRITE_DAC to the owner role";}
                    else if(computer&&policy=="Block")
                    {
                        bool exception=false;int dash=target.LastIndexOf('-');
                        if(adminComplete&&type=="Owns"&&target.StartsWith("S-1-5-21-",StringComparison.OrdinalIgnoreCase)&&dash>0)
                        {admin.Bind(1,target.Substring(0,dash));admin.Bind(2,source);exception=admin.Read();admin.Reset();}
                        usable=exception;reason=exception?"Captured blocking mode; same-domain DA/EA membership supplies an owner exception":"Captured blocking mode; no supported owner exception established";
                        if(exception)adminWitness=OwnershipAdminPath(target.Substring(0,dash),source);
                    }
                    else if(computer&&policy=="Restricted"){usable=false;reason="Newer restricted mode needs operation-specific server evaluation";}
                    else if(computer&&policy!="Allow"){assessment="Conditional candidate";reason="Computer owner behavior depends on server/version defaults; captured mode: "+policy;}
                    else if(computer){reason="Explicit non-blocking policy value captured; server enforcement still unverified";}
                    if(!usable){assessment="Suppressed projection";suppressed++;}else if(assessment=="Conditional candidate")conditional++;else supported++;
                    json.Begin().Prop("Source",source).Prop("Target",target).Prop("EdgeType",type).Prop("Derivation","OwnershipControlV1")
                        .Prop("Assessment",assessment).Prop("Reason",reason).Prop("OwnerRightsPresent",present).Prop("OwnerRightsWriteDacl",explicitWd)
                        .Prop("ComputerDerived",computer).Prop("OwnerPolicyMode",policy).Prop("EffectiveAccessVerified",false).Prop("ServerEnforcementVerified",false);
                    json.Prop("PolicySource",policySource);
                    json.PropArray("AdminMembership",adminWitness);
                    write.Bind(1,target);write.Bind(2,usable?"acl":"observed");write.Bind(3,source);write.Bind(4,type);write.Bind(5,json.End());write.Read();write.Reset();
                }
            }
            // Keep a machine-readable conflict marker in retained nodes and merges.
            using(var rows=new Statement(this,"SELECT n.id,n.record FROM owner_meta m JOIN nodes n ON n.id=m.id WHERE m.conflict<>0 AND n.record IS NOT NULL"))
            using(var update=new Statement(this,"UPDATE nodes SET record=?1 WHERE id=?2"))
            {while(rows.Read()){update.BindRecord(1,GpoConflictRecord(rows.Record(1),"OwnershipEvidenceConflict"));update.Bind(2,rows.Text(0));update.Read();update.Reset();}}
            OwnershipScopeStatus="Ownership relationships: "+supported+" supported projections, "+conditional+" conditional projections, "+suppressed+" suppressed projections; "+legacy+" conditional legacy relationships; effective access remains unverified"+(adminComplete?"":"; admin exception closure incomplete");
            using(var state=new Statement(this,"INSERT OR REPLACE INTO derivation_meta VALUES('ownership',?1)")){state.Bind(1,OwnershipScopeStatus);state.Read();}
        }
        public string OwnershipScopeNote()
        {
            if(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='derivation_meta'")==0)return "Legacy index: ownership context is unverified";
            using(var s=new Statement(this,"SELECT value FROM derivation_meta WHERE key='ownership'")){return s.Read()?s.Text(0):"Legacy ownership context is unverified";}
        }
        private string[] OwnershipAdminPath(string domain,string principal)
        {
            var path=new List<string>();using(var next=new Statement(this,"SELECT previous FROM owner_admins WHERE domain=?1 AND principal=?2"))
            {for(int i=0;principal!=null&&i<130;i++){path.Add(principal);next.Bind(1,domain);next.Bind(2,principal);principal=next.Read()?next.Text(0):null;next.Reset();}}
            return path.ToArray();
        }
    }
}
