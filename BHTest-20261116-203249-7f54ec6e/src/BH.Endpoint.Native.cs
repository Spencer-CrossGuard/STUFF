using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace BH.Native
{
    public sealed class EndpointTarget
    {
        public string Id;
        public string Host;
    }

    public sealed class EndpointSummary
    {
        public string Host;
        public string Id;
        public long Edges;
        public int SuccessfulOperations;
        public int FailedOperations;
        public int UnresolvedSessions;
        public List<string> Errors = new List<string>();
    }

    public sealed class EndpointTargetCursor : IDisposable
    {
        private readonly IEnumerator<EndpointTarget> cursor;
        public EndpointTargetCursor(string[] files) { cursor = EndpointNative.Targets(files).GetEnumerator(); }
        public bool MoveNext() { return cursor.MoveNext(); }
        public EndpointTarget Current { get { return cursor.Current; } }
        public void Dispose() { cursor.Dispose(); }
    }

    public static class EndpointNative
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LocalMember { public IntPtr Sid; public int SidUsage; public IntPtr Name; }
        [StructLayout(LayoutKind.Sequential)]
        private struct LoggedOnUser { public IntPtr User; public IntPtr Domain; public IntPtr OtherDomains; public IntPtr Server; }

        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(string server, string group, int level, out IntPtr buffer,
            int maximum, out int count, out int total, ref UIntPtr resume);
        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetWkstaUserEnum(string server, int level, out IntPtr buffer,
            int maximum, out int count, out int total, ref int resume);
        [DllImport("Netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);
        [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupAccountSid(string system, byte[] sid, StringBuilder name, ref uint nameLength,
            StringBuilder domain, ref uint domainLength, out int use);
        [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupAccountName(string system, string account, byte[] sid, ref uint sidLength,
            StringBuilder domain, ref uint domainLength, out int use);

        public static IEnumerable<EndpointTarget> Targets(string[] nodeFiles)
        {
            foreach (string file in nodeFiles)
                foreach (string line in NdjsonReader.Lines(file))
                    yield return new EndpointTarget {
                        Id = JsonRecordReader.ExtractField(line, "Id"),
                        Host = JsonRecordReader.ExtractField(line, "Dns")
                    };
        }

        public static void Append(string source, NdjsonWriter destination)
        {
            foreach (string line in NdjsonReader.Lines(source)) destination.WriteLine(line);
        }

        public static bool ValidHost(string host)
        {
            return !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
                Uri.CheckHostName(host) == UriHostNameType.Dns &&
                host.IndexOfAny(new[] { '\\', '/', ':', '"', ' ', '\r', '\n' }) < 0;
        }

        public static EndpointSummary Collect(string host, string id, string outputPath)
        {
            if (!ValidHost(host) || string.IsNullOrEmpty(id)) throw new ArgumentException("A valid DNS hostname and computer ID are required.");
            var result = new EndpointSummary { Host = host, Id = id };
            var json = new JsonRecordWriter();
            using (var writer = new NdjsonWriter(outputPath, false))
            {
                int[] rids = { 544, 555, 562, 580 };
                string[] rights = { "AdminTo", "CanRDP", "ExecuteDCOM", "CanPSRemote" };
                for (int i = 0; i < rids.Length; i++)
                {
                    try { CollectGroup(host, id, rids[i], rights[i], writer, json, result); result.SuccessfulOperations++; }
                    catch (Exception error) { result.FailedOperations++; result.Errors.Add("Local group " + rids[i] + ": " + error.Message); }
                }
                try { CollectSessions(host, id, writer, json, result); result.SuccessfulOperations++; }
                catch (Exception error) { result.FailedOperations++; result.Errors.Add("Logged-on users: " + error.Message); }
            }
            return result;
        }

        private static string GroupName(string host, int rid)
        {
            var sid = new SecurityIdentifier("S-1-5-32-" + rid);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            uint nameLength = 0, domainLength = 0;
            int use;
            LookupAccountSid(host, bytes, null, ref nameLength, null, ref domainLength, out use);
            if (nameLength == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var name = new StringBuilder((int)nameLength);
            var domain = new StringBuilder((int)domainLength);
            if (!LookupAccountSid(host, bytes, name, ref nameLength, domain, ref domainLength, out use))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return name.ToString();
        }

        private static void CollectGroup(string host, string id, int rid, string type, NdjsonWriter writer,
            JsonRecordWriter json, EndpointSummary summary)
        {
            string name = GroupName(host, rid);
            UIntPtr resume = UIntPtr.Zero;
            int size = Marshal.SizeOf(typeof(LocalMember));
            int status;
            do
            {
                IntPtr buffer = IntPtr.Zero;
                UIntPtr previous = resume;
                try
                {
                    int count, total;
                    status = NetLocalGroupGetMembers("\\\\" + host, name, 2, out buffer, 65536, out count, out total, ref resume);
                    if (status != 0 && status != 234) throw new Win32Exception(status);
                    for (int i = 0; i < count; i++)
                    {
                        var member = (LocalMember)Marshal.PtrToStructure(IntPtr.Add(buffer, i * size), typeof(LocalMember));
                        string sid = new SecurityIdentifier(member.Sid).Value;
                        writer.WriteLine(json.Begin().Prop("Source", sid).Prop("Target", id).Prop("EdgeType", type).End());
                        summary.Edges++;
                    }
                    if (status == 234 && count == 0 && previous == resume) throw new IOException("Enumeration made no progress.");
                }
                finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
            } while (status == 234);
        }

        private static string ResolveAccount(string host, string domain, string user)
        {
            string account = domain + "\\" + user;
            uint sidLength = 0, domainLength = 0;
            int use;
            LookupAccountName(host, account, null, ref sidLength, null, ref domainLength, out use);
            if (sidLength == 0) return null;
            var sid = new byte[sidLength];
            var name = new StringBuilder((int)domainLength);
            if (!LookupAccountName(host, account, sid, ref sidLength, name, ref domainLength, out use) || use != 1)
                return null;
            string value = new SecurityIdentifier(sid, 0).Value;
            return value.StartsWith("S-1-5-21-", StringComparison.Ordinal) ? value : null;
        }

        private static void CollectSessions(string host, string id, NdjsonWriter writer, JsonRecordWriter json, EndpointSummary summary)
        {
            int resume = 0;
            int size = Marshal.SizeOf(typeof(LoggedOnUser));
            int status;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                IntPtr buffer = IntPtr.Zero;
                int previous = resume;
                try
                {
                    int count, total;
                    status = NetWkstaUserEnum("\\\\" + host, 1, out buffer, 65536, out count, out total, ref resume);
                    if (status != 0 && status != 234) throw new Win32Exception(status);
                    for (int i = 0; i < count; i++)
                    {
                        var session = (LoggedOnUser)Marshal.PtrToStructure(IntPtr.Add(buffer, i * size), typeof(LoggedOnUser));
                        string user = Marshal.PtrToStringUni(session.User);
                        string domain = Marshal.PtrToStringUni(session.Domain);
                        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(domain)) { summary.UnresolvedSessions++; continue; }
                        if (user.EndsWith("$", StringComparison.Ordinal)) continue;
                        string sid = ResolveAccount(host, domain, user);
                        if (sid == null) { summary.UnresolvedSessions++; continue; }
                        if (!seen.Add(sid)) continue;
                        writer.WriteLine(json.Begin().Prop("Source", id).Prop("Target", sid).Prop("EdgeType", "HasSession").End());
                        summary.Edges++;
                    }
                    if (status == 234 && count == 0 && previous == resume) throw new IOException("Enumeration made no progress.");
                }
                finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
            } while (status == 234);
        }
    }
}
