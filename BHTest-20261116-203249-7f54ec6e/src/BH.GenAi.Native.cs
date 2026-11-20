// BH.GenAi.Native.cs  -  optional GenAI.mil assistant for the desktop viewer.
// Explains the path or route on screen and suggests fixes. Nothing is sent until the user presses Send, the API key is typed
// each time and kept only in memory, and by default object names are replaced with neutral tokens before anything leaves
// the computer (the answer is translated back locally). Plain C# 5; uses only System.Net, so no extra references.
//
// This file is inserted into the viewer's C# inside "namespace BH.Native { ... }". The part above the "window" marker has no
// WinForms dependency and is tested separately.

    // ---- client, prompt builder (no WinForms)
    internal sealed class GenAiResult
    {
        public string Text = "";
        public string Error;
        public int Status;
        public int PromptTokens, CompletionTokens;
    }

    internal static class GenAiJson
    {
        public static object Parse(string s) { int i = 0; return Value(s, ref i); }
        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[i];
            if (c == '{')
            {
                var map = new Dictionary<string, object>(); i++; Skip(s, ref i);
                if (s[i] == '}') { i++; return map; }
                while (true)
                {
                    Skip(s, ref i); string key = Str(s, ref i); Skip(s, ref i);
                    if (s[i] != ':') throw new FormatException("Expected ':' in JSON.");
                    i++; map[key] = Value(s, ref i); Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return map; }
                    throw new FormatException("Expected ',' or '}' in JSON.");
                }
            }
            if (c == '[')
            {
                var list = new List<object>(); i++; Skip(s, ref i);
                if (s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i)); Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException("Expected ',' or ']' in JSON.");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException("Unexpected character in JSON.");
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }
        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string in JSON.");
            i++; var sb = new StringBuilder();
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
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static object Get(object o, string key) { var m = o as Dictionary<string, object>; object v; return m != null && m.TryGetValue(key, out v) ? v : null; }
        public static object At(object o, int index) { var l = o as List<object>; return l != null && index < l.Count ? l[index] : null; }
        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }

    /// <summary>Calls the OpenAI-compatible chat endpoint. Streams when the service streams, reads one JSON body otherwise.</summary>
    internal sealed class GenAiClient
    {
        // Tier 0 limits: 60 requests per minute, 200,000 tokens per minute, 2,000,000 credits per 24 hours.
        public const int MinimumMillisecondsBetweenRequests = 1100;
        private static readonly object pace = new object();
        private static DateTime lastRequest = DateTime.MinValue;
        private readonly string baseUrl, key;
        private readonly System.Net.Http.HttpClient http;
        private readonly CancellationTokenSource cancelSource = new CancellationTokenSource();

        public GenAiClient(string baseUrl, string apiKey)
        {
            this.baseUrl = (baseUrl ?? "").Trim().TrimEnd('/'); key = apiKey;
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; } catch (Exception) { }
            http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(180);
        }

        public void Cancel() { try { cancelSource.Cancel(); } catch (Exception) { } }

        private void Pace()
        {
            lock (pace)
            {
                double wait = MinimumMillisecondsBetweenRequests - (DateTime.UtcNow - lastRequest).TotalMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                lastRequest = DateTime.UtcNow;
            }
        }

        private System.Net.Http.HttpResponseMessage Send(string path, bool post, string body)
        {
            if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("http://127.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The service address must start with https://.");
            var request = new System.Net.Http.HttpRequestMessage(post ? System.Net.Http.HttpMethod.Post : System.Net.Http.HttpMethod.Get, baseUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            request.Headers.TryAddWithoutValidation("User-Agent", "Tessera-viewer");
            if (body != null) request.Content = new System.Net.Http.StringContent(body, new UTF8Encoding(false), "application/json");
            Pace();
            return http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancelSource.Token).GetAwaiter().GetResult();
        }

        private static string ErrorText(System.Net.Http.HttpResponseMessage response)
        {
            int status = (int)response.StatusCode; string body = "";
            try { body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch (Exception) { }
            if (body.Length > 400) body = body.Substring(0, 400);
            if (status == 401 || status == 403) return "The service rejected the API key (HTTP " + status + "). Check the key and that it is active.";
            if (status == 429)
            {
                string after = RetryAfterSeconds(response) > 0 ? RetryAfterSeconds(response).ToString(CultureInfo.InvariantCulture) : null;
                return "Rate limit reached (HTTP 429). Tier 0 allows 60 requests and 200,000 tokens per minute and 2,000,000 credits per day." + (after == null ? "" : " Retry after " + after + " seconds.");
            }
            return "HTTP " + status + ": " + body;
        }

        private static int RetryAfterSeconds(System.Net.Http.HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header != null && header.Delta.HasValue) return (int)Math.Ceiling(header.Delta.Value.TotalSeconds);
            return 0;
        }

        public List<string> ListModels(out string error)
        {
            error = null; var ids = new List<string>();
            try
            {
                using (var response = Send("/v1/models", false, null))
                {
                    if (!response.IsSuccessStatusCode) { error = ErrorText(response); return ids; }
                    var list = GenAiJson.Get(GenAiJson.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()), "data") as List<object>;
                    if (list != null) foreach (object item in list) { string id = GenAiJson.Get(item, "id") as string; if (id != null) ids.Add(id); }
                }
            }
            catch (OperationCanceledException) { error = "Cancelled."; }
            catch (Exception ex) { error = "Could not reach the service: " + Inner(ex); }
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }

        private static string Inner(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex.Message; }

        public GenAiResult Chat(string model, string system, string user, int maxTokens, Action<string> onText)
        {
            var turns = new List<string[]>();
            turns.Add(new string[] { "system", system }); turns.Add(new string[] { "user", user });
            return ChatMessages(model, turns, maxTokens, onText);
        }

        /// <summary>One request carrying the whole conversation so far. Each turn is { role, content }.</summary>
        public GenAiResult ChatMessages(string model, List<string[]> turns, int maxTokens, Action<string> onText)
        {
            var result = new GenAiResult();
            var built = new StringBuilder(",\"messages\":[");
            for (int t = 0; t < turns.Count; t++)
            {
                if (t > 0) built.Append(',');
                built.Append("{\"role\":").Append(GenAiJson.Quote(turns[t][0])).Append(",\"content\":").Append(GenAiJson.Quote(turns[t][1])).Append('}');
            }
            string messages = built.Append("]}").ToString();
            string streamBody = "{\"model\":" + GenAiJson.Quote(model) + ",\"stream\":true,\"stream_options\":{\"include_usage\":true},\"max_tokens\":" + maxTokens + messages;
            string plainBody = "{\"model\":" + GenAiJson.Quote(model) + ",\"max_tokens\":" + maxTokens + messages;
            string body = streamBody;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var response = Send("/v1/chat/completions", true, body))
                    {
                        result.Status = (int)response.StatusCode;
                        if (!response.IsSuccessStatusCode)
                        {
                            result.Error = ErrorText(response);
                            // A service that does not accept the streaming options gets one plain request.
                            if (result.Status == 400 && body == streamBody) { body = plainBody; result.Error = null; continue; }
                            int seconds = RetryAfterSeconds(response);
                            if (result.Status == 429 && attempt == 0 && seconds > 0 && seconds <= 20) { Thread.Sleep(seconds * 1000); result.Error = null; continue; }
                            return result;
                        }
                        string type = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.ToString();
                        using (var reader = new StreamReader(response.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), Encoding.UTF8))
                        {
                            if (type.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0) ReadStream(reader, result, onText);
                            else ReadBody(reader.ReadToEnd(), result, onText);
                        }
                    }
                    return result;
                }
                catch (OperationCanceledException) { result.Error = "Cancelled."; return result; }
                catch (Exception ex) { result.Error = "Could not reach the service: " + Inner(ex); return result; }
            }
            return result;
        }

        private static void ReadStream(StreamReader reader, GenAiResult result, Action<string> onText)
        {
            var text = new StringBuilder(); string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string payload = line.Substring(5).Trim();
                if (payload.Length == 0) continue;
                if (payload == "[DONE]") break;
                object chunk;
                try { chunk = GenAiJson.Parse(payload); } catch (FormatException) { continue; }
                object error = GenAiJson.Get(chunk, "error");
                if (error != null) { result.Error = (GenAiJson.Get(error, "message") as string) ?? "The service reported an error."; break; }
                object usage = GenAiJson.Get(chunk, "usage");
                if (usage != null) ReadUsage(usage, result);
                object delta = GenAiJson.Get(GenAiJson.At(GenAiJson.Get(chunk, "choices"), 0), "delta");
                string piece = GenAiJson.Get(delta, "content") as string;
                if (!string.IsNullOrEmpty(piece)) { text.Append(piece); if (onText != null) onText(piece); }
            }
            result.Text = text.ToString();
        }

        private static void ReadBody(string body, GenAiResult result, Action<string> onText)
        {
            object doc = GenAiJson.Parse(body);
            object error = GenAiJson.Get(doc, "error");
            if (error != null) { result.Error = (GenAiJson.Get(error, "message") as string) ?? "The service reported an error."; return; }
            object usage = GenAiJson.Get(doc, "usage");
            if (usage != null) ReadUsage(usage, result);
            string content = GenAiJson.Get(GenAiJson.Get(GenAiJson.At(GenAiJson.Get(doc, "choices"), 0), "message"), "content") as string;
            result.Text = content ?? "";
            if (onText != null && result.Text.Length > 0) onText(result.Text);
        }

        private static void ReadUsage(object usage, GenAiResult result)
        {
            object p = GenAiJson.Get(usage, "prompt_tokens"), c = GenAiJson.Get(usage, "completion_tokens");
            if (p is double) result.PromptTokens = (int)(double)p;
            if (c is double) result.CompletionTokens = (int)(double)c;
        }
    }

    /// <summary>Model lanes from the GenAI.mil capability matrix.</summary>
    internal static class GenAiModels
    {
        public sealed class Info { public string Id, Lane; public int In, Out; }
        public static readonly Info[] Known = new Info[] {
            new Info { Id = "gemini-3.8-flash", Lane = "Assist", In = 0, Out = 0 },
            new Info { Id = "gemini-3.7-flash", Lane = "Assist", In = 0, Out = 0 },
            new Info { Id = "gemini-3.5-flash", Lane = "Assist", In = 0, Out = 0 },
            new Info { Id = "gemini-4-argon", Lane = "Assist", In = 0, Out = 0 },
            new Info { Id = "gemini-3.1-flash-lite-withtools", Lane = "Standard", In = 1, Out = 1 },
            new Info { Id = "gemini-3.5-flash-lite-withtools", Lane = "Standard", In = 1, Out = 1 },
            new Info { Id = "gemini-3.5-flash-withtools", Lane = "Standard", In = 1, Out = 1 },
            new Info { Id = "gemini-3.7-flash-withtools", Lane = "Standard", In = 1, Out = 1 },
            new Info { Id = "gemini-3.8-flash-withtools", Lane = "Standard", In = 1, Out = 2 },
            new Info { Id = "gemini-4-argon-withtools", Lane = "Standard", In = 1, Out = 2 }
        };
        public static Info Find(string id)
        {
            foreach (Info info in Known) if (string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase)) return info;
            return null;
        }
        public static string Describe(string id)
        {
            Info info = Find(id);
            if (info == null) return "Unknown model: cost not estimated here. Embedding models cannot be used for this.";
            if (info.Lane == "Assist") return "Assist lane: 0 credits (free), still subject to the request and token limits. Tool definitions are ignored, which this does not need.";
            return "Standard lane: " + info.In + "x per prompt token, " + info.Out + "x per answer token, drawn from the 2,000,000 credits per 24 hours.";
        }
        public static long Credits(string id, int promptTokens, int completionTokens)
        {
            Info info = Find(id);
            return info == null ? 0 : (long)promptTokens * info.In + (long)completionTokens * info.Out;
        }
    }

    /// <summary>Builds the question from the relationships on screen. Names can be replaced by neutral tokens.</summary>
    internal sealed class GenAiPrompt
    {
        public const int MaxSteps = 60;
        public string System, User;
        public bool Truncated;
        public int Steps;
        private readonly Dictionary<string, string> tokenToName = new Dictionary<string, string>(StringComparer.Ordinal);

        public static readonly string[] Tasks = new string[] { "Explain the path and recommend fixes", "Explain the path only", "Recommend fixes only" };

        private static string KindWord(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "users": return "USER"; case "groups": return "GROUP"; case "computers": return "COMPUTER"; case "gpos": return "GPO";
                case "ous": return "OU"; case "domains": return "DOMAIN"; case "certtemplates": return "CERT-TEMPLATE"; case "enterprisecas": return "CA";
                default: return "OBJECT";
            }
        }

        private static string Clip(string text, int max)
        {
            if (text == null) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        public static GenAiPrompt Build(List<Dictionary<string, object>> edges, bool hideNames, int task, Func<string, string[]> glossary)
        {
            var p = new GenAiPrompt();
            var tokenOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var counters = new Dictionary<string, int>();
            Func<Dictionary<string, object>, string, string> label = delegate(Dictionary<string, object> edge, string side)
            {
                string id = Get(edge, side), name = Get(edge, side + "n"), kind = Get(edge, side + "k");
                string display = name.Length > 0 ? name : id;
                if (!hideNames) return display;
                string token;
                if (!tokenOf.TryGetValue(id, out token))
                {
                    string word = KindWord(kind); int n; counters.TryGetValue(word, out n); n++; counters[word] = n;
                    token = word + "-" + n; tokenOf[id] = token; p.tokenToName[token] = display;
                }
                return token;
            };
            var types = new List<string>();
            var lines = new StringBuilder();
            int count = 0;
            foreach (var edge in edges)
            {
                if (Get(edge, "ty").Length == 0) continue;
                if (count >= MaxSteps) { p.Truncated = true; break; }
                count++;
                string type = Get(edge, "ty");
                if (!types.Contains(type)) types.Add(type);
                string sk = Get(edge, "sk"), tk = Get(edge, "tk");
                lines.Append(count).Append(". ").Append(label(edge, "s")).Append(sk.Length > 0 ? " (" + KindWord(sk).ToLowerInvariant() + ")" : "")
                    .Append("  --[").Append(type).Append("]-->  ").Append(label(edge, "t")).Append(tk.Length > 0 ? " (" + KindWord(tk).ToLowerInvariant() + ")" : "").Append('\n');
            }
            p.Steps = count;
            var glossaryText = new StringBuilder();
            foreach (string type in types)
            {
                string[] h = glossary == null ? null : glossary(type);
                if (h == null || h.Length < 7) { glossaryText.Append("- ").Append(type).Append('\n'); continue; }
                glossaryText.Append("- ").Append(type).Append(": ").Append(Clip(h[1], 260)).Append(" Impact: ").Append(Clip(h[3], 260)).Append(" Usual fix: ").Append(Clip(h[6], 260)).Append('\n');
            }
            // task: 0 = both, 1 = explain only, 2 = recommend only
            string want = task == 1 ? "Explain this path." : task == 2 ? "Recommend how to fix it." : "Explain this path, then recommend how to fix it.";
            var user = new StringBuilder();
            user.Append("Directory attack path from an authorized internal security review, source first, target last. ")
                .Append(hideNames ? "Object names are replaced by tokens such as USER-1; treat each token as an opaque name and use the same token in your answer.\n\n" : "\n\n");
            user.Append("Relationships (").Append(count).Append(p.Truncated ? ", list shortened to the first " + MaxSteps : "").Append("):\n").Append(lines).Append('\n');
            if (glossaryText.Length > 0) user.Append("What each relationship type means in this tool:\n").Append(glossaryText).Append('\n');
            user.Append("Evidence, permissions masks and original object details are not included. ").Append(want);
            p.User = user.ToString();
            p.System = "You are a defensive Active Directory and Entra ID security analyst helping an administrator who owns the environment. " +
                "Use only the relationships supplied. Do not invent objects, permissions or facts, and say plainly when something cannot be known from this data. " +
                "Format with simple Markdown that is easy to read: a short heading per section (## SUMMARY), numbered or bulleted lists, **bold** for object names and relationship names. Never use tables; use bulleted lists instead. Never use emojis, icons or special symbols; use plain punctuation only. Sections, in order, only for what was asked: " +
                "SUMMARY (two sentences); WHY IT MATTERS (what an attacker who controls the first object could reach); STEP BY STEP (one short line per relationship); " +
                "RECOMMENDED FIXES (numbered, safest and highest impact first, each naming the exact relationship to change and the least disruptive change); " +
                "HOW TO VERIFY AND ROLL BACK; CONFIDENCE AND LIMITS. " +
                "Give defensive remediation only. Do not provide exploitation commands or attack tooling. " +
                "This is a one-way report: do not ask the user any question, do not offer further help or next steps beyond the fixes themselves, and stop when the content is complete. When the user later asks a follow-up question, answer it directly and briefly in the same style.";
            return p;
        }

        private static string Get(Dictionary<string, object> row, string key)
        {
            object v; return row.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        /// <summary>Swaps real names the user typed for their tokens, so a follow-up question does not leak names.</summary>
        public string Hide(string text)
        {
            if (tokenToName.Count == 0 || string.IsNullOrEmpty(text)) return text;
            var pairs = new List<KeyValuePair<string, string>>(tokenToName);
            pairs.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return b.Value.Length.CompareTo(a.Value.Length); });
            foreach (var pair in pairs)
            {
                if (pair.Value.Length < 2) continue;
                var sb = new StringBuilder(); int i = 0;
                while (i < text.Length)
                {
                    int at = text.IndexOf(pair.Value, i, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) { sb.Append(text, i, text.Length - i); break; }
                    sb.Append(text, i, at - i).Append(pair.Key); i = at + pair.Value.Length;
                }
                text = sb.ToString();
            }
            return text;
        }

        /// <summary>Puts the real names back into text that came back with tokens.</summary>
        public string Restore(string text)
        {
            if (tokenToName.Count == 0 || string.IsNullOrEmpty(text)) return text;
            var tokens = new List<string>(tokenToName.Keys);
            tokens.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string token in tokens)
            {
                // Replace whole tokens only, so USER-1 does not hit inside USER-12.
                var sb = new StringBuilder(); int i = 0;
                while (i < text.Length)
                {
                    int at = text.IndexOf(token, i, StringComparison.Ordinal);
                    if (at < 0) { sb.Append(text, i, text.Length - i); break; }
                    bool before = at == 0 || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '-');
                    int end = at + token.Length;
                    bool after = end >= text.Length || !(char.IsDigit(text[end]) || char.IsLetter(text[end]));
                    sb.Append(text, i, at - i);
                    sb.Append(before && after ? tokenToName[token] : token);
                    i = end;
                }
                text = sb.ToString();
            }
            return text;
        }
    }

    /// <summary>Turns the Markdown an AI model returns into RTF for a RichTextBox: headings, bold, italic, code, lists, quotes and tables.</summary>
    internal static class GenAiMarkdown
    {
        private const string Header = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}{\\f1 Consolas;}}" +
            "{\\colortbl ;\\red237\\green243\\blue252;\\red121\\green179\\blue255;\\red164\\green180\\blue202;\\red246\\green199\\blue119;\\red31\\green50\\blue77;}\\f0\\fs21\\cf1 ";

        public static string ToRtf(string markdown)
        {
            var o = new StringBuilder(Header);
            string[] lines = Clean(markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool code = false, blankPending = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i], trimmed = line.Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal)) { code = !code; blankPending = false; continue; }
                if (code) { o.Append("\\pard\\li240\\sa0{\\f1\\fs19\\cf4\\highlight5 ").Append(Esc(line.Length == 0 ? " " : line)).Append("}\\par\n"); continue; }
                if (trimmed.Length == 0) { blankPending = true; continue; }
                if (blankPending) { o.Append("\\pard\\sa0\\fs8\\par\\fs21\n"); blankPending = false; }

                int hashes = 0; while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
                if (hashes > 0 && hashes <= 6 && trimmed.Length > hashes && trimmed[hashes] == ' ')
                {
                    int size = hashes == 1 ? 30 : hashes == 2 ? 26 : hashes == 3 ? 23 : 21;
                    o.Append("\\pard\\sb140\\sa60{\\b\\fs").Append(size).Append("\\cf2 "); Inline(trimmed.Substring(hashes + 1).Trim(), o); o.Append("}\\par\n");
                    continue;
                }
                if (IsRule(trimmed)) { o.Append("\\pard\\sb60\\sa60{\\f1\\fs14\\cf3 ").Append(new string('-', 70)).Append("}\\par\n"); continue; }
                if (trimmed.StartsWith("|", StringComparison.Ordinal) && trimmed.EndsWith("|", StringComparison.Ordinal))
                {
                    var rows = new List<string[]>();
                    int j = i;
                    for (; j < lines.Length; j++)
                    {
                        string t = lines[j].Trim();
                        if (!(t.StartsWith("|", StringComparison.Ordinal) && t.EndsWith("|", StringComparison.Ordinal))) break;
                        string[] cells = t.Substring(1, t.Length - 2).Split('|');
                        bool separator = true;
                        for (int c = 0; c < cells.Length; c++) { cells[c] = Plain(cells[c].Trim()); if (cells[c].Replace("-", "").Replace(":", "").Trim().Length > 0) separator = false; }
                        if (!separator) rows.Add(cells);
                    }
                    i = j - 1;
                    // Tables are hard to read in a text box, so each row becomes a bullet with its columns as sub-bullets.
                    string[] header = rows.Count > 0 ? rows[0] : new string[0];
                    for (int r = 1; r < rows.Count; r++)
                    {
                        string first = rows[r].Length > 0 ? rows[r][0] : "";
                        o.Append("\\pard\\tx380\\li380\\fi-260\\sa30\\u8226?\\tab {\\b ").Append(Esc(first.Length == 0 ? "(item)" : first)).Append("}\\par\n");
                        for (int c = 1; c < rows[r].Length; c++)
                        {
                            if (rows[r][c].Length == 0) continue;
                            string label = c < header.Length ? header[c] : "";
                            o.Append("\\pard\\tx740\\li740\\fi-260\\sa20\\u8226?\\tab ");
                            if (label.Length > 0) o.Append("{\\i ").Append(Esc(label)).Append(":} ");
                            o.Append(Esc(rows[r][c])).Append("\\par\n");
                        }
                    }
                    if (rows.Count == 1)
                    {
                        string joined = string.Join("; ", rows[0]);
                        o.Append("\\pard\\tx380\\li380\\fi-260\\sa30\\u8226?\\tab ").Append(Esc(joined)).Append("\\par\n");
                    }
                    o.Append("\\pard\\sa60\\par\n");
                    continue;
                }
                int indent = 0; while (indent < line.Length && line[indent] == ' ') indent++;
                int level = Math.Min(3, indent / 2);
                if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
                {
                    int x = 380 + level * 360;
                    o.Append("\\pard\\tx").Append(x).Append("\\li").Append(x).Append("\\fi-260\\sa30\\u8226?\\tab "); Inline(trimmed.Substring(2).Trim(), o); o.Append("\\par\n");
                    continue;
                }
                int digits = 0; while (digits < trimmed.Length && char.IsDigit(trimmed[digits])) digits++;
                if (digits > 0 && digits < 4 && trimmed.Length > digits + 1 && (trimmed[digits] == '.' || trimmed[digits] == ')') && trimmed[digits + 1] == ' ')
                {
                    int x = 440 + level * 360;
                    o.Append("\\pard\\tx").Append(x).Append("\\li").Append(x).Append("\\fi-340\\sa30 ").Append(Esc(trimmed.Substring(0, digits + 1))).Append("\\tab "); Inline(trimmed.Substring(digits + 2).Trim(), o); o.Append("\\par\n");
                    continue;
                }
                if (trimmed[0] == '>')
                {
                    o.Append("\\pard\\li360\\sa30{\\i\\cf3 "); Inline(trimmed.Substring(1).Trim(), o); o.Append("}\\par\n");
                    continue;
                }
                o.Append("\\pard\\sa60 "); Inline(trimmed, o); o.Append("\\par\n");
            }
            return o.Append("}").ToString();
        }

        /// <summary>Replaces or removes characters the viewer font cannot draw (emoji, icons, arrows, box drawing).</summary>
        public static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool dropped = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // A space left behind by a removed symbol is not doubled.
                if (c == ' ' && dropped && sb.Length > 0 && sb[sb.Length - 1] == ' ') { dropped = false; continue; }
                dropped = false;
                if (char.IsHighSurrogate(c)) { i++; dropped = true; continue; }          // emoji and other supplementary-plane symbols
                if (char.IsLowSurrogate(c)) { dropped = true; continue; }
                switch (c)
                {
                    case '\u2705': case '\u2714': case '\u2713': sb.Append("[ok]"); continue;
                    case '\u274C': case '\u2716': case '\u2717': sb.Append("[x]"); continue;
                    case '\u26A0': sb.Append("Warning:"); continue;
                    case '\u2757': case '\u2755': case '\u2753': dropped = true; continue;
                    case '\u2192': case '\u27A1': sb.Append("->"); continue;
                    case '\u2190': sb.Append("<-"); continue;
                    case '\u2018': case '\u2019': sb.Append('\''); continue;
                    case '\u201C': case '\u201D': sb.Append('"'); continue;
                    case '\u2013': sb.Append('-'); continue;
                    case '\u2014': sb.Append('-'); continue;
                    case '\u2026': sb.Append("..."); continue;
                    case '\u00A0': case '\u2002': case '\u2003': case '\u2009': case '\u202F': sb.Append(' '); continue;
                    case '\u200B': case '\u200C': case '\u200D': case '\uFE0E': case '\uFE0F': case '\uFEFF': continue;
                }
                if (c >= '\u2190' && c <= '\u2BFF') { dropped = true; continue; }              // arrows, math, box drawing, dingbats, misc symbols
                if (c >= '\uE000' && c <= '\uF8FF') continue;              // private use
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Removes a closing question or offer of more help ("Would you like me to...?") from the end of an answer.</summary>
        public static string TrimFollowUp(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown;
            string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
            int end = lines.Length;
            while (true)
            {
                while (end > 0 && lines[end - 1].Trim().Length == 0) end--;
                if (end == 0) break;
                string last = lines[end - 1].Trim(), lower = last.TrimStart('*', '_', '>', ' ').ToLowerInvariant();
                bool listItem = last.Length > 1 && (last[0] == '-' || last[0] == '*' || last[0] == '+' || char.IsDigit(last[0]));
                bool offer = lower.StartsWith("would you like") || lower.StartsWith("do you want") || lower.StartsWith("let me know") || lower.StartsWith("feel free") ||
                    lower.StartsWith("if you'd like") || lower.StartsWith("if you would like") || lower.StartsWith("if you want") || lower.StartsWith("i can also") ||
                    lower.StartsWith("i'm happy to") || lower.StartsWith("i am happy to") || lower.StartsWith("shall i") || lower.StartsWith("want me to") ||
                    lower.StartsWith("should i") || lower.StartsWith("is there anything") || lower.StartsWith("need help") || lower.StartsWith("how would you like");
                if (offer || (!listItem && last.EndsWith("?", StringComparison.Ordinal)) || IsRule(last)) { end--; continue; }
                break;
            }
            return string.Join("\n", lines, 0, end);
        }

        private static bool IsRule(string t)
        {
            if (t.Length < 3) return false;
            char c = t[0]; if (c != '-' && c != '*' && c != '_') return false;
            foreach (char x in t) if (x != c && x != ' ') return false;
            return true;
        }

        /// <summary>Text with Markdown marks removed (used for table cells).</summary>
        private static string Plain(string s)
        {
            return s.Replace("**", "").Replace("__", "").Replace("`", "");
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c == '\t') sb.Append("\\tab ");
                else if (c < ' ') { }
                else if (c > 126) sb.Append("\\u").Append((short)c).Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void Inline(string s, StringBuilder o)
        {
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2) { o.Append("{\\b "); Inline(s.Substring(i + 2, end - i - 2), o); o.Append("}"); i = end + 2; continue; }
                }
                if (c == '_' && i + 1 < s.Length && s[i + 1] == '_' && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
                {
                    int end = s.IndexOf("__", i + 2, StringComparison.Ordinal);
                    if (end > i + 2) { o.Append("{\\b "); Inline(s.Substring(i + 2, end - i - 2), o); o.Append("}"); i = end + 2; continue; }
                }
                if (c == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i + 1) { o.Append("{\\f1\\fs19\\cf4 ").Append(Esc(s.Substring(i + 1, end - i - 1))).Append("}"); i = end + 1; continue; }
                }
                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] != ' ' && s[i + 1] != c && (c == '*' || i == 0 || !char.IsLetterOrDigit(s[i - 1])))
                {
                    int end = s.IndexOf(c, i + 1);
                    if (end > i + 1 && s[end - 1] != ' ' && (c == '*' || end + 1 >= s.Length || !char.IsLetterOrDigit(s[end + 1])))
                    { o.Append("{\\i "); Inline(s.Substring(i + 1, end - i - 1), o); o.Append("}"); i = end + 1; continue; }
                }
                if (c == '[')
                {
                    int close = s.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int paren = close < 0 ? -1 : s.IndexOf(')', close + 2);
                    if (close > i && paren > close)
                    {
                        string label = s.Substring(i + 1, close - i - 1), url = s.Substring(close + 2, paren - close - 2);
                        Inline(label, o); if (url.Length > 0) o.Append("{\\cf3  (").Append(Esc(url)).Append(")}");
                        i = paren + 1; continue;
                    }
                }
                o.Append(Esc(c.ToString())); i++;
            }
        }
    }

    // ---- session state and the tab inside the viewer (WinForms)
    /// <summary>Kept in memory for as long as the viewer is open. Never written to disk.</summary>
    internal static class GenAiSession
    {
        public static string Key = "";
        public static string Address = "https://api.genai.mil";
        public static string Model = "gemini-3.8-flash";
        public static bool HideNames = true;
        public static int Task;
        public static long Credits;
        public static int Requests;
        public static void Forget() { Key = ""; }
    }

    internal sealed class GenAiPanel : UserControl
    {
        private static readonly Color Bg = Color.FromArgb(12, 18, 32), PanelColor = Color.FromArgb(19, 30, 48), Line = Color.FromArgb(43, 58, 81),
            Ink = Color.FromArgb(237, 243, 252), Muted = Color.FromArgb(164, 180, 202), Accent = Color.FromArgb(121, 179, 255), Danger = Color.FromArgb(255, 135, 150);
        private List<Dictionary<string, object>> edges = new List<Dictionary<string, object>>();
        private string source = "";
        private readonly TextBox key, address, preview;
        private readonly RichTextBox answer;
        private readonly ComboBox model, task;
        private readonly CheckBox hide, showSent;
        private readonly Label context, status, keyState;
        private readonly Button useKey, forgetKey, send, cancel, copy, load;
        private readonly SplitContainer split;
        private GenAiPrompt prompt;
        private GenAiClient client;
        private Thread worker;
        private string rawAnswer = "";
        private readonly TextBox ask;
        private readonly Button askButton;
        private readonly List<string[]> history = new List<string[]>();
        private string transcript = "";

        public GenAiPanel()
        {
            Name = "GenAiPanel"; Dock = DockStyle.Fill; BackColor = PanelColor; ForeColor = Ink;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = PanelColor, Padding = new Padding(8, 6, 8, 6) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(layout);

            // Row 0: title and what is loaded
            var head = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = PanelColor };
            head.Controls.Add(new Label { Text = "GENAI.MIL ASSISTANT", AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(2, 6, 14, 0) });
            context = new Label { Text = "", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 7, 0, 0) };
            head.Controls.Add(context);
            layout.Controls.Add(head, 0, 0);

            // Row 1: connection (key is typed once per viewer session)
            var conn = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = PanelColor };
            conn.Controls.Add(Small("API key"));
            key = new TextBox { Name = "GenAiKey", Width = 250, UseSystemPasswordChar = true, BackColor = Bg, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4, 6, 4, 4) };
            key.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { SaveKey(); e.SuppressKeyPress = true; } };
            conn.Controls.Add(key);
            useKey = Flat("Use key", "GenAiUseKey"); useKey.Click += delegate { SaveKey(); };
            forgetKey = Flat("Forget key", "GenAiForgetKey"); forgetKey.Click += delegate { GenAiSession.Forget(); RefreshKeyState(); SetStatus("The key was removed from memory.", false); };
            keyState = new Label { AutoSize = true, ForeColor = Muted, Margin = new Padding(6, 9, 10, 0) };
            conn.Controls.Add(useKey); conn.Controls.Add(forgetKey); conn.Controls.Add(keyState);
            conn.Controls.Add(Small("Address"));
            address = new TextBox { Name = "GenAiAddress", Width = 210, Text = GenAiSession.Address, BackColor = Bg, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4, 6, 4, 4) };
            address.TextChanged += delegate { GenAiSession.Address = address.Text.Trim(); };
            conn.Controls.Add(address);
            layout.Controls.Add(conn, 0, 1);

            // Row 2: request
            var req = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = PanelColor };
            task = new ComboBox { Name = "GenAiTask", DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Bg, ForeColor = Ink, Width = 255, Margin = new Padding(4, 6, 4, 4) };
            foreach (string t in GenAiPrompt.Tasks) task.Items.Add(t);
            task.SelectedIndex = Math.Max(0, Math.Min(GenAiSession.Task, task.Items.Count - 1));
            task.SelectedIndexChanged += delegate { GenAiSession.Task = task.SelectedIndex; Rebuild(); };
            model = new ComboBox { Name = "GenAiModel", DropDownStyle = ComboBoxStyle.DropDown, BackColor = Bg, ForeColor = Ink, Width = 235, Margin = new Padding(4, 6, 4, 4) };
            foreach (GenAiModels.Info info in GenAiModels.Known) model.Items.Add(info.Id);
            model.Text = GenAiSession.Model;
            model.TextChanged += delegate { GenAiSession.Model = model.Text.Trim(); ShowLane(); };
            load = Flat("Load models", "GenAiLoadModels"); load.Click += delegate { LoadModels(); };
            hide = new CheckBox { Name = "GenAiHide", Text = "Hide object names", Checked = GenAiSession.HideNames, AutoSize = true, ForeColor = Ink, Margin = new Padding(10, 8, 6, 0) };
            hide.CheckedChanged += delegate { GenAiSession.HideNames = hide.Checked; Rebuild(); };
            send = Flat("Send", "GenAiSend"); send.Click += delegate { Send(); };
            cancel = Flat("Cancel", "GenAiCancel"); cancel.Enabled = false; cancel.Click += delegate { if (client != null) client.Cancel(); };
            copy = Flat("Copy answer", "GenAiCopy"); copy.Click += delegate { if (answer.Text.Length > 0) Clipboard.SetText(answer.Text); };
            req.Controls.AddRange(new Control[] { task, model, load, hide, send, cancel, copy });
            layout.Controls.Add(req, 0, 2);

            // Row 3: answer on top, optional preview of exactly what is sent below
            split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 8, BackColor = Line };
            answer = new RichTextBox { Name = "GenAiAnswer", Dock = DockStyle.Fill, ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Vertical, BackColor = Bg, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10.5f), DetectUrls = false, TabStop = true };
            split.Panel1.Controls.Add(answer);
            preview = new TextBox { Name = "GenAiPreview", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Bg, ForeColor = Muted, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9.5f) };
            split.Panel2.Controls.Add(preview);
            split.Panel2Collapsed = true;
            layout.Controls.Add(split, 0, 3);

            // Row 4: follow-up questions about the answer (the conversation is kept until a new path is loaded)
            var askBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), BackColor = PanelColor };
            askBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); askBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); askBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            askBar.Controls.Add(new Label { Text = "Follow-up", Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4, 0, 0, 0) }, 0, 0);
            ask = new TextBox { Name = "GenAiAsk", Dock = DockStyle.Fill, BackColor = Bg, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4, 8, 4, 4) };
            ask.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { Ask(); e.SuppressKeyPress = true; } };
            askBar.Controls.Add(ask, 1, 0);
            askButton = Flat("Ask", "GenAiAskButton"); askButton.Dock = DockStyle.Fill; askButton.Click += delegate { Ask(); };
            askBar.Controls.Add(askButton, 2, 0);
            layout.Controls.Add(askBar, 0, 4);

            // Row 4: status, lane and the preview switch
            var foot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = PanelColor };
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); foot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
            status = new Label { Name = "GenAiStatus", Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            foot.Controls.Add(status, 0, 0);
            showSent = new CheckBox { Name = "GenAiShowSent", Text = "Show what will be sent", AutoSize = true, ForeColor = Ink, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 4, 0) };
            showSent.CheckedChanged += delegate { split.Panel2Collapsed = !showSent.Checked; if (showSent.Checked) split.SplitterDistance = Math.Max(120, split.Height * 55 / 100); };
            foot.Controls.Add(showSent, 1, 0);
            layout.Controls.Add(foot, 0, 5);

            Disposed += delegate { if (client != null) client.Cancel(); };
            RefreshKeyState(); ShowLane(); Rebuild();
        }

        private static Label Small(string text) { return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(6, 9, 2, 0) }; }

        private static Button Flat(string text, string name)
        {
            var b = new Button { Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(25, 39, 60), ForeColor = Ink, Margin = new Padding(4, 3, 4, 3), Padding = new Padding(6, 0, 6, 0) };
            b.FlatAppearance.BorderColor = Line; return b;
        }

        private void SetStatus(string text, bool error) { status.Text = text; status.ForeColor = error ? Danger : Muted; }

        private void ShowLane() { if (status != null && worker == null) SetStatus(GenAiModels.Describe(model.Text.Trim()), false); }

        private void SaveKey()
        {
            string typed = key.Text.Trim();
            if (typed.Length > 0) { GenAiSession.Key = typed; key.Text = ""; }
            RefreshKeyState();
        }

        private void RefreshKeyState()
        {
            bool have = GenAiSession.Key.Length > 0;
            key.Visible = !have; useKey.Visible = !have; forgetKey.Visible = have;
            keyState.Text = have ? "Key saved for this session (memory only)." : "Typed once, kept in memory until the viewer closes.";
        }

        /// <summary>Shows the relationships to explain and switches the prompt to them.</summary>
        public void LoadEdges(List<Dictionary<string, object>> items, string what)
        {
            edges = items ?? new List<Dictionary<string, object>>(); source = what ?? "";
            rawAnswer = ""; answer.Clear(); history.Clear(); transcript = "";
            Rebuild();
            SetStatus(prompt.Steps + " relationships ready. " + (GenAiSession.Key.Length > 0 ? "Press Send." : "Enter your API key, then press Send."), false);
        }

        private void Rebuild()
        {
            prompt = GenAiPrompt.Build(edges, hide.Checked, task.SelectedIndex, delegate(string type) { string[] h; return DesktopViewer.Guide.TryGetValue(type, out h) ? h : null; });
            preview.Text = "SYSTEM INSTRUCTIONS\r\n" + prompt.System.Replace("\n", "\r\n") + "\r\n\r\nQUESTION\r\n" + prompt.User.Replace("\n", "\r\n");
            int approx = (prompt.System.Length + prompt.User.Length) / 4;
            context.Text = (source.Length > 0 ? source + ": " : "") + prompt.Steps + " relationships, about " + approx + " tokens" + (prompt.Truncated ? " (shortened to " + GenAiPrompt.MaxSteps + ")" : "") + (hide.Checked ? ", names hidden" : ", names included");
        }

        private string UsableKey() { SaveKey(); return GenAiSession.Key; }

        private void LoadModels()
        {
            string secret = UsableKey();
            if (secret.Length == 0) { SetStatus("Enter the API key first.", true); return; }
            string where = address.Text; load.Enabled = false; SetStatus("Loading the model list...", false);
            var thread = new Thread(delegate()
            {
                string error; var ids = new GenAiClient(where, secret).ListModels(out error);
                Post(delegate
                {
                    load.Enabled = true;
                    if (error != null) { SetStatus(error, true); return; }
                    string current = model.Text; model.Items.Clear();
                    foreach (string id in ids) if (id.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) < 0) model.Items.Add(id);
                    model.Text = current; SetStatus(ids.Count + " models available.", false);
                });
            });
            thread.IsBackground = true; thread.Start();
        }

        private void Post(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void Send()
        {
            if (worker != null && worker.IsAlive) return;
            if (prompt.Steps == 0) { SetStatus("Nothing to explain yet. Open a path or select a relationship, then choose Ask GenAI.mil.", true); return; }
            if ((prompt.System.Length + prompt.User.Length) / 4 > 20000) { SetStatus("The question is too large for one request. Choose a shorter path.", true); return; }
            history.Clear(); history.Add(new string[] { "system", prompt.System }); history.Add(new string[] { "user", prompt.User });
            transcript = "";
            Run(false, "");
        }

        private void Ask()
        {
            if (worker != null && worker.IsAlive) return;
            string typed = ask.Text.Trim();
            if (typed.Length == 0) return;
            if (history.Count < 3) { SetStatus("Press Send for the path first, then ask follow-up questions about the answer.", true); return; }
            int size = 0; foreach (string[] turn in history) size += turn[1].Length;
            if ((size + typed.Length) / 4 > 20000) { SetStatus("This conversation is too long for one request. Press Send to start again.", true); return; }
            string before = transcript;
            history.Add(new string[] { "user", hide.Checked ? prompt.Hide(typed) : typed });
            transcript += (transcript.Length > 0 ? "\n\n---\n\n" : "") + "**You asked:** " + typed + "\n\n";
            ask.Text = "";
            Run(true, before);
        }

        private void Run(bool followUp, string transcriptBefore)
        {
            string secret = UsableKey(), chosen = model.Text.Trim();
            if (secret.Length == 0) { SetStatus("Enter your API key.", true); if (followUp) Undo(transcriptBefore); return; }
            if (chosen.Length == 0) { SetStatus("Choose a model.", true); if (followUp) Undo(transcriptBefore); return; }
            rawAnswer = ""; send.Enabled = false; askButton.Enabled = false; cancel.Enabled = true; ShowAnswer(""); SetStatus("Sending to " + address.Text.Trim() + " ...", false);
            client = new GenAiClient(address.Text, secret);
            var current = client; var currentPrompt = prompt; var turns = new List<string[]>(history);
            worker = new Thread(delegate()
            {
                GenAiResult result = current.ChatMessages(chosen, turns, 2500, delegate(string piece)
                {
                    Post(delegate
                    {
                        rawAnswer += piece; ShowAnswer(currentPrompt.Restore(rawAnswer));
                        SetStatus("Receiving...", false);
                    });
                });
                Post(delegate
                {
                    send.Enabled = true; askButton.Enabled = true; cancel.Enabled = false; GenAiSession.Requests++;
                    if (result.Error != null)
                    {
                        if (followUp) Undo(transcriptBefore); else { history.Clear(); transcript = ""; ShowAnswer(""); }
                        SetStatus(result.Error, true);
                        if (result.Status == 401 || result.Status == 403) { GenAiSession.Forget(); RefreshKeyState(); }
                        return;
                    }
                    GenAiSession.Credits += GenAiModels.Credits(chosen, result.PromptTokens, result.CompletionTokens);
                    history.Add(new string[] { "assistant", result.Text });
                    transcript += GenAiMarkdown.TrimFollowUp(currentPrompt.Restore(result.Text));
                    ShowAnswer("");
                    SetStatus("Done. Tokens in " + result.PromptTokens + ", out " + result.CompletionTokens + ". Credits used in this session: " + GenAiSession.Credits + " of 2,000,000 per day (" + GenAiSession.Requests + " requests). Review before acting.", false);
                });
            });
            worker.IsBackground = true; worker.Start();
        }

        private void Undo(string transcriptBefore)
        {
            if (history.Count > 0 && history[history.Count - 1][0] == "user") history.RemoveAt(history.Count - 1);
            transcript = transcriptBefore; ShowAnswer("");
        }

        private void ShowAnswer(string markdown)
        {
            try { answer.Rtf = GenAiMarkdown.ToRtf(transcript + (markdown ?? "")); }
            catch (Exception) { answer.Text = (transcript + (markdown ?? "")).Replace("\r\n", "\n").Replace("\n", "\r\n"); }
            answer.SelectionStart = answer.TextLength; answer.ScrollToCaret();
        }
    }
