using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class PermissionBroker
    {
        private readonly ConcurrentDictionary<string, Approval> _items = new ConcurrentDictionary<string, Approval>();
        private readonly AgentEventStore _eventStore;
        private NativeHost _window;

        public event Action Changed;

        public PermissionBroker(AgentEventStore eventStore)
        {
            _eventStore = eventStore;
            foreach (var token in _eventStore.ListOpenApprovals())
            {
                var item = Approval.Restore(token as JObject);
                if (item != null) { Enrich(item); _items[item.Id] = item; }
            }
            Cleanup();
        }

        public void AttachWindow(NativeHost window) { _window = window; }

        public async Task<bool> HandleAsync(HttpListenerContext context, string path, string method)
        {
            if (!path.StartsWith("/api/permissions/", StringComparison.Ordinal)) return false;
            Cleanup();
            if (method == "POST" && path == "/api/permissions/request")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                var item = new Approval
                {
                    Id = Guid.NewGuid().ToString("N"), JobId = ((string)body["jobId"] ?? "").Trim(),
                    ToolName = ((string)body["toolName"] ?? "未知工具").Trim(), Input = body["input"] ?? new JObject(),
                    CreatedAt = DateTime.UtcNow
                };
                Enrich(item);
                var policyPath = Path.Combine(AppPaths.Runs, item.JobId, "security-policy.json");
                var policy = JsonUtil.Read(policyPath, new JObject()) as JObject;
                var security = TaskSecurityPolicy.Evaluate(policy, item.ToolName, item.Input);
                item.Capability = security.Capability; item.Risk = security.Risk; item.Reason = security.Reason;
                if (security.Behavior == "allow") { item.State = "allow"; item.Message = security.Reason; }
                else if (security.Behavior == "deny") { item.State = "deny"; item.Message = security.Reason; }
                _items[item.Id] = item;
                _eventStore.RecordApproval(item.Id, item.JobId, item.ToolName, item.State, item.Input, new JObject
                { ["behavior"] = item.State, ["capability"] = item.Capability, ["risk"] = item.Risk, ["reason"] = item.Reason });
                NotifyChanged();
                if (item.State == "pending" && _window != null) _window.ShowNotification("Claude Code 请求权限", item.ToolName + " 正在等待允许或拒绝");
                await Write(context.Response, new JObject { ["id"] = item.Id, ["state"] = item.State == "pending" ? "pending" : "completed", ["automatic"] = item.State != "pending" }); return true;
            }
            if (method == "GET" && path == "/api/permissions/pending")
            {
                await Write(context.Response, PendingSnapshot()); return true;
            }
            if (method == "POST" && path == "/api/permissions/respond")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                Approval item;
                if (!_items.TryGetValue(((string)body["id"] ?? "").Trim(), out item))
                { await Write(context.Response, new JObject { ["error"] = "审批请求不存在或已过期" }, 404); return true; }
                var behavior = ((string)body["behavior"] ?? "deny").Trim().ToLowerInvariant();
                var alreadyHandled = false;
                lock (item)
                {
                    alreadyHandled = item.State != "pending";
                    if (!alreadyHandled)
                    {
                        item.State = behavior == "allow" ? "allow" : "deny";
                        item.Message = ((string)body["message"] ?? "用户在图形界面中拒绝了此操作").Trim();
                    }
                }
                if (alreadyHandled) { await Write(context.Response, new JObject { ["error"] = "审批请求已经处理", ["state"] = item.State }, 409); return true; }
                _eventStore.RecordApproval(item.Id, item.JobId, item.ToolName, item.State, item.Input, new JObject
                { ["behavior"] = item.State, ["capability"] = item.Capability, ["risk"] = item.Risk, ["reason"] = item.Reason, ["message"] = item.Message });
                NotifyChanged();
                await Write(context.Response, new JObject { ["ok"] = true, ["state"] = item.State }); return true;
            }
            if (method == "GET" && path.StartsWith("/api/permissions/result/", StringComparison.Ordinal))
            {
                var id = path.Substring("/api/permissions/result/".Length);
                Approval item;
                if (!_items.TryGetValue(id, out item)) { await Write(context.Response, new JObject { ["state"] = "expired" }, 404); return true; }
                JObject payload = null; var stillPending = false;
                lock (item)
                {
                    stillPending = item.State == "pending";
                    if (!stillPending) payload = item.State == "allow"
                            ? new JObject { ["behavior"] = "allow", ["updatedInput"] = item.Input.DeepClone() }
                            : new JObject { ["behavior"] = "deny", ["message"] = item.Message };
                }
                if (stillPending) { await Write(context.Response, new JObject { ["state"] = "pending" }); return true; }
                Approval removed; _items.TryRemove(id, out removed); _eventStore.CloseApproval(id, "consumed");
                NotifyChanged();
                await Write(context.Response, new JObject { ["state"] = "completed", ["decision"] = payload }); return true;
            }
            await Write(context.Response, new JObject { ["error"] = "审批接口不存在" }, 404); return true;
        }

        private void Cleanup()
        {
            var changed = false;
            foreach (var pair in _items)
                if ((DateTime.UtcNow - pair.Value.CreatedAt).TotalMinutes > 20)
                { Approval ignored; if (_items.TryRemove(pair.Key, out ignored)) { _eventStore.CloseApproval(pair.Key, "expired"); changed = true; } }
            if (changed) NotifyChanged();
        }

        public JArray PendingSnapshot()
        {
            Cleanup();
            var result = new JArray();
            foreach (var item in _items.Values.OrderBy(value => value.CreatedAt)) if (item.State == "pending") result.Add(item.Public());
            return result;
        }

        private void NotifyChanged()
        {
            try { var handler = Changed; if (handler != null) handler(); } catch { }
        }

        public int PendingCount(string jobId)
        {
            Cleanup();
            var count = 0;
            foreach (var item in _items.Values) if (item.State == "pending" && string.Equals(item.JobId, jobId, StringComparison.Ordinal)) count++;
            return count;
        }

        private static void Enrich(Approval item)
        {
            var request = JsonUtil.Read(Path.Combine(AppPaths.Runs, item.JobId ?? "", "request.json"), new JObject()) as JObject ?? new JObject();
            item.SessionId = (string)request["guiSessionId"] ?? (string)request["sessionId"] ?? "";
            item.Workspace = (string)request["sourceWorkspace"] ?? (string)request["workspace"] ?? "";
            item.Model = (string)request["model"] ?? "";
        }

        private static async Task Write(HttpListenerResponse response, JToken data, int status = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(data.ToString(Formatting.None));
            response.StatusCode = status; response.ContentType = "application/json; charset=utf-8"; response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length); response.Close();
        }

        private sealed class Approval
        {
            public string Id, JobId, SessionId, Workspace, Model, ToolName, State = "pending", Message = "", Capability = "unknown", Risk = "high", Reason = "";
            public JToken Input;
            public DateTime CreatedAt;
            public JObject Public() { return new JObject { ["id"] = Id, ["jobId"] = JobId, ["sessionId"] = SessionId, ["workspace"] = Workspace, ["model"] = Model, ["toolName"] = ToolName, ["input"] = SecretRedactor.Sanitize(Input), ["capability"] = Capability, ["risk"] = Risk, ["reason"] = Reason, ["createdAt"] = CreatedAt.ToString("o") }; }
            public static Approval Restore(JObject value)
            {
                if (value == null) return null;
                DateTime created; if (!DateTime.TryParse((string)value["createdAt"], out created)) created = DateTime.UtcNow;
                var decision = value["decision"] as JObject ?? new JObject();
                return new Approval
                {
                    Id = (string)value["id"] ?? "", JobId = (string)value["runId"] ?? "", ToolName = (string)value["toolName"] ?? "未知工具",
                    State = (string)value["state"] ?? "pending", Input = value["input"]?.DeepClone() ?? new JObject(), CreatedAt = created.ToUniversalTime(),
                    Capability = (string)decision["capability"] ?? "unknown", Risk = (string)decision["risk"] ?? "high",
                    Reason = (string)decision["reason"] ?? "", Message = (string)decision["message"] ?? "用户在图形界面中拒绝了此操作"
                };
            }
        }
    }

    internal static class PermissionMcp
    {
        public static void Run()
        {
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            string line;
            while ((line = input.ReadLine()) != null)
            {
                JObject request;
                try { request = JObject.Parse(line); } catch { continue; }
                var id = request["id"];
                var method = (string)request["method"] ?? "";
                if (id == null) continue;
                try
                {
                    JToken result;
                    if (method == "initialize") result = new JObject
                    {
                        ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "claude-workbench-permissions", ["version"] = "1.0.0" }
                    };
                    else if (method == "tools/list") result = new JObject { ["tools"] = new JArray(new JObject
                    {
                        ["name"] = "approval_prompt", ["description"] = "Evaluate a tool call against the durable task permission manifest; safe calls may be allowed automatically and risky calls are shown to the local user.",
                        ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject
                        {
                            ["tool_name"] = new JObject { ["type"] = "string" }, ["input"] = new JObject { ["type"] = "object" }
                        }, ["required"] = new JArray("tool_name", "input") }
                    }) };
                    else if (method == "tools/call") result = HandleApproval(request["params"] as JObject ?? new JObject());
                    else { WriteError(output, id, -32601, "Method not found"); continue; }
                    output.WriteLine(new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToString(Formatting.None));
                }
                catch (Exception error) { WriteError(output, id, -32603, error.Message); }
            }
        }

        private static JToken HandleApproval(JObject parameters)
        {
            var arguments = parameters["arguments"] as JObject ?? new JObject();
            var baseUrl = Environment.GetEnvironmentVariable("CLAUDE_GUI_PERMISSION_BASE") ?? "";
            var protectedSecret = Environment.GetEnvironmentVariable("CLAUDE_GUI_PERMISSION_SECRET_PROTECTED") ?? "";
            var secret = protectedSecret.Length == 0 ? "" : SecretStore.Unprotect(protectedSecret);
            var jobId = Environment.GetEnvironmentVariable("CLAUDE_GUI_PERMISSION_JOB") ?? "";
            int approvalTimeout;
            if (!int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_GUI_PERMISSION_TIMEOUT_SECONDS"), out approvalTimeout)) approvalTimeout = 600;
            approvalTimeout = Math.Max(5, Math.Min(3600, approvalTimeout));
            if (baseUrl.Length == 0 || secret.Length == 0) throw new InvalidOperationException("GUI permission broker is unavailable");
            var created = Request(baseUrl + "api/permissions/request", secret, "POST", new JObject
            {
                ["jobId"] = jobId, ["toolName"] = (string)arguments["tool_name"] ?? "未知工具", ["input"] = arguments["input"] ?? new JObject()
            });
            var id = (string)created["id"] ?? "";
            for (var attempt = 0; attempt < approvalTimeout * 4; attempt++)
            {
                Thread.Sleep(250);
                var state = Request(baseUrl + "api/permissions/result/" + id, secret, "GET", null);
                if ((string)state["state"] == "expired") return new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = new JObject { ["behavior"] = "deny", ["message"] = "GUI 审批已过期或 Host 已清理请求" }.ToString(Formatting.None) }) };
                if ((string)state["state"] != "completed") continue;
                var decision = state["decision"] as JObject ?? new JObject { ["behavior"] = "deny", ["message"] = "审批返回无效" };
                return new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = decision.ToString(Formatting.None) }) };
            }
            return new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = new JObject { ["behavior"] = "deny", ["message"] = "GUI 审批等待超时" }.ToString(Formatting.None) }) };
        }

        private static JObject Request(string url, string secret, string method, JObject body)
        {
            using (var client = new WebClient())
            {
                client.Encoding = Encoding.UTF8; client.Headers["X-Desktop-Secret"] = secret;
                client.Headers["X-Workbench-Protocol"] = ApiServer.ProtocolVersion.ToString();
                string text;
                if (method == "POST") { client.Headers[HttpRequestHeader.ContentType] = "application/json"; text = client.UploadString(url, "POST", (body ?? new JObject()).ToString(Formatting.None)); }
                else text = client.DownloadString(url);
                return JObject.Parse(text);
            }
        }

        private static void WriteError(StreamWriter output, JToken id, int code, string message)
        {
            output.WriteLine(new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message } }.ToString(Formatting.None));
        }
    }
}
