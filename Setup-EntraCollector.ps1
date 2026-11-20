# Installs the Entra ID collector into an existing BHTest folder. All files are embedded below.
# Usage: save as Setup-EntraCollector.ps1, then from anywhere run:
#   .\Setup-EntraCollector.ps1 -Target "C:\path\to\BHTest-folder"      (the folder that contains BH.Gui.ps1 and src)
# If run from inside that folder, -Target can be left out. Safe to run twice. BH.Core.psm1 is backed up first.
param([string]$Target = (Get-Location).Path)
$ErrorActionPreference = 'Stop'
$core = Join-Path $Target 'src\BH.Core.psm1'
if (-not (Test-Path -LiteralPath $core)) { throw "Cannot find src\BH.Core.psm1 under '$Target'. Pass -Target with the BHTest folder." }
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Save($rel, $text) {
    $path = Join-Path $Target $rel
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n") + "`n", $utf8)
    'wrote ' + $rel
}
Save 'src\BH.Entra.Native.cs' @'
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
'@
Save 'src\BH.Entra.psm1' @'
# BH.Entra.psm1 - Entra ID (Azure AD) collection entry point. Uses the native collector in BH.Entra.Native.cs,
# compiled together with the rest of the toolkit by Initialize-BHNative (BH.Core.psm1).

$script:EntraRoleFile = Join-Path $PSScriptRoot 'entra-roles.json'

function ConvertTo-BHPlainText([securestring]$Secure) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Get-BHEntraCertificate {
    param([string]$Thumbprint, [string]$PfxPath, [securestring]$PfxPassword)
    if ($PfxPath) {
        $password = if ($PfxPassword) { ConvertTo-BHPlainText $PfxPassword } else { $null }
        return New-Object Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $password)
    }
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
        $found = Get-ChildItem -LiteralPath $store -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(' ', '').ToUpperInvariant() } | Select-Object -First 1
        if ($found) { return $found }
    }
    throw "No certificate with thumbprint $Thumbprint was found in CurrentUser\My or LocalMachine\My."
}

function Format-BHDuration([double]$Seconds) {
    if ($Seconds -lt 0 -or [double]::IsInfinity($Seconds) -or [double]::IsNaN($Seconds)) { return 'unknown' }
    $span = [TimeSpan]::FromSeconds([Math]::Round($Seconds))
    if ($span.TotalHours -ge 1) { return '{0}h {1:00}m' -f [int]$span.TotalHours, $span.Minutes }
    return '{0}m {1:00}s' -f [int]$span.TotalMinutes, $span.Seconds
}

function Start-BHEntraCollection {
<#
.SYNOPSIS
Collects an Entra ID tenant into the same shard format as the AD collector.
.DESCRIPTION
Sign-in choices (pick one):
  -CertificateThumbprint, or -PfxPath [-PfxPassword]   app registration with a certificate (recommended)
  -ClientSecret                                         app registration with a secret
  -DeviceCode                                           a person signs in (for example a Global Reader)
  -AccessToken                                          a token you already have
Cloud defaults to USGovDoD (https://dod-graph.microsoft.us). Use -Cloud USGov for GCC High, Global for commercial.
App registration needs these Microsoft Graph APPLICATION permissions (read only) with admin consent:
  Directory.Read.All, RoleManagement.Read.Directory, Application.Read.All
  Optional: RoleEligibilitySchedule.Read.Directory (eligible role assignments, needs Entra ID P2).
A Global Reader signing in with -DeviceCode needs no app permissions set up by you.
Resume: pass -ResumeDirectory with the folder of an interrupted run.
#>
    [CmdletBinding()]
    param(
        [ValidateSet('USGovDoD', 'USGov', 'Global', 'China')][string]$Cloud = 'USGovDoD',
        [Parameter(Mandatory)][string]$TenantId,
        [string]$ClientId,
        [string]$CertificateThumbprint,
        [string]$PfxPath,
        [securestring]$PfxPassword,
        [securestring]$ClientSecret,
        [switch]$DeviceCode,
        [securestring]$AccessToken,
        [string]$OutputRoot = (Join-Path (Get-Location) 'entra-scans'),
        [string]$ResumeDirectory,
        [ValidateRange(1, 64)][int]$Threads = 16,
        [ValidateSet('All', 'RoleAssignable', 'None')][string]$Members = 'All',
        [switch]$SkipOwners,
        [switch]$SkipEligibleRoles,
        [switch]$SkipAppRoleAssignments,
        [string[]]$Sections,
        [switch]$NoCompress,
        [string]$GraphUrl,
        [string]$LoginUrl
    )

    Import-Module (Join-Path $PSScriptRoot 'BH.Core.psm1') -DisableNameChecking
    Initialize-BHNative

    $methods = @($CertificateThumbprint, $PfxPath, $ClientSecret, $AccessToken) | Where-Object { $_ }
    if (($methods.Count + [int][bool]$DeviceCode) -ne 1) { throw 'Choose exactly one sign-in method: -CertificateThumbprint, -PfxPath, -ClientSecret, -DeviceCode or -AccessToken.' }
    if (-not $DeviceCode -and -not $AccessToken -and -not $ClientId) { throw '-ClientId (the app registration id) is required with a certificate or secret.' }

    $cloudProfile = if ($GraphUrl) { [BH.Entra.CloudProfile]::Custom($GraphUrl, $(if ($LoginUrl) { $LoginUrl } else { $GraphUrl })) } else { [BH.Entra.CloudProfile]::Get($Cloud) }
    if ($AccessToken) { $tokens = New-Object BH.Entra.StaticTokenSource((ConvertTo-BHPlainText $AccessToken)) }
    elseif ($ClientSecret) { $tokens = New-Object BH.Entra.SecretTokenSource($cloudProfile, $TenantId, $ClientId, (ConvertTo-BHPlainText $ClientSecret)) }
    elseif ($DeviceCode) {
        $notify = [Action[string]] { param($m) Write-Host ''; Write-Host $m -ForegroundColor Yellow; Write-Host '' }
        $tokens = New-Object BH.Entra.DeviceCodeTokenSource($cloudProfile, $TenantId, $ClientId, $notify)
    }
    else { $tokens = New-Object BH.Entra.CertificateTokenSource($cloudProfile, $TenantId, $ClientId, (Get-BHEntraCertificate -Thumbprint $CertificateThumbprint -PfxPath $PfxPath -PfxPassword $PfxPassword)) }

    $options = New-Object BH.Entra.EntraOptions
    $options.OutputDirectory = if ($ResumeDirectory) { $ResumeDirectory } else { Join-Path $OutputRoot ('entra-{0}-{1}' -f $TenantId.Substring(0, [Math]::Min(8, $TenantId.Length)), (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    $options.TenantId = $TenantId
    $options.Threads = $Threads
    $options.Members = $Members
    $options.IncludeOwners = -not $SkipOwners
    $options.IncludeEligibleRoles = -not $SkipEligibleRoles
    $options.IncludeAppRoleAssignments = -not $SkipAppRoleAssignments
    $options.Compress = -not $NoCompress
    if ($Sections) { $options.Sections = New-Object 'System.Collections.Generic.HashSet[string]' (, [string[]]@($Sections | ForEach-Object { $_.ToLowerInvariant() })) }
    if (Test-Path -LiteralPath $script:EntraRoleFile) {
        foreach ($role in (Get-Content -LiteralPath $script:EntraRoleFile -Raw | ConvertFrom-Json)) { if ($role.privileged) { [void]$options.PrivilegedRoleTemplates.Add([string]$role.templateId) } }
    }

    $graph = New-Object BH.Entra.GraphClient($cloudProfile, $tokens, ($Threads * 2))
    $collector = New-Object BH.Entra.EntraCollector($graph, $options)
    Write-Host ('Entra collection: cloud {0}, tenant {1}' -f $cloudProfile.Name, $TenantId)
    Write-Host ('Output: {0}' -f $options.OutputDirectory)
    if ($ResumeDirectory) { Write-Host 'Resuming: finished sections are skipped, interrupted ones continue from their last saved page.' }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $task = $collector.Start()
    $lastLine = [DateTime]::MinValue; $lastRequests = 0L; $lastTime = 0.0
    try {
        while (-not $task.Wait(500)) {
            $p = $collector.Progress
            if (([DateTime]::UtcNow - $lastLine).TotalSeconds -lt 5) { continue }
            $lastLine = [DateTime]::UtcNow
            $now = $watch.Elapsed.TotalSeconds
            $rate = ($graph.Requests - $lastRequests) / [Math]::Max(0.1, $now - $lastTime); $lastRequests = $graph.Requests; $lastTime = $now
            if ($p.SectionTotal -gt 0) {
                $pct = [Math]::Min(100, 100.0 * $p.SectionDone / $p.SectionTotal)
                $detail = '{0:N0}/{1:N0} ({2:N0}%)' -f $p.SectionDone, $p.SectionTotal, $pct
            } else { $detail = '{0:N0} read so far' -f $p.SectionDone }
            Write-Host ('[{0}] {1}: {2} | objects {3:N0}, edges {4:N0} | {5:N1} req/s, throttled {6}, workers {7}' -f (Format-BHDuration $now), $p.Section, $detail, $p.Objects, $p.Edges, $rate, $graph.Throttles, $graph.Gate.Limit)
        }
    }
    finally {
        if (-not $task.IsCompleted) {
            Write-Host 'Stopping... finishing the current shard so the run can be resumed.' -ForegroundColor Yellow
            $collector.Cancel()
            try { [void]$task.Wait(60000) } catch { }
        }
    }
    if ($task.IsFaulted) {
        $failure = $task.Exception.GetBaseException()
        if ($failure -is [OperationCanceledException]) { Write-Host ('Cancelled. Resume with -ResumeDirectory "{0}"' -f $options.OutputDirectory); return }
        Write-Host ('Stopped: {0}' -f $failure.Message) -ForegroundColor Red
        Write-Host ('Run again with -ResumeDirectory "{0}" to continue.' -f $options.OutputDirectory)
        throw $failure
    }
    $p = $collector.Progress
    Write-Host ('Done in {0}: {1:N0} objects, {2:N0} edges, {3:N0} requests, {4} throttled.' -f (Format-BHDuration $watch.Elapsed.TotalSeconds), $p.Objects, $p.Edges, $graph.Requests, $graph.Throttles) -ForegroundColor Green
    foreach ($problem in $p.Problems) { Write-Host ('  note: {0}' -f $problem) -ForegroundColor DarkYellow }
    [pscustomobject]@{ Directory = $options.OutputDirectory; Objects = $p.Objects; Edges = $p.Edges; Requests = $graph.Requests; Throttled = $graph.Throttles; Problems = @($p.Problems) }
}

Export-ModuleMember -Function Start-BHEntraCollection
'@
Save 'src\entra-roles.json' @'
[
 {
  "name": "Agent ID Administrator",
  "templateId": "db506228-d27e-4b7d-95e5-295956d6615f",
  "privileged": true
 },
 {
  "name": "Agent ID Developer",
  "templateId": "adb2368d-a9be-41b5-8667-d96778e081b0",
  "privileged": false
 },
 {
  "name": "Agent Registry Administrator",
  "templateId": "6b942400-691f-4bf0-9d12-d8a254a2baf5",
  "privileged": false
 },
 {
  "name": "AI Administrator",
  "templateId": "d2562ede-74db-457e-a7b6-544e236ebb61",
  "privileged": true
 },
 {
  "name": "AI Reader",
  "templateId": "1fe13547-53f6-408d-ac04-7f8eed167b38",
  "privileged": true
 },
 {
  "name": "Application Administrator",
  "templateId": "9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3",
  "privileged": true
 },
 {
  "name": "Application Developer",
  "templateId": "cf1c38e5-3621-4004-a7cb-879624dced7c",
  "privileged": true
 },
 {
  "name": "Attack Payload Author",
  "templateId": "9c6df0f2-1e7c-4dc3-b195-66dfbd24aa8f",
  "privileged": false
 },
 {
  "name": "Attack Simulation Administrator",
  "templateId": "c430b396-e693-46cc-96f3-db01bf8bb62a",
  "privileged": false
 },
 {
  "name": "Attribute Assignment Administrator",
  "templateId": "58a13ea3-c632-46ae-9ee0-9c0d43cd7f3d",
  "privileged": false
 },
 {
  "name": "Attribute Assignment Reader",
  "templateId": "ffd52fa5-98dc-465c-991d-fc073eb59f8f",
  "privileged": false
 },
 {
  "name": "Attribute Definition Administrator",
  "templateId": "8424c6f0-a189-499e-bbd0-26c1753c96d4",
  "privileged": false
 },
 {
  "name": "Attribute Definition Reader",
  "templateId": "1d336d2c-4ae8-42ef-9711-b3604ce3fc2c",
  "privileged": false
 },
 {
  "name": "Attribute Log Administrator",
  "templateId": "5b784334-f94b-471a-a387-e7219fc49ca2",
  "privileged": false
 },
 {
  "name": "Attribute Log Reader",
  "templateId": "9c99539d-8186-4804-835f-fd51ef9e2dcd",
  "privileged": false
 },
 {
  "name": "Attribute Provisioning Administrator",
  "templateId": "ecb2c6bf-0ab6-418e-bd87-7986f8d63bbe",
  "privileged": true
 },
 {
  "name": "Attribute Provisioning Reader",
  "templateId": "422218e4-db15-4ef9-bbe0-8afb41546d79",
  "privileged": true
 },
 {
  "name": "Authentication Administrator",
  "templateId": "c4e39bd9-1100-46d3-8c65-fb160da0071f",
  "privileged": true
 },
 {
  "name": "Authentication Extensibility Administrator",
  "templateId": "25a516ed-2fa0-40ea-a2d0-12923a21473a",
  "privileged": true
 },
 {
  "name": "Authentication Extensibility Password Administrator",
  "templateId": "0b00bede-4072-4d22-b441-e7df02a1ef63",
  "privileged": true
 },
 {
  "name": "Authentication Policy Administrator",
  "templateId": "0526716b-113d-4c15-b2c8-68e3c22b9f80",
  "privileged": false
 },
 {
  "name": "Azure DevOps Administrator",
  "templateId": "e3973bdf-4987-49ae-837a-ba8e231c7286",
  "privileged": false
 },
 {
  "name": "Azure Information Protection Administrator",
  "templateId": "7495fdc4-34c4-4d15-a289-98788ce399fd",
  "privileged": false
 },
 {
  "name": "B2C IEF Keyset Administrator",
  "templateId": "aaf43236-0c0d-4d5f-883a-6955382ac081",
  "privileged": true
 },
 {
  "name": "B2C IEF Policy Administrator",
  "templateId": "3edaf663-341e-4475-9f94-5c398ef6c070",
  "privileged": false
 },
 {
  "name": "Billing Administrator",
  "templateId": "b0f54661-2d74-4c50-afa3-1ec803f12efe",
  "privileged": false
 },
 {
  "name": "Cloud App Security Administrator",
  "templateId": "892c5842-a9a6-463a-8041-72aa08ca3cf6",
  "privileged": false
 },
 {
  "name": "Cloud Application Administrator",
  "templateId": "158c047a-c907-4556-b7ef-446551a6b5f7",
  "privileged": true
 },
 {
  "name": "Cloud Device Administrator",
  "templateId": "7698a772-787b-4ac8-901f-60d6b08affd2",
  "privileged": true
 },
 {
  "name": "Compliance Administrator",
  "templateId": "17315797-102d-40b4-93e0-432062caca18",
  "privileged": false
 },
 {
  "name": "Compliance Data Administrator",
  "templateId": "e6d1a23a-da11-4be4-9570-befc86d067a7",
  "privileged": false
 },
 {
  "name": "Conditional Access Administrator",
  "templateId": "b1be1c3e-b65d-4f19-8427-f6fa0d97feb9",
  "privileged": true
 },
 {
  "name": "Customer Delegated Admin Relationship Administrator",
  "templateId": "fc8ad4e2-40e4-4724-8317-bcda7503ecbf",
  "privileged": false
 },
 {
  "name": "Customer Lockbox Access Approver",
  "templateId": "5c4f9dcd-47dc-4cf7-8c9a-9e4207cbfc91",
  "privileged": false
 },
 {
  "name": "Desktop Analytics Administrator",
  "templateId": "38a96431-2bdf-4b4c-8b6e-5d3d8abac1a4",
  "privileged": false
 },
 {
  "name": "Directory Readers",
  "templateId": "88d8e3e3-8f55-4a1e-953a-9b9898b8876b",
  "privileged": false
 },
 {
  "name": "Directory Synchronization Accounts",
  "templateId": "d29b2b05-8046-44ba-8758-1e26182fcf32",
  "privileged": false
 },
 {
  "name": "Directory Writers",
  "templateId": "9360feb5-f418-4baa-8175-e2a00bac4301",
  "privileged": true
 },
 {
  "name": "Domain Name Administrator",
  "templateId": "8329153b-31d0-4727-b945-745eb3bc5f31",
  "privileged": true
 },
 {
  "name": "Dragon Administrator",
  "templateId": "e93e3737-fa85-474a-aee4-7d3fb86510f3",
  "privileged": false
 },
 {
  "name": "Dynamics 365 Administrator",
  "templateId": "44367163-eba1-44c3-98af-f5787879f96a",
  "privileged": false
 },
 {
  "name": "Dynamics 365 Business Central Administrator",
  "templateId": "963797fb-eb3b-4cde-8ce3-5878b3f32a3f",
  "privileged": false
 },
 {
  "name": "Edge Administrator",
  "templateId": "3f1acade-1e04-4fbc-9b69-f0302cd84aef",
  "privileged": false
 },
 {
  "name": "Entra Backup Administrator",
  "templateId": "b6a27b2b-f905-4b2e-81b5-0d90e0ef1fdb",
  "privileged": false
 },
 {
  "name": "Entra Backup Reader",
  "templateId": "f42252d9-5400-4d7b-b9ef-cc582dbb8577",
  "privileged": false
 },
 {
  "name": "Entra SOC Identity Responder",
  "templateId": "58f930cc-fcf4-4152-852c-1d7dbf502139",
  "privileged": true
 },
 {
  "name": "Exchange Administrator",
  "templateId": "29232cdf-9323-42fd-ade2-1d097af3e4de",
  "privileged": false
 },
 {
  "name": "Exchange Backup Administrator",
  "templateId": "49eb8f75-97e9-4e37-9b2b-6c3ebfcffa31",
  "privileged": false
 },
 {
  "name": "Exchange Recipient Administrator",
  "templateId": "31392ffb-586c-42d1-9346-e59415a2cc4e",
  "privileged": false
 },
 {
  "name": "Extended Directory User Administrator",
  "templateId": "dd13091a-6207-4fc0-82ba-3641e056ab95",
  "privileged": false
 },
 {
  "name": "External ID User Flow Administrator",
  "templateId": "6e591065-9bad-43ed-90f3-e9424366d2f0",
  "privileged": false
 },
 {
  "name": "External ID User Flow Attribute Administrator",
  "templateId": "0f971eea-41eb-4569-a71e-57bb8a3eff1e",
  "privileged": false
 },
 {
  "name": "External Identity Provider Administrator",
  "templateId": "be2f45a1-457d-42af-a067-6ec1fa63bc45",
  "privileged": true
 },
 {
  "name": "Fabric Administrator",
  "templateId": "a9ea8996-122f-4c74-9520-8edcd192826c",
  "privileged": false
 },
 {
  "name": "Global Administrator",
  "templateId": "62e90394-69f5-4237-9190-012177145e10",
  "privileged": true
 },
 {
  "name": "Global Reader",
  "templateId": "f2ef992c-3afb-46b9-b7cf-a126ee74c451",
  "privileged": true
 },
 {
  "name": "Global Secure Access Administrator",
  "templateId": "ac434307-12b9-4fa1-a708-88bf58caabc1",
  "privileged": false
 },
 {
  "name": "Global Secure Access Log Reader",
  "templateId": "843318fb-79a6-4168-9e6f-aa9a07481cc4",
  "privileged": false
 },
 {
  "name": "Groups Administrator",
  "templateId": "fdd7a751-b60b-444a-984c-02652fe8fa1c",
  "privileged": false
 },
 {
  "name": "Guest Inviter",
  "templateId": "95e79109-95c0-4d8e-aee3-d01accf2d47b",
  "privileged": false
 },
 {
  "name": "Helpdesk Administrator",
  "templateId": "729827e3-9c14-49f7-bb1b-9608f156bbb8",
  "privileged": true
 },
 {
  "name": "Hybrid Identity Administrator",
  "templateId": "8ac3fc64-6eca-42ea-9e69-59f4c7b60eb2",
  "privileged": true
 },
 {
  "name": "Identity Governance Administrator",
  "templateId": "45d8d3c5-c802-45c6-b32a-1d70b5e1e86e",
  "privileged": true
 },
 {
  "name": "Insights Administrator",
  "templateId": "eb1f4a8d-243a-41f0-9fbd-c7cdf6c5ef7c",
  "privileged": false
 },
 {
  "name": "Insights Analyst",
  "templateId": "25df335f-86eb-4119-b717-0ff02de207e9",
  "privileged": false
 },
 {
  "name": "Insights Business Leader",
  "templateId": "31e939ad-9672-4796-9c2e-873181342d2d",
  "privileged": false
 },
 {
  "name": "Intune Administrator",
  "templateId": "3a2c62db-5318-420d-8d74-23affee5d9d5",
  "privileged": true
 },
 {
  "name": "IoT Device Administrator",
  "templateId": "2ea5ce4c-b2d8-4668-bd81-3680bd2d227a",
  "privileged": false
 },
 {
  "name": "Kaizala Administrator",
  "templateId": "74ef975b-6605-40af-a5d2-b9539d836353",
  "privileged": false
 },
 {
  "name": "Knowledge Administrator",
  "templateId": "b5a8dcf3-09d5-43a9-a639-8e29ef291470",
  "privileged": false
 },
 {
  "name": "Knowledge Manager",
  "templateId": "744ec460-397e-42ad-a462-8b3f9747a02c",
  "privileged": false
 },
 {
  "name": "License Administrator",
  "templateId": "4d6ac14f-3453-41d0-bef9-a3e0c569773a",
  "privileged": false
 },
 {
  "name": "Lifecycle Workflows Administrator",
  "templateId": "59d46f88-662b-457b-bceb-5c3809e5908f",
  "privileged": true
 },
 {
  "name": "Message Center Privacy Reader",
  "templateId": "ac16e43d-7b2d-40e0-ac05-243ff356ab5b",
  "privileged": false
 },
 {
  "name": "Message Center Reader",
  "templateId": "790c1fb9-7f7d-4f88-86a1-ef1f95c05c1b",
  "privileged": false
 },
 {
  "name": "Microsoft 365 Backup Administrator",
  "templateId": "1707125e-0aa2-4d4d-8655-a7c786c76a25",
  "privileged": false
 },
 {
  "name": "Microsoft 365 Migration Administrator",
  "templateId": "8c8b803f-96e1-4129-9349-20738d9f9652",
  "privileged": false
 },
 {
  "name": "Microsoft Entra Joined Device Local Administrator",
  "templateId": "9f06204d-73c1-4d4c-880a-6edb90606fd8",
  "privileged": false
 },
 {
  "name": "Microsoft Graph Data Connect Administrator",
  "templateId": "ee67aa9c-e510-4759-b906-227085a7fd4d",
  "privileged": false
 },
 {
  "name": "Microsoft Hardware Warranty Administrator",
  "templateId": "1501b917-7653-4ff9-a4b5-203eaf33784f",
  "privileged": false
 },
 {
  "name": "Microsoft Hardware Warranty Specialist",
  "templateId": "281fe777-fb20-4fbb-b7a3-ccebce5b0d96",
  "privileged": false
 },
 {
  "name": "Network Administrator",
  "templateId": "d37c8bed-0711-4417-ba38-b4abe66ce4c2",
  "privileged": false
 },
 {
  "name": "Office Apps Administrator",
  "templateId": "2b745bdf-0803-4d80-aa65-822c4493daac",
  "privileged": false
 },
 {
  "name": "Organizational Branding Administrator",
  "templateId": "92ed04bf-c94a-4b82-9729-b799a7a4c178",
  "privileged": false
 },
 {
  "name": "Organizational Data Source Administrator",
  "templateId": "9d70768a-0cbc-4b4c-aea3-2e124b2477f4",
  "privileged": false
 },
 {
  "name": "Organizational Messages Approver",
  "templateId": "e48398e2-f4bb-4074-8f31-4586725e205b",
  "privileged": false
 },
 {
  "name": "Organizational Messages Writer",
  "templateId": "507f53e4-4e52-4077-abd3-d2e1558b6ea2",
  "privileged": false
 },
 {
  "name": "Partner Tier1 Support",
  "templateId": "4ba39ca4-527c-499a-b93d-d9b492c50246",
  "privileged": true
 },
 {
  "name": "Partner Tier2 Support",
  "templateId": "e00e864a-17c5-4a4b-9c06-f5b95a8d5bd8",
  "privileged": true
 },
 {
  "name": "Password Administrator",
  "templateId": "966707d0-3269-4727-9be2-8c3a10f19b9d",
  "privileged": true
 },
 {
  "name": "People Administrator",
  "templateId": "024906de-61e5-49c8-8572-40335f1e0e10",
  "privileged": false
 },
 {
  "name": "Permissions Management Administrator",
  "templateId": "af78dc32-cf4d-46f9-ba4e-4428526346b5",
  "privileged": false
 },
 {
  "name": "Places Administrator",
  "templateId": "78b0ccd1-afc2-4f92-9116-b41aedd09592",
  "privileged": false
 },
 {
  "name": "Power Platform Administrator",
  "templateId": "11648597-926c-4cf3-9c36-bcebb0ba8dcc",
  "privileged": false
 },
 {
  "name": "Printer Administrator",
  "templateId": "644ef478-e28f-4e28-b9dc-3fdde9aa0b1f",
  "privileged": false
 },
 {
  "name": "Printer Technician",
  "templateId": "e8cef6f1-e4bd-4ea8-bc07-4b8d950f4477",
  "privileged": false
 },
 {
  "name": "Privileged Authentication Administrator",
  "templateId": "7be44c8a-adaf-4e2a-84d6-ab2649e08a13",
  "privileged": true
 },
 {
  "name": "Privileged Role Administrator",
  "templateId": "e8611ab8-c189-46e8-94e1-60213ab1f814",
  "privileged": true
 },
 {
  "name": "Purview Workload Content Administrator",
  "templateId": "3f04f91a-4ad7-4bd3-bcfa-49882ea1a88a",
  "privileged": false
 },
 {
  "name": "Purview Workload Content Reader",
  "templateId": "e07494ad-1654-4dd2-922e-6f81a71bf00f",
  "privileged": false
 },
 {
  "name": "Purview Workload Content Writer",
  "templateId": "02d5655b-c1cf-4e5f-98da-5fb919085bf6",
  "privileged": false
 },
 {
  "name": "Reports Reader",
  "templateId": "4a5d8f65-41da-4de4-8968-e035b65339cf",
  "privileged": false
 },
 {
  "name": "Resource Policy Administrator",
  "templateId": "c0c46a03-c257-4f91-ab71-3c7e59c89d97",
  "privileged": false
 },
 {
  "name": "Resource Policy Evaluator",
  "templateId": "1dae4b79-8aea-40ab-9ad0-30d3ab1fa9c1",
  "privileged": false
 },
 {
  "name": "Resource Policy Reader",
  "templateId": "311877b7-9a2e-4337-81c2-068a250eaa22",
  "privileged": false
 },
 {
  "name": "Search Administrator",
  "templateId": "0964bb5e-9bdb-4d7b-ac29-58e794862a40",
  "privileged": false
 },
 {
  "name": "Search Editor",
  "templateId": "8835291a-918c-4fd7-a9ce-faa49f0cf7d9",
  "privileged": false
 },
 {
  "name": "Security Administrator",
  "templateId": "194ae4cb-b126-40b2-bd5b-6091b380977d",
  "privileged": true
 },
 {
  "name": "Security Operator",
  "templateId": "5f2222b1-57c3-48ba-8ad5-d4759f1fde6f",
  "privileged": true
 },
 {
  "name": "Security Reader",
  "templateId": "5d6b6bb7-de71-4623-b4af-96380a352509",
  "privileged": true
 },
 {
  "name": "Service Support Administrator",
  "templateId": "f023fd81-a637-4b56-95fd-791ac0226033",
  "privileged": false
 },
 {
  "name": "SharePoint Administrator",
  "templateId": "f28a1f50-f6e7-4571-818b-6a12f2af6b6c",
  "privileged": false
 },
 {
  "name": "SharePoint Advanced Management Administrator",
  "templateId": "99009c4a-3b3f-4957-82a9-9d35e12db77e",
  "privileged": false
 },
 {
  "name": "SharePoint Backup Administrator",
  "templateId": "9d3e04ba-3ee4-4d1b-a3a7-9aef423a09be",
  "privileged": false
 },
 {
  "name": "SharePoint Embedded Administrator",
  "templateId": "1a7d78b6-429f-476b-b8eb-35fb715fffd4",
  "privileged": false
 },
 {
  "name": "Skype for Business Administrator",
  "templateId": "75941009-915a-4869-abe7-691bff18279e",
  "privileged": false
 },
 {
  "name": "Teams Administrator",
  "templateId": "69091246-20e8-4a56-aa4d-066075b2a7a8",
  "privileged": false
 },
 {
  "name": "Teams Communications Administrator",
  "templateId": "baf37b3a-610e-45da-9e62-d9d1e5e8914b",
  "privileged": false
 },
 {
  "name": "Teams Communications Support Engineer",
  "templateId": "f70938a0-fc10-4177-9e90-2178f8765737",
  "privileged": false
 },
 {
  "name": "Teams Communications Support Specialist",
  "templateId": "fcf91098-03e3-41a9-b5ba-6f0ec8188a12",
  "privileged": false
 },
 {
  "name": "Teams Devices Administrator",
  "templateId": "3d762c5a-1b6c-493f-843e-55a3b42923d4",
  "privileged": false
 },
 {
  "name": "Teams External Collaboration Administrator",
  "templateId": "2fe872fb-daa8-4afc-8f6c-53c4565cfef4",
  "privileged": false
 },
 {
  "name": "Teams Reader",
  "templateId": "1076ac91-f3d9-41a7-a339-dcdf5f480acc",
  "privileged": false
 },
 {
  "name": "Teams Telephony Administrator",
  "templateId": "aa38014f-0993-46e9-9b45-30501a20909d",
  "privileged": false
 },
 {
  "name": "Tenant Creator",
  "templateId": "112ca1a2-15ad-4102-995e-45b0bc479a6a",
  "privileged": false
 },
 {
  "name": "Tenant Governance Administrator",
  "templateId": "1981f584-96e9-4a6f-95b0-f522373f8fae",
  "privileged": true
 },
 {
  "name": "Tenant Governance Reader",
  "templateId": "e0a4caa6-fe82-443f-b92f-d87341d17b2e",
  "privileged": false
 },
 {
  "name": "Tenant Governance Relationship Administrator",
  "templateId": "b8e31d83-1534-480f-9b10-0338ded51b7e",
  "privileged": false
 },
 {
  "name": "Tenant Governance Relationship Reader",
  "templateId": "124577f8-48ed-456a-839f-13b419002e33",
  "privileged": false
 },
 {
  "name": "Usage Summary Reports Reader",
  "templateId": "75934031-6c7e-415a-99d7-48dbd49e875e",
  "privileged": false
 },
 {
  "name": "User Administrator",
  "templateId": "fe930be7-5e62-47db-91af-98c3a49a38b1",
  "privileged": true
 },
 {
  "name": "User Experience Success Manager",
  "templateId": "27460883-1df1-4691-b032-3b79643e5e63",
  "privileged": false
 },
 {
  "name": "Virtual Visits Administrator",
  "templateId": "e300d9e7-4a2b-4295-9eff-f1c78b36cc98",
  "privileged": false
 },
 {
  "name": "Viva Glint Tenant Administrator",
  "templateId": "0ec3f692-38d6-4d14-9e69-0377ca7797ad",
  "privileged": false
 },
 {
  "name": "Viva Goals Administrator",
  "templateId": "92b086b3-e367-4ef2-b869-1de128fb986e",
  "privileged": false
 },
 {
  "name": "Viva Pulse Administrator",
  "templateId": "87761b17-1ed2-4af3-9acd-92a150038160",
  "privileged": false
 },
 {
  "name": "Windows 365 Administrator",
  "templateId": "11451d60-acb2-45eb-a7d6-43d0f0125c13",
  "privileged": false
 },
 {
  "name": "Windows Update Deployment Administrator",
  "templateId": "32696413-001a-46ae-978c-ce0f6b3620d2",
  "privileged": false
 },
 {
  "name": "Yammer Administrator",
  "templateId": "810a2642-a034-447f-a5e8-41beaa378541",
  "privileged": false
 }
]
'@
Save 'Collect-Entra.ps1' @'
# Collects an Entra ID tenant (same parameters as Start-BHEntraCollection). Examples:
#   .\Collect-Entra.ps1 -TenantId <guid> -ClientId <guid> -CertificateThumbprint <thumbprint>
#   .\Collect-Entra.ps1 -TenantId <guid> -DeviceCode            (sign in as a Global Reader)
#   .\Collect-Entra.ps1 -TenantId <guid> -ResumeDirectory .\entra-scans\entra-xxxx   (continue a stopped run)
# Defaults to USGovDoD. Add -Cloud USGov for GCC High or -Cloud Global for commercial.
[CmdletBinding()]
param(
    [ValidateSet('USGovDoD', 'USGov', 'Global', 'China')][string]$Cloud = 'USGovDoD',
    [Parameter(Mandatory)][string]$TenantId,
    [string]$ClientId,
    [string]$CertificateThumbprint,
    [string]$PfxPath,
    [securestring]$PfxPassword,
    [securestring]$ClientSecret,
    [switch]$DeviceCode,
    [securestring]$AccessToken,
    [string]$OutputRoot = (Join-Path (Get-Location) 'entra-scans'),
    [string]$ResumeDirectory,
    [ValidateRange(1, 64)][int]$Threads = 16,
    [ValidateSet('All', 'RoleAssignable', 'None')][string]$Members = 'All',
    [switch]$SkipOwners,
    [switch]$SkipEligibleRoles,
    [switch]$SkipAppRoleAssignments,
    [string[]]$Sections,
    [switch]$NoCompress,
    [string]$GraphUrl,
    [string]$LoginUrl
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'src\BH.Entra.psm1') -Force -DisableNameChecking
Start-BHEntraCollection @PSBoundParameters
'@
Save 'ENTRA.md' @'
# Entra ID collector

Run from this folder in Windows PowerShell 5.1 or PowerShell 7:

    .\Collect-Entra.ps1 -TenantId <tenant guid> -ClientId <app guid> -CertificateThumbprint <thumbprint>
    .\Collect-Entra.ps1 -TenantId <tenant guid> -DeviceCode
    .\Collect-Entra.ps1 -TenantId <tenant guid> -ResumeDirectory .\entra-scans\entra-xxxxxxxx-yyyymmdd-hhmmss

Cloud defaults to USGovDoD (dod-graph.microsoft.us, login.microsoftonline.us). Use `-Cloud USGov` for GCC High, `-Cloud Global` for commercial.

## Access
Either a read only app registration (Microsoft Graph application permissions, admin consent):
Directory.Read.All, RoleManagement.Read.Directory, Application.Read.All.
Optional: RoleEligibilitySchedule.Read.Directory (eligible roles, needs Entra ID P2).
Or `-DeviceCode`, where a Global Reader signs in; nothing to configure.

## What it collects
tenant, users, groups, service principals, applications, role definitions, active and eligible role assignments,
group members, owners (groups, apps, service principals), app role assignments (with dangerous Graph permissions flagged),
OAuth consent grants.

## Large tenants
* Lists stream into gzip shards of 10 pages; the paging cursor is saved after each shard.
* Fan-out calls (members, owners, app role assignments) use $batch, 20 per call, with a worker pool that halves on HTTP 429 and grows back on success. Retry-After is honored.
* Ctrl+C or a failure leaves a resumable folder. Finished sections are skipped.
* A section that is forbidden or unlicensed is noted in `entra-manifest.json` and skipped, never fatal.

Output shape matches the AD collector: `nodes_<kind>.*.ndjson.gz` and `edges_<bucket>.*.ndjson.gz` (memberof, acl, access, observed).
Indexing, analysis and viewer support for these Entra kinds is the next milestone.
'@

# Register the Entra source in the shared native compile (BH.Core.psm1).
$bytes = [IO.File]::ReadAllBytes($core)
$bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
$text = [IO.File]::ReadAllText($core)
if ($text.Contains('BH.Entra.Native.cs')) { 'BH.Core.psm1 already registers the Entra collector.' }
else {
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    function Count-Of($h, $n) { ($h.Split([string[]]@($n), [StringSplitOptions]::None).Count) - 1 }
    $edits = @(
        @(("        (Join-Path `$PSScriptRoot 'BH.Deep.Native.cs')" + $nl + "    )"), ("        (Join-Path `$PSScriptRoot 'BH.Deep.Native.cs')," + $nl + "        (Join-Path `$PSScriptRoot 'BH.Entra.Native.cs')" + $nl + "    )")),
        @("'BH.Native.ExposureAnalysis', 'BH.Native.DiskAnalysisProgress'", "'BH.Native.ExposureAnalysis', 'BH.Native.DiskAnalysisProgress', 'BH.Entra.EntraCollector'"),
        @("'System.DirectoryServices.Protocols.dll' -ErrorAction Stop", "'System.DirectoryServices.Protocols.dll','System.Net.Http.dll' -ErrorAction Stop")
    )
    foreach ($e in $edits) {
        $n = Count-Of $text $e[0]
        if ($n -ne 1) { throw "BH.Core.psm1 is not the expected version (expected 1 match, found $n for: $($e[0].Trim())). The Entra files were written, but BH.Core.psm1 was NOT changed." }
    }
    Copy-Item $core "$core.entra.bak" -Force
    foreach ($e in $edits) { $text = $text.Replace($e[0], $e[1]) }
    [IO.File]::WriteAllText($core, $text, (New-Object System.Text.UTF8Encoding($bom)))
    'patched src\BH.Core.psm1 (backup: src\BH.Core.psm1.entra.bak)'
}
''
'Done. Open a NEW PowerShell window, cd to the folder, and run:  .\Collect-Entra.ps1 -TenantId <guid> -DeviceCode'
