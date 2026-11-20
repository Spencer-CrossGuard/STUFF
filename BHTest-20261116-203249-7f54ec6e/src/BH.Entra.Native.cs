// BH.Entra.Native.cs
//
// Entra ID (Azure AD) collector, same architecture as the AD collector: C# loaded with Add-Type,
// streaming gzip NDJSON shards (nodes_<kind>.*.ndjson.gz and edges_<bucket>.*.ndjson.gz), checkpointed
// so a stopped run resumes where it left off. Plain C# 5, no third-party assemblies.
//
// Design notes for very large tenants:
//   * Every list is streamed page by page into shards of PartPages pages; nothing is held in memory
//     except id lists and small lookup tables.
//   * The paging cursor (@odata.nextLink) is saved after each shard, so a crash or Ctrl+C loses at most one shard.
//   * Per-object fan-out (members, owners, app role assignments) uses $batch (20 requests per call)
//     with a worker pool whose width adapts: it halves on HTTP 429 and creeps back up on success.
//   * 429 and 503 honour Retry-After, other transient errors use exponential backoff with jitter.
//   * A forbidden or unlicensed optional section is recorded and skipped; it never aborts the run.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BH.Native;

namespace BH.Entra
{
    // ------------------------------------------------------------------ JSON
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            object value = ReadValue(text, ref i);
            return value;
        }

        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object ReadValue(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[i];
            if (c == '{')
            {
                var map = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; Skip(s, ref i);
                if (s[i] == '}') { i++; return map; }
                while (true)
                {
                    Skip(s, ref i);
                    string key = ReadString(s, ref i);
                    Skip(s, ref i);
                    if (s[i] != ':') throw new FormatException("Expected ':' in JSON at " + i);
                    i++;
                    map[key] = ReadValue(s, ref i);
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return map; }
                    throw new FormatException("Expected ',' or '}' in JSON at " + i);
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++; Skip(s, ref i);
                if (s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue(s, ref i));
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException("Expected ',' or ']' in JSON at " + i);
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException("Unexpected character '" + c + "' in JSON at " + i);
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string in JSON at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("Unterminated string in JSON.");
        }
    }

    public static class J
    {
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static List<object> Arr(object o) { return o as List<object>; }
        public static object Get(Dictionary<string, object> o, string key)
        {
            object v; return o != null && o.TryGetValue(key, out v) ? v : null;
        }
        public static string Str(Dictionary<string, object> o, string key)
        {
            object v = Get(o, key);
            if (v == null) return null;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            return v as string;
        }
        public static bool? Bool(Dictionary<string, object> o, string key)
        {
            object v = Get(o, key);
            if (v is bool) return (bool)v;
            return null;
        }
        public static int Count(Dictionary<string, object> o, string key)
        {
            var a = Arr(Get(o, key)); return a == null ? 0 : a.Count;
        }
        public static List<string> Strings(Dictionary<string, object> o, string key)
        {
            var result = new List<string>();
            var a = Arr(Get(o, key));
            if (a != null) foreach (object x in a) { string s = x as string; if (s != null) result.Add(s); }
            return result;
        }
    }

    // ------------------------------------------------------------------ clouds
    public sealed class CloudProfile
    {
        public string Name;
        public string GraphUrl;   // https://graph.microsoft.com
        public string LoginUrl;   // https://login.microsoftonline.com
        public string DefaultGraphVersion = "v1.0";

        public static CloudProfile Get(string name)
        {
            switch ((name ?? "usgovdod").ToLowerInvariant())
            {
                case "global": return new CloudProfile { Name = "global", GraphUrl = "https://graph.microsoft.com", LoginUrl = "https://login.microsoftonline.com" };
                case "usgov": case "gcchigh": case "usgovl4":
                    return new CloudProfile { Name = "usgov", GraphUrl = "https://graph.microsoft.us", LoginUrl = "https://login.microsoftonline.us" };
                case "usgovdod": case "dod": case "usgovl5":
                    return new CloudProfile { Name = "usgovdod", GraphUrl = "https://dod-graph.microsoft.us", LoginUrl = "https://login.microsoftonline.us" };
                case "china":
                    return new CloudProfile { Name = "china", GraphUrl = "https://microsoftgraph.chinacloudapi.cn", LoginUrl = "https://login.chinacloudapi.cn" };
                default: throw new ArgumentException("Unknown cloud '" + name + "'. Use global, usgov or usgovdod.");
            }
        }

        public static CloudProfile Custom(string graphUrl, string loginUrl)
        {
            return new CloudProfile { Name = "custom", GraphUrl = graphUrl.TrimEnd('/'), LoginUrl = loginUrl.TrimEnd('/') };
        }
    }

    // ------------------------------------------------------------------ tokens
    public abstract class TokenSource
    {
        private string token;
        private DateTime expires = DateTime.MinValue;
        private readonly object gate = new object();

        public string GetToken()
        {
            lock (gate)
            {
                if (token == null || DateTime.UtcNow > expires.AddMinutes(-5))
                {
                    int seconds;
                    token = Acquire(out seconds);
                    expires = DateTime.UtcNow.AddSeconds(seconds);
                }
                return token;
            }
        }

        public void Invalidate() { lock (gate) { token = null; } }
        protected abstract string Acquire(out int expiresInSeconds);

        protected static string PostForm(string url, Dictionary<string, string> form)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(60);
                var response = http.PostAsync(url, new FormUrlEncodedContent(form)).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode && !body.Contains("authorization_pending") && !body.Contains("slow_down"))
                    throw new InvalidOperationException("Token endpoint returned " + (int)response.StatusCode + ": " + Trim(body));
                return body;
            }
        }

        protected static string Trim(string s) { return s != null && s.Length > 600 ? s.Substring(0, 600) : s; }

        protected static int ReadToken(string body, out string accessToken)
        {
            var o = J.Obj(MiniJson.Parse(body));
            accessToken = J.Str(o, "access_token");
            if (accessToken == null) throw new InvalidOperationException("Token response had no access_token: " + Trim(body));
            int seconds;
            if (!int.TryParse(J.Str(o, "expires_in"), out seconds)) seconds = 3000;
            return seconds;
        }
    }

    public sealed class StaticTokenSource : TokenSource
    {
        private readonly string value;
        public StaticTokenSource(string accessToken) { value = accessToken; }
        protected override string Acquire(out int expiresInSeconds) { expiresInSeconds = 3000; return value; }
    }

    public sealed class SecretTokenSource : TokenSource
    {
        private readonly string tokenUrl, clientId, secret, scope;
        public SecretTokenSource(CloudProfile cloud, string tenant, string clientId, string secret)
        {
            tokenUrl = cloud.LoginUrl + "/" + tenant + "/oauth2/v2.0/token";
            this.clientId = clientId; this.secret = secret; scope = cloud.GraphUrl + "/.default";
        }
        protected override string Acquire(out int expiresInSeconds)
        {
            var form = new Dictionary<string, string>();
            form["grant_type"] = "client_credentials"; form["client_id"] = clientId; form["client_secret"] = secret; form["scope"] = scope;
            string token; expiresInSeconds = ReadToken(PostForm(tokenUrl, form), out token); return token;
        }
    }

    public sealed class CertificateTokenSource : TokenSource
    {
        private readonly string tokenUrl, clientId, scope;
        private readonly X509Certificate2 cert;
        public CertificateTokenSource(CloudProfile cloud, string tenant, string clientId, X509Certificate2 cert)
        {
            tokenUrl = cloud.LoginUrl + "/" + tenant + "/oauth2/v2.0/token";
            this.clientId = clientId; this.cert = cert; scope = cloud.GraphUrl + "/.default";
        }

        private static string B64(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        public static string BuildAssertion(X509Certificate2 cert, string clientId, string audience)
        {
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            string header = "{\"alg\":\"RS256\",\"typ\":\"JWT\",\"x5t\":\"" + B64(cert.GetCertHash()) + "\"}";
            string claims = "{\"aud\":\"" + audience + "\",\"iss\":\"" + clientId + "\",\"sub\":\"" + clientId + "\",\"jti\":\"" +
                Guid.NewGuid().ToString() + "\",\"nbf\":" + (now - 60) + ",\"exp\":" + (now + 540) + "}";
            string signingInput = B64(Encoding.UTF8.GetBytes(header)) + "." + B64(Encoding.UTF8.GetBytes(claims));
            RSA rsa = cert.GetRSAPrivateKey();
            if (rsa == null) throw new InvalidOperationException("The certificate has no accessible RSA private key.");
            byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return signingInput + "." + B64(signature);
        }

        protected override string Acquire(out int expiresInSeconds)
        {
            var form = new Dictionary<string, string>();
            form["grant_type"] = "client_credentials"; form["client_id"] = clientId; form["scope"] = scope;
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = BuildAssertion(cert, clientId, tokenUrl);
            string token; expiresInSeconds = ReadToken(PostForm(tokenUrl, form), out token); return token;
        }
    }

    /// <summary>Delegated sign-in for a user such as a Global Reader. Shows the code through the callback.</summary>
    public sealed class DeviceCodeTokenSource : TokenSource
    {
        private readonly string tokenUrl, deviceUrl, clientId, scope;
        private readonly Action<string> notify;
        private string refresh;

        // Microsoft Azure PowerShell public client id works in every cloud; Graph scope is delegated .default.
        public const string DefaultClientId = "1950a258-227b-4e31-a9cf-717495945fc2";

        public DeviceCodeTokenSource(CloudProfile cloud, string tenant, string clientId, Action<string> notify)
        {
            tokenUrl = cloud.LoginUrl + "/" + tenant + "/oauth2/v2.0/token";
            deviceUrl = cloud.LoginUrl + "/" + tenant + "/oauth2/v2.0/devicecode";
            this.clientId = string.IsNullOrEmpty(clientId) ? DefaultClientId : clientId;
            scope = cloud.GraphUrl + "/.default offline_access";
            this.notify = notify;
        }

        protected override string Acquire(out int expiresInSeconds)
        {
            string token;
            if (refresh != null)
            {
                try
                {
                    var f = new Dictionary<string, string>();
                    f["grant_type"] = "refresh_token"; f["client_id"] = clientId; f["refresh_token"] = refresh; f["scope"] = scope;
                    string body = PostForm(tokenUrl, f);
                    var o = J.Obj(MiniJson.Parse(body)); refresh = J.Str(o, "refresh_token") ?? refresh;
                    expiresInSeconds = ReadToken(body, out token); return token;
                }
                catch (InvalidOperationException) { refresh = null; }
            }
            var form = new Dictionary<string, string>();
            form["client_id"] = clientId; form["scope"] = scope;
            var start = J.Obj(MiniJson.Parse(PostForm(deviceUrl, form)));
            string message = J.Str(start, "message");
            if (notify != null) notify(message);
            string code = J.Str(start, "device_code");
            int interval; if (!int.TryParse(J.Str(start, "interval"), out interval)) interval = 5;
            int lifetime; if (!int.TryParse(J.Str(start, "expires_in"), out lifetime)) lifetime = 900;
            DateTime deadline = DateTime.UtcNow.AddSeconds(lifetime);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(interval * 1000);
                var poll = new Dictionary<string, string>();
                poll["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"; poll["client_id"] = clientId; poll["device_code"] = code;
                string body = PostForm(tokenUrl, poll);
                var o = J.Obj(MiniJson.Parse(body));
                string err = J.Str(o, "error");
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { interval += 5; continue; }
                if (err != null) throw new InvalidOperationException("Device sign-in failed: " + Trim(body));
                refresh = J.Str(o, "refresh_token");
                expiresInSeconds = ReadToken(body, out token); return token;
            }
            throw new TimeoutException("Device sign-in timed out before the code was entered.");
        }
    }

    // ------------------------------------------------------------------ HTTP
    public sealed class GraphException : Exception
    {
        public int Status;
        public GraphException(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>Concurrency gate that backs off on throttling and recovers slowly.</summary>
    public sealed class AdaptiveGate
    {
        private readonly object sync = new object();
        private int limit, active, successes;
        private DateTime lastCut = DateTime.MinValue;
        public readonly int Max;
        public AdaptiveGate(int max) { Max = Math.Max(1, max); limit = Math.Max(1, Math.Min(Max, 8)); }
        public int Limit { get { lock (sync) return limit; } }

        public void Enter()
        {
            lock (sync) { while (active >= limit) Monitor.Wait(sync, 500); active++; }
        }
        public void Exit() { lock (sync) { active--; Monitor.PulseAll(sync); } }
        public void Throttled()
        {
            lock (sync)
            {
                successes = 0;
                // A burst of simultaneous 429s counts once.
                if ((DateTime.UtcNow - lastCut).TotalSeconds < 3) return;
                lastCut = DateTime.UtcNow;
                limit = Math.Max(1, limit / 2);
            }
        }
        public void Success()
        {
            lock (sync)
            {
                if (++successes >= 25 && limit < Max) { limit++; successes = 0; Monitor.PulseAll(sync); }
            }
        }
    }

    public sealed class GraphClient
    {
        private readonly HttpClient http;
        private readonly TokenSource tokens;
        public readonly CloudProfile Cloud;
        public readonly AdaptiveGate Gate;
        public long Requests, Throttles, Retries;
        public volatile bool Cancel;
        private readonly Random jitter = new Random();

        public GraphClient(CloudProfile cloud, TokenSource tokens, int maxConcurrency)
        {
            Cloud = cloud; this.tokens = tokens; Gate = new AdaptiveGate(maxConcurrency);
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            ServicePointManager.DefaultConnectionLimit = Math.Max(16, maxConcurrency * 2);
            var handler = new HttpClientHandler();
            if (handler.SupportsAutomaticDecompression) handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            http = new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(120);
        }

        public string Url(string path)
        {
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
            return Cloud.GraphUrl + "/" + (path.StartsWith("beta/") ? "" : Cloud.DefaultGraphVersion + "/") + path.TrimStart('/');
        }

        private void Sleep(double seconds)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (!Cancel && DateTime.UtcNow < end) Thread.Sleep(200);
        }

        public string Send(HttpMethod method, string path, string jsonBody)
        {
            string url = Url(path);
            for (int attempt = 0; ; attempt++)
            {
                if (Cancel) throw new OperationCanceledException();
                HttpResponseMessage response = null;
                string body = null;
                Exception network = null;
                Gate.Enter();
                try
                {
                    var request = new HttpRequestMessage(method, url);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokens.GetToken());
                    request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
                    if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    Interlocked.Increment(ref Requests);
                    response = http.SendAsync(request).GetAwaiter().GetResult();
                    body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (HttpRequestException ex) { network = ex; }
                catch (TaskCanceledException ex) { network = ex; }
                finally { Gate.Exit(); }

                if (network != null)
                {
                    if (attempt >= 5) throw new GraphException(0, "Network error for " + Short(url) + ": " + network.Message);
                    Interlocked.Increment(ref Retries);
                    Sleep(Math.Min(30, Math.Pow(2, attempt)));
                    continue;
                }
                int code = (int)response.StatusCode;
                if (code >= 200 && code < 300) { Gate.Success(); return body; }
                if (code == 401 && attempt < 2) { tokens.Invalidate(); continue; }
                bool transient = code == 429 || code == 503 || code == 502 || code == 504 || code == 500 || code == 408;
                if (!transient || attempt >= 8)
                    throw new GraphException(code, "HTTP " + code + " for " + Short(url) + ": " + (body.Length > 300 ? body.Substring(0, 300) : body));
                double wait = Math.Min(60, Math.Pow(2, attempt)) + jitter.NextDouble();
                if (response.Headers.RetryAfter != null)
                {
                    if (response.Headers.RetryAfter.Delta.HasValue) wait = Math.Max(wait, response.Headers.RetryAfter.Delta.Value.TotalSeconds);
                    else if (response.Headers.RetryAfter.Date.HasValue) wait = Math.Max(wait, (response.Headers.RetryAfter.Date.Value.UtcDateTime - DateTime.UtcNow).TotalSeconds);
                }
                if (code == 429) { Interlocked.Increment(ref Throttles); Gate.Throttled(); }
                Interlocked.Increment(ref Retries);
                Sleep(Math.Min(wait, 300));
            }
        }

        private static string Short(string url) { int q = url.IndexOf('?'); return q > 0 ? url.Substring(0, q) : url; }

        public Dictionary<string, object> GetJson(string path)
        {
            return J.Obj(MiniJson.Parse(Send(HttpMethod.Get, path, null)));
        }

        /// <summary>Up to 20 GETs in one call. Returns one entry per request: status and body object.</summary>
        public List<BatchResult> Batch(List<string> relativeUrls)
        {
            var sb = new StringBuilder("{\"requests\":[");
            for (int i = 0; i < relativeUrls.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(i).Append("\",\"method\":\"GET\",\"url\":\"");
                sb.Append(relativeUrls[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\"headers\":{\"ConsistencyLevel\":\"eventual\"}}");
            }
            sb.Append("]}");
            var reply = J.Obj(MiniJson.Parse(Send(HttpMethod.Post, "$batch", sb.ToString())));
            var results = new BatchResult[relativeUrls.Count];
            foreach (object item in J.Arr(J.Get(reply, "responses")) ?? new List<object>())
            {
                var r = J.Obj(item); int id = int.Parse(J.Str(r, "id"), CultureInfo.InvariantCulture);
                var h = J.Obj(J.Get(r, "headers"));
                results[id] = new BatchResult { Status = (int)(double)J.Get(r, "status"), Body = J.Obj(J.Get(r, "body")),
                    RetryAfter = h == null ? null : J.Str(h, "Retry-After") };
            }
            return new List<BatchResult>(results);
        }
    }

    public sealed class BatchResult
    {
        public int Status;
        public Dictionary<string, object> Body;
        public string RetryAfter;
    }

    // ------------------------------------------------------------------ collection
    public sealed class EntraOptions
    {
        public string OutputDirectory;
        public string TenantId;
        public int Threads = 16;
        public int PartPages = 20;          // pages per shard
        public int PageSize = 999;
        public string Members = "All";      // All | RoleAssignable | None
        public bool IncludeOwners = true;
        public bool IncludeEligibleRoles = true;
        public bool IncludeAppRoleAssignments = true;
        public bool Compress = true;
        public HashSet<string> PrivilegedRoleTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Sections;     // null = all
    }

    public sealed class EntraProgress
    {
        public volatile string Section = "starting";
        public long Objects, Edges, Errors;
        public long SectionDone, SectionTotal;
        public string Note = "";
        public readonly List<string> Problems = new List<string>();
        public void Problem(string text) { lock (Problems) { Errors++; if (Problems.Count < 200) Problems.Add(text); } }
    }

    public sealed class EntraCollector
    {
        public static readonly string[] SectionOrder = new string[] {
            "tenant", "users", "groups", "serviceprincipals", "applications", "roles", "roleassignments",
            "groupmembers", "owners", "approleassignments", "oauthgrants" };

        private static readonly HashSet<string> DangerousAppRoles = new HashSet<string>(new string[] {
            "RoleManagement.ReadWrite.Directory", "AppRoleAssignment.ReadWrite.All", "Application.ReadWrite.All",
            "Directory.ReadWrite.All", "Group.ReadWrite.All", "GroupMember.ReadWrite.All", "User.ReadWrite.All",
            "UserAuthenticationMethod.ReadWrite.All", "Policy.ReadWrite.ConditionalAccess", "PrivilegedAccess.ReadWrite.AzureAD",
            "PrivilegedAccess.ReadWrite.AzureADGroup", "RoleEligibilitySchedule.ReadWrite.Directory", "RoleAssignmentSchedule.ReadWrite.Directory",
            "Application.ReadWrite.OwnedBy", "DelegatedPermissionGrant.ReadWrite.All", "Directory.AccessAsUser.All",
            "User.EnableDisableAccount.All", "Device.ReadWrite.All", "Mail.ReadWrite", "Files.ReadWrite.All" }, StringComparer.OrdinalIgnoreCase);

        private readonly GraphClient graph;
        private readonly EntraOptions options;
        public readonly EntraProgress Progress = new EntraProgress();
        private readonly string stateDir;
        private readonly object writeLock = new object();
        private Dictionary<string, NdjsonWriter> writers = new Dictionary<string, NdjsonWriter>();
        private readonly JsonRecordWriter nodeJson = new JsonRecordWriter();
        private readonly JsonRecordWriter edgeJson = new JsonRecordWriter();
        private string part;
        private string tenantId;
        // id -> (appRoleId -> value) for resource service principals; small compared to the tenant.
        private readonly Dictionary<string, Dictionary<string, string>> appRoleNames = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> spKind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public EntraCollector(GraphClient graph, EntraOptions options)
        {
            this.graph = graph; this.options = options;
            Directory.CreateDirectory(options.OutputDirectory);
            stateDir = Path.Combine(options.OutputDirectory, "state");
            Directory.CreateDirectory(stateDir);
            tenantId = options.TenantId;
        }

        public Task Start() { return Task.Factory.StartNew(new Action(Run), TaskCreationOptions.LongRunning); }
        public void Cancel() { graph.Cancel = true; }

        public void Run()
        {
            try
            {
                foreach (string section in SectionOrder)
                {
                    if (graph.Cancel) break;
                    if (options.Sections != null && !options.Sections.Contains(section)) continue;
                    if (File.Exists(Path.Combine(stateDir, section + ".done")))
                    {
                        Progress.Note = "skipped " + section + " (already complete)";
                        if (section == "serviceprincipals") LoadServicePrincipalLookup();
                        continue;
                    }
                    Progress.Section = section; Progress.SectionDone = 0; Progress.SectionTotal = 0;
                    try
                    {
                        RunSection(section);
                        if (!graph.Cancel) File.WriteAllText(Path.Combine(stateDir, section + ".done"), DateTime.UtcNow.ToString("o"));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (GraphException ex)
                    {
                        if (ex.Status == 401 || ex.Status == 403 || ex.Status == 400 || ex.Status == 404)
                        {
                            Progress.Problem("Section '" + section + "' skipped: " + ex.Message);
                            File.WriteAllText(Path.Combine(stateDir, section + ".done"), "skipped: " + ex.Message);
                        }
                        else throw;
                    }
                    finally { CloseWriters(); }
                }
                if (!graph.Cancel) { Progress.Section = "complete"; WriteManifest(); }
            }
            finally { CloseWriters(); }
        }

        // -------------------------------------------------- output helpers
        private NdjsonWriter Writer(string prefix)
        {
            NdjsonWriter w;
            if (!writers.TryGetValue(prefix, out w))
            {
                w = new NdjsonWriter(Path.Combine(options.OutputDirectory, prefix + "." + part + ".ndjson" + (options.Compress ? ".gz" : "")), false);
                writers.Add(prefix, w);
            }
            return w;
        }

        private void CloseWriters()
        {
            foreach (var w in writers.Values) w.Dispose();
            writers.Clear();
        }

        private void Node(string kind, JsonRecordWriter json)
        {
            Writer("nodes_" + kind).WriteLine(json.End()); Progress.Objects++;
        }

        private void Edge(string bucket, string source, string target, string type, string evidence)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
            edgeJson.Begin().Prop("Source", source).Prop("Target", target).Prop("EdgeType", type);
            if (evidence != null) edgeJson.Prop("Evidence", evidence);
            Writer("edges_" + bucket).WriteLine(edgeJson.End()); Progress.Edges++;
        }

        private static string Esc(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void WriteManifest()
        {
            var sb = new StringBuilder("{\"Source\":\"EntraID\",\"TenantId\":\"" + Esc(tenantId) + "\",\"Cloud\":\"" + Esc(graph.Cloud.Name) +
                "\",\"Completed\":\"" + DateTime.UtcNow.ToString("o") + "\",\"Objects\":" + Progress.Objects + ",\"Edges\":" + Progress.Edges +
                ",\"Requests\":" + graph.Requests + ",\"Throttled\":" + graph.Throttles + ",\"Problems\":[");
            lock (Progress.Problems)
                for (int i = 0; i < Progress.Problems.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(Esc(Progress.Problems[i])).Append('"'); }
            sb.Append("]}");
            File.WriteAllText(Path.Combine(options.OutputDirectory, "entra-manifest.json"), sb.ToString());
        }

        // -------------------------------------------------- paged lists
        private delegate void ObjectHandler(Dictionary<string, object> item);

        /// <summary>Streams a list endpoint into numbered shards, saving the cursor after every shard.</summary>
        private void StreamList(string section, string firstUrl, ObjectHandler handler)
        {
            string stateFile = Path.Combine(stateDir, section + ".cursor");
            string next = firstUrl; int partNo = 0; long done = 0;
            if (File.Exists(stateFile))
            {
                string[] lines = File.ReadAllLines(stateFile);
                if (lines.Length >= 3) { next = lines[0]; partNo = int.Parse(lines[1]); done = long.Parse(lines[2]); }
                Progress.Note = "resuming " + section + " at shard " + partNo;
            }
            Progress.SectionDone = done;
            while (next != null)
            {
                part = section + "-" + partNo.ToString("D5");
                string cursor = next;
                for (int pages = 0; pages < options.PartPages && next != null; pages++)
                {
                    if (graph.Cancel) { CloseWriters(); throw new OperationCanceledException(); }
                    var page = graph.GetJson(next);
                    foreach (object item in J.Arr(J.Get(page, "value")) ?? new List<object>())
                    {
                        var o = J.Obj(item); if (o == null) continue;
                        try { handler(o); } catch (Exception ex) { Progress.Problem(section + ": " + ex.Message); }
                        Progress.SectionDone++;
                    }
                    next = J.Str(page, "@odata.nextLink");
                }
                CloseWriters();
                partNo++;
                // The cursor file always names the NEXT unfetched page, so a restart never repeats a finished shard.
                File.WriteAllText(stateFile, (next ?? "") + "\n" + partNo + "\n" + Progress.SectionDone);
                if (next == null) File.Delete(stateFile);
            }
        }

        private void RunSection(string section)
        {
            string sel = "?$top=" + options.PageSize;
            switch (section)
            {
                case "tenant": CollectTenant(); break;
                case "users":
                    StreamList(section, "users" + sel + "&$select=id,displayName,userPrincipalName,accountEnabled,userType,onPremisesSyncEnabled,onPremisesSecurityIdentifier,onPremisesDistinguishedName,onPremisesSamAccountName,createdDateTime,lastPasswordChangeDateTime,mail,jobTitle,department,externalUserState", WriteUser);
                    break;
                case "groups":
                    StreamList(section, "groups" + sel + "&$select=id,displayName,securityEnabled,mailEnabled,groupTypes,isAssignableToRole,onPremisesSyncEnabled,onPremisesSecurityIdentifier,membershipRule,visibility,createdDateTime", WriteGroup);
                    break;
                case "serviceprincipals":
                    StreamList(section, "servicePrincipals" + sel + "&$select=id,appId,displayName,servicePrincipalType,accountEnabled,appOwnerOrganizationId,appRoles,passwordCredentials,keyCredentials,tags,signInAudience", WriteServicePrincipal);
                    break;
                case "applications":
                    StreamList(section, "applications" + sel + "&$select=id,appId,displayName,signInAudience,passwordCredentials,keyCredentials,createdDateTime,publisherDomain", WriteApplication);
                    break;
                case "roles": CollectRoleDefinitions(); break;
                case "roleassignments": CollectRoleAssignments(); break;
                case "groupmembers": if (!string.Equals(options.Members, "None", StringComparison.OrdinalIgnoreCase)) CollectMembers(); break;
                case "owners": if (options.IncludeOwners) CollectOwners(); break;
                case "approleassignments": if (options.IncludeAppRoleAssignments) CollectAppRoleAssignments(); break;
                case "oauthgrants": CollectOAuthGrants(); break;
            }
        }

        // -------------------------------------------------- node writers
        private void CollectTenant()
        {
            part = "tenant-00000";
            var page = graph.GetJson("organization?$select=id,displayName,verifiedDomains,onPremisesSyncEnabled,createdDateTime");
            foreach (object item in J.Arr(J.Get(page, "value")) ?? new List<object>())
            {
                var o = J.Obj(item); string id = J.Str(o, "id");
                if (tenantId == null) tenantId = id;
                string domains = "";
                foreach (object d in J.Arr(J.Get(o, "verifiedDomains")) ?? new List<object>())
                { string n = J.Str(J.Obj(d), "name"); if (n != null) domains += (domains.Length > 0 ? "," : "") + n; }
                nodeJson.Begin().Prop("Id", id).PropS("Name", J.Str(o, "displayName")).Prop("Dn", id)
                    .PropS("Domains", domains).PropS("Created", J.Str(o, "createdDateTime"))
                    .Prop("OnPremisesSync", J.Bool(o, "onPremisesSyncEnabled") ?? false);
                Node("tenants", nodeJson);
            }
        }

        private void WriteUser(Dictionary<string, object> o)
        {
            string id = J.Str(o, "id");
            nodeJson.Begin().Prop("Id", id).PropS("Name", J.Str(o, "displayName")).Prop("Dn", id)
                .PropS("UserPrincipalName", J.Str(o, "userPrincipalName")).PropS("Mail", J.Str(o, "mail"))
                .Prop("Enabled", J.Bool(o, "accountEnabled") ?? true).PropS("UserType", J.Str(o, "userType"))
                .Prop("Synced", J.Bool(o, "onPremisesSyncEnabled") ?? false).PropS("Sid", J.Str(o, "onPremisesSecurityIdentifier"))
                .PropS("OnPremisesDn", J.Str(o, "onPremisesDistinguishedName")).PropS("SamAccountName", J.Str(o, "onPremisesSamAccountName"))
                .PropS("Created", J.Str(o, "createdDateTime")).PropS("PasswordChanged", J.Str(o, "lastPasswordChangeDateTime"))
                .PropS("Department", J.Str(o, "department")).PropS("JobTitle", J.Str(o, "jobTitle"))
                .PropS("GuestState", J.Str(o, "externalUserState"));
            Node("users", nodeJson);
        }

        private void WriteGroup(Dictionary<string, object> o)
        {
            string id = J.Str(o, "id");
            var types = J.Strings(o, "groupTypes");
            bool dynamic = types.Contains("DynamicMembership");
            nodeJson.Begin().Prop("Id", id).PropS("Name", J.Str(o, "displayName")).Prop("Dn", id)
                .Prop("SecurityEnabled", J.Bool(o, "securityEnabled") ?? false).Prop("MailEnabled", J.Bool(o, "mailEnabled") ?? false)
                .Prop("RoleAssignable", J.Bool(o, "isAssignableToRole") ?? false).Prop("Dynamic", dynamic)
                .Prop("Unified", types.Contains("Unified")).Prop("Synced", J.Bool(o, "onPremisesSyncEnabled") ?? false)
                .PropS("Sid", J.Str(o, "onPremisesSecurityIdentifier")).PropS("MembershipRule", J.Str(o, "membershipRule"))
                .PropS("Visibility", J.Str(o, "visibility")).PropS("Created", J.Str(o, "createdDateTime"));
            Node("groups", nodeJson);
        }

        private void WriteServicePrincipal(Dictionary<string, object> o)
        {
            string id = J.Str(o, "id");
            var roles = J.Arr(J.Get(o, "appRoles"));
            if (roles != null && roles.Count > 0)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (object r in roles) { var ro = J.Obj(r); string rid = J.Str(ro, "id"); if (rid != null) map[rid] = J.Str(ro, "value") ?? J.Str(ro, "displayName"); }
                lock (appRoleNames) appRoleNames[id] = map;
            }
            nodeJson.Begin().Prop("Id", id).PropS("Name", J.Str(o, "displayName")).Prop("Dn", id)
                .PropS("AppId", J.Str(o, "appId")).PropS("SpType", J.Str(o, "servicePrincipalType"))
                .Prop("Enabled", J.Bool(o, "accountEnabled") ?? true).PropS("OwnerTenant", J.Str(o, "appOwnerOrganizationId"))
                .Prop("Secrets", (long)J.Count(o, "passwordCredentials")).Prop("Certificates", (long)J.Count(o, "keyCredentials"))
                .Prop("ExposedRoles", (long)J.Count(o, "appRoles"));
            Node("serviceprincipals", nodeJson);
            string appId = J.Str(o, "appId");
            if (appId != null) Edge("observed", appId, id, "RunsAs", null);
        }

        private void WriteApplication(Dictionary<string, object> o)
        {
            string id = J.Str(o, "id");
            nodeJson.Begin().Prop("Id", id).PropS("Name", J.Str(o, "displayName")).Prop("Dn", id)
                .PropS("AppId", J.Str(o, "appId")).PropS("SignInAudience", J.Str(o, "signInAudience"))
                .Prop("Secrets", (long)J.Count(o, "passwordCredentials")).Prop("Certificates", (long)J.Count(o, "keyCredentials"))
                .PropS("Created", J.Str(o, "createdDateTime")).PropS("PublisherDomain", J.Str(o, "publisherDomain"));
            Node("applications", nodeJson);
            string appId = J.Str(o, "appId");
            if (appId != null) Edge("observed", id, appId, "AppRegistrationOf", null);
        }

        // -------------------------------------------------- roles
        private void CollectRoleDefinitions()
        {
            StreamList("roles", "roleManagement/directory/roleDefinitions?$select=id,displayName,description,isBuiltIn,isPrivileged,templateId", delegate(Dictionary<string, object> o)
            {
                string id = J.Str(o, "id");
                string template = J.Str(o, "templateId") ?? id;
                bool privileged = (J.Bool(o, "isPrivileged") ?? false) || options.PrivilegedRoleTemplates.Contains(template);
                nodeJson.Begin().Prop("Id", id).PropS("Name", J.Str(o, "displayName")).Prop("Dn", id)
                    .PropS("TemplateId", template).Prop("BuiltIn", J.Bool(o, "isBuiltIn") ?? false)
                    .Prop("Privileged", privileged).PropS("Description", J.Str(o, "description"));
                Node("entraroles", nodeJson);
            });
        }

        private void CollectRoleAssignments()
        {
            StreamList("roleassignments", "roleManagement/directory/roleAssignments?$top=" + options.PageSize, delegate(Dictionary<string, object> o)
            {
                string scope = J.Str(o, "directoryScopeId") ?? "/";
                Edge("access", J.Str(o, "principalId"), J.Str(o, "roleDefinitionId"), "HasEntraRole",
                    "{\"Assignment\":\"Active\",\"Scope\":\"" + Esc(scope) + "\"}");
            });
            if (options.IncludeEligibleRoles)
            {
                try
                {
                    StreamList("roleeligibility", "roleManagement/directory/roleEligibilityScheduleInstances?$top=" + options.PageSize, delegate(Dictionary<string, object> o)
                    {
                        string scope = J.Str(o, "directoryScopeId") ?? "/";
                        Edge("access", J.Str(o, "principalId"), J.Str(o, "roleDefinitionId"), "EligibleEntraRole",
                            "{\"Assignment\":\"Eligible\",\"Scope\":\"" + Esc(scope) + "\",\"Ends\":\"" + Esc(J.Str(o, "endDateTime")) + "\"}");
                    });
                }
                catch (GraphException ex) { if (ex.Status == 401 || ex.Status == 403 || ex.Status == 400) Progress.Problem("Eligible role assignments not readable (needs RoleEligibilitySchedule.Read.Directory and an Entra ID P2 license): " + ex.Message); else throw; }
            }
        }

        // -------------------------------------------------- per-object fan-out
        private List<string> ReadIds(string nodeKind, Func<Dictionary<string, object>, bool> filter)
        {
            var ids = new List<string>();
            foreach (string file in Directory.GetFiles(options.OutputDirectory, "nodes_" + nodeKind + ".*.ndjson*"))
            {
                Stream s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) s = new GZipStream(s, CompressionMode.Decompress);
                using (var reader = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        var o = J.Obj(MiniJson.Parse(line));
                        if (filter == null || filter(o)) ids.Add(J.Str(o, "Id"));
                    }
                }
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        private delegate void BatchHandler(string id, Dictionary<string, object> body);

        /// <summary>
        /// Runs one request per id through $batch with a worker pool. Pages beyond the first are followed
        /// individually. Chunks of ids are written as shards and checkpointed by index.
        /// </summary>
        private void FanOut(string section, List<string> ids, string urlFormat, string label, BatchHandler handler)
        {
            string stateFile = Path.Combine(stateDir, section + ".index");
            int start = 0, partNo = 0;
            if (File.Exists(stateFile))
            {
                string[] lines = File.ReadAllLines(stateFile);
                start = int.Parse(lines[0]); partNo = int.Parse(lines[1]);
            }
            Progress.SectionTotal = ids.Count; Progress.SectionDone = start;
            const int chunk = 4000;
            while (start < ids.Count)
            {
                if (graph.Cancel) throw new OperationCanceledException();
                part = section + "-" + partNo.ToString("D5");
                int end = Math.Min(ids.Count, start + chunk);
                var batches = new List<List<string>>();
                for (int i = start; i < end; i += 20) batches.Add(ids.GetRange(i, Math.Min(20, end - i)));
                var queue = new Queue<List<string>>(batches);
                var failure = new List<Exception>();
                int workers = Math.Min(options.Threads, Math.Max(1, batches.Count));
                var threads = new List<Thread>();
                for (int w = 0; w < workers; w++)
                {
                    var t = new Thread(delegate()
                    {
                        while (true)
                        {
                            List<string> mine;
                            lock (queue) { if (queue.Count == 0 || failure.Count > 0 || graph.Cancel) return; mine = queue.Dequeue(); }
                            try { RunBatch(mine, urlFormat, label, handler); }
                            catch (Exception ex) { lock (queue) failure.Add(ex); return; }
                            Interlocked.Add(ref Progress.SectionDone, mine.Count);
                        }
                    });
                    t.IsBackground = true; t.Start(); threads.Add(t);
                }
                foreach (var t in threads) t.Join();
                CloseWriters();
                if (failure.Count > 0) throw failure[0];
                if (graph.Cancel) throw new OperationCanceledException();
                start = end; partNo++;
                File.WriteAllText(stateFile, start + "\n" + partNo);
            }
            if (File.Exists(stateFile)) File.Delete(stateFile);
        }

        private void RunBatch(List<string> ids, string urlFormat, string label, BatchHandler handler)
        {
            var urls = new List<string>();
            foreach (string id in ids) urls.Add(string.Format(urlFormat, id));
            List<BatchResult> results = graph.Batch(urls);
            var retry = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                BatchResult r = results[i];
                if (r == null || r.Status == 429 || r.Status == 503) { retry.Add(ids[i]); continue; }
                if (r.Status == 404) continue;   // deleted between listing and lookup
                if (r.Status >= 400) { Progress.Problem(label + " " + ids[i] + ": HTTP " + r.Status); continue; }
                Consume(ids[i], r.Body, handler);
            }
            if (retry.Count > 0)
            {
                graph.Gate.Throttled();
                Thread.Sleep(1500);
                foreach (string id in retry)
                {
                    try { Consume(id, graph.GetJson(string.Format(urlFormat, id)), handler); }
                    catch (GraphException ex) { if (ex.Status != 404) Progress.Problem(label + " " + id + ": " + ex.Message); }
                }
            }
        }

        private void Consume(string id, Dictionary<string, object> body, BatchHandler handler)
        {
            while (true)
            {
                lock (writeLock) { handler(id, body); }
                string next = J.Str(body, "@odata.nextLink");
                if (next == null) return;
                body = graph.GetJson(next);
            }
        }

        private void CollectMembers()
        {
            bool all = string.Equals(options.Members, "All", StringComparison.OrdinalIgnoreCase);
            List<string> ids = ReadIds("groups", all ? (Func<Dictionary<string, object>, bool>)null : delegate(Dictionary<string, object> o) { return J.Bool(o, "RoleAssignable") == true; });
            FanOut("groupmembers", ids, "groups/{0}/members?$select=id&$top=" + options.PageSize, "members of group", delegate(string group, Dictionary<string, object> body)
            {
                foreach (object item in J.Arr(J.Get(body, "value")) ?? new List<object>())
                {
                    var m = J.Obj(item); string memberId = J.Str(m, "id");
                    Edge("memberof", memberId, group, "MemberOf", null);
                }
            });
        }

        private void CollectOwners()
        {
            foreach (string pair in new string[] { "groups:groups", "applications:applications", "serviceprincipals:servicePrincipals" })
            {
                string kind = pair.Split(':')[0], path = pair.Split(':')[1];
                List<string> ids = ReadIds(kind, null);
                FanOut("owners-" + kind, ids, path + "/{0}/owners?$select=id&$top=" + options.PageSize, "owners of " + kind, delegate(string target, Dictionary<string, object> body)
                {
                    foreach (object item in J.Arr(J.Get(body, "value")) ?? new List<object>())
                        Edge("acl", J.Str(J.Obj(item), "id"), target, "Owns", null);
                });
            }
        }

        private void LoadServicePrincipalLookup()
        {
            if (appRoleNames.Count > 0) return;
            string next = "servicePrincipals?$top=" + options.PageSize + "&$select=id,appRoles";
            try
            {
                while (next != null)
                {
                    var page = graph.GetJson(next);
                    foreach (object item in J.Arr(J.Get(page, "value")) ?? new List<object>()) WriteLookupOnly(J.Obj(item));
                    next = J.Str(page, "@odata.nextLink");
                }
            }
            catch (GraphException ex) { Progress.Problem("Could not rebuild app role names on resume: " + ex.Message); }
        }

        private void WriteLookupOnly(Dictionary<string, object> o)
        {
            string id = J.Str(o, "id"); var roles = J.Arr(J.Get(o, "appRoles"));
            if (roles == null || roles.Count == 0) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (object r in roles) { var ro = J.Obj(r); string rid = J.Str(ro, "id"); if (rid != null) map[rid] = J.Str(ro, "value") ?? J.Str(ro, "displayName"); }
            lock (appRoleNames) appRoleNames[id] = map;
        }

        private void CollectAppRoleAssignments()
        {
            // Only service principals that expose app roles can have anything assigned to them.
            List<string> ids = ReadIds("serviceprincipals", delegate(Dictionary<string, object> o)
            {
                object n = J.Get(o, "ExposedRoles"); return n is double && (double)n > 0;
            });
            FanOut("approleassignments", ids, "servicePrincipals/{0}/appRoleAssignedTo?$top=" + options.PageSize, "app role assignments", delegate(string resource, Dictionary<string, object> body)
            {
                foreach (object item in J.Arr(J.Get(body, "value")) ?? new List<object>())
                {
                    var a = J.Obj(item);
                    string roleId = J.Str(a, "appRoleId"), name = null;
                    Dictionary<string, string> map;
                    if (roleId != null && appRoleNames.TryGetValue(resource, out map)) map.TryGetValue(roleId, out name);
                    if (roleId == "00000000-0000-0000-0000-000000000000") name = "(default access)";
                    bool dangerous = name != null && DangerousAppRoles.Contains(name);
                    Edge("access", J.Str(a, "principalId"), resource, "AppRoleAssigned",
                        "{\"Role\":\"" + Esc(name ?? roleId) + "\",\"RoleId\":\"" + Esc(roleId) + "\",\"PrincipalType\":\"" + Esc(J.Str(a, "principalType")) +
                        "\",\"Dangerous\":" + (dangerous ? "true" : "false") + "}");
                }
            });
        }

        private void CollectOAuthGrants()
        {
            StreamList("oauthgrants", "oauth2PermissionGrants?$top=" + Math.Min(options.PageSize, 999), delegate(Dictionary<string, object> o)
            {
                Edge("access", J.Str(o, "clientId"), J.Str(o, "resourceId"), "OAuthGrant",
                    "{\"Scope\":\"" + Esc(J.Str(o, "scope")) + "\",\"ConsentType\":\"" + Esc(J.Str(o, "consentType")) +
                    "\",\"PrincipalId\":\"" + Esc(J.Str(o, "principalId")) + "\"}");
            });
        }
    }
}
