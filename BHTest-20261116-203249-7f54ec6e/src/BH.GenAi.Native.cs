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
            var result = new GenAiResult();
            string messages = ",\"messages\":[{\"role\":\"system\",\"content\":" + GenAiJson.Quote(system) + "},{\"role\":\"user\",\"content\":" + GenAiJson.Quote(user) + "}]}";
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
                "Write in plain text without markdown tables. Sections, in order, only for what was asked: " +
                "SUMMARY (two sentences); WHY IT MATTERS (what an attacker who controls the first object could reach); STEP BY STEP (one short line per relationship); " +
                "RECOMMENDED FIXES (numbered, safest and highest impact first, each naming the exact relationship to change and the least disruptive change); " +
                "HOW TO VERIFY AND ROLL BACK; CONFIDENCE AND LIMITS. " +
                "Give defensive remediation only. Do not provide exploitation commands or attack tooling.";
            return p;
        }

        private static string Get(Dictionary<string, object> row, string key)
        {
            object v; return row.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
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

    // ---- window
    internal sealed class GenAiWindow : Form
    {
        private readonly List<Dictionary<string, object>> edges;
        private readonly TextBox key, baseUrl, preview, answer;
        private readonly ComboBox model, task;
        private readonly CheckBox hide;
        private readonly Label status, laneNote;
        private readonly Button send, cancel, copy, load;
        private GenAiPrompt prompt;
        private GenAiClient client;
        private Thread worker;
        private long sessionCredits;
        private int sessionRequests;
        private string rawAnswer = "";

        public GenAiWindow(List<Dictionary<string, object>> edges)
        {
            this.edges = edges;
            Text = "GenAI.mil assistant"; Name = "GenAiWindow"; Size = new Size(1000, 800); MinimumSize = new Size(820, 640);
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(12, 18, 32); ForeColor = Color.FromArgb(237, 243, 252);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);

            layout.Controls.Add(Note("Sends only the text in the box below, over HTTPS, when you press Send. The API key is typed each time, kept in memory and cleared when this window closes. Names are hidden by default."), 0, 0);

            var row1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(0) };
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60)); row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60)); row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            row1.Controls.Add(Note("API key"), 0, 0);
            key = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Name = "GenAiKey", Margin = new Padding(0, 6, 8, 0) }; row1.Controls.Add(key, 1, 0);
            row1.Controls.Add(Note("Model"), 2, 0);
            model = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Name = "GenAiModel", Margin = new Padding(0, 6, 8, 0) };
            foreach (GenAiModels.Info info in GenAiModels.Known) model.Items.Add(info.Id);
            model.Text = "gemini-3.8-flash"; model.TextChanged += delegate { laneNote.Text = GenAiModels.Describe(model.Text.Trim()); };
            row1.Controls.Add(model, 3, 0);
            row1.Controls.Add(Note("Address"), 4, 0);
            baseUrl = new TextBox { Dock = DockStyle.Fill, Text = "https://api.genai.mil", Name = "GenAiAddress", Margin = new Padding(0, 6, 0, 0) }; row1.Controls.Add(baseUrl, 5, 0);
            layout.Controls.Add(row1, 0, 1);

            var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            task = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Name = "GenAiTask" };
            foreach (string t in GenAiPrompt.Tasks) task.Items.Add(t);
            task.SelectedIndex = 0; task.SelectedIndexChanged += delegate { Rebuild(); };
            hide = new CheckBox { Text = "Hide object names", Checked = true, AutoSize = true, Name = "GenAiHide", Margin = new Padding(12, 6, 12, 0), ForeColor = ForeColor };
            hide.CheckedChanged += delegate { Rebuild(); };
            load = new Button { Text = "Load models", AutoSize = true, Name = "GenAiLoadModels" }; load.Click += delegate { LoadModels(); };
            row2.Controls.Add(task); row2.Controls.Add(hide); row2.Controls.Add(load);
            layout.Controls.Add(row2, 0, 2);

            preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Name = "GenAiPreview", Font = new Font("Consolas", 9.5f), BackColor = Color.FromArgb(19, 30, 48), ForeColor = ForeColor };
            layout.Controls.Add(preview, 0, 3);

            var row3 = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            send = new Button { Text = "Send", Width = 110, Name = "GenAiSend" }; send.Click += delegate { Send(); };
            cancel = new Button { Text = "Cancel", Width = 90, Enabled = false, Name = "GenAiCancel" }; cancel.Click += delegate { if (client != null) client.Cancel(); };
            copy = new Button { Text = "Copy answer", Width = 120, Name = "GenAiCopy" }; copy.Click += delegate { if (answer.Text.Length > 0) Clipboard.SetText(answer.Text); };
            laneNote = Note(GenAiModels.Describe(model.Text)); laneNote.AutoSize = true; laneNote.Margin = new Padding(14, 8, 0, 0);
            row3.Controls.Add(send); row3.Controls.Add(cancel); row3.Controls.Add(copy); row3.Controls.Add(laneNote);
            layout.Controls.Add(row3, 0, 4);

            answer = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Name = "GenAiAnswer", BackColor = Color.FromArgb(19, 30, 48), ForeColor = ForeColor };
            layout.Controls.Add(answer, 0, 5);

            status = Note("Ready. Review the text above, enter your API key and press Send. The answer is generated by an AI model and needs human review before any change.");
            layout.Controls.Add(status, 0, 6);

            FormClosing += delegate { if (client != null) client.Cancel(); key.Text = ""; };
            Rebuild();
        }

        private static Label Note(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(164, 180, 202), TextAlign = ContentAlignment.MiddleLeft };
        }

        private void Rebuild()
        {
            prompt = GenAiPrompt.Build(edges, hide.Checked, task.SelectedIndex, delegate(string type) { string[] h; return DesktopViewer.Guide.TryGetValue(type, out h) ? h : null; });
            preview.Text = "SYSTEM INSTRUCTIONS\r\n" + prompt.System.Replace("\n", "\r\n") + "\r\n\r\nQUESTION\r\n" + prompt.User.Replace("\n", "\r\n");
            int approx = (prompt.System.Length + prompt.User.Length) / 4;
            if (status != null) status.Text = prompt.Steps + " relationships, about " + approx + " tokens to send." + (prompt.Truncated ? " The list was shortened to " + GenAiPrompt.MaxSteps + " steps." : "");
        }

        private void LoadModels()
        {
            if (key.Text.Trim().Length == 0) { status.Text = "Enter the API key first."; return; }
            string address = baseUrl.Text, secret = key.Text.Trim();
            load.Enabled = false; status.Text = "Loading the model list...";
            var thread = new Thread(delegate()
            {
                string error; var ids = new GenAiClient(address, secret).ListModels(out error);
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        load.Enabled = true;
                        if (error != null) { status.Text = error; return; }
                        string current = model.Text; model.Items.Clear();
                        foreach (string id in ids) if (id.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) < 0) model.Items.Add(id);
                        model.Text = current; status.Text = ids.Count + " models available.";
                    }));
                }
                catch (InvalidOperationException) { }
            });
            thread.IsBackground = true; thread.Start();
        }

        private void Send()
        {
            if (worker != null && worker.IsAlive) return;
            string secret = key.Text.Trim(), chosen = model.Text.Trim();
            if (secret.Length == 0) { status.Text = "Enter your API key."; return; }
            if (chosen.Length == 0) { status.Text = "Choose a model."; return; }
            if (prompt.Steps == 0) { status.Text = "There are no relationships to explain. Open a path first."; return; }
            if ((prompt.System.Length + prompt.User.Length) / 4 > 20000) { status.Text = "The question is too large for one request. Choose a shorter path."; return; }
            rawAnswer = ""; answer.Text = ""; send.Enabled = false; cancel.Enabled = true; status.Text = "Sending to " + baseUrl.Text.Trim() + " ...";
            client = new GenAiClient(baseUrl.Text, secret);
            var current = client; var currentPrompt = prompt; string system = prompt.System, user = prompt.User;
            worker = new Thread(delegate()
            {
                GenAiResult result = current.Chat(chosen, system, user, 2500, delegate(string piece)
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            rawAnswer += piece; answer.Text = currentPrompt.Restore(rawAnswer).Replace("\r\n", "\n").Replace("\n", "\r\n");
                            answer.SelectionStart = answer.Text.Length; answer.ScrollToCaret(); status.Text = "Receiving...";
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        send.Enabled = true; cancel.Enabled = false; sessionRequests++;
                        if (result.Error != null) { status.Text = result.Error; return; }
                        sessionCredits += GenAiModels.Credits(chosen, result.PromptTokens, result.CompletionTokens);
                        answer.Text = currentPrompt.Restore(result.Text).Replace("\r\n", "\n").Replace("\n", "\r\n");
                        status.Text = "Done. Tokens in " + result.PromptTokens + ", out " + result.CompletionTokens + ". Credits used this window: " + sessionCredits + " of 2,000,000 per day (" + sessionRequests + " requests). Review before acting.";
                    }));
                }
                catch (InvalidOperationException) { }
            });
            worker.IsBackground = true; worker.Start();
        }
    }
