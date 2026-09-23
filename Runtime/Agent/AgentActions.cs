/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 动作声明、注册表与执行器 │
 * │ Remark      : [AgentAction] 与 [Tool] 分家： │
 * │               动作异步、要上下文、绝不进注册表│
 * │ ClassName   : AgentActionRegistry           │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LLM.Runtime.Agent
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class AgentActionAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        /// <summary>0 = 继承 profile 的 GlobalRepeatLimit，负数 = 关闭 L2（对非幂等动作无效）</summary>
        public int RepeatLimit { get; set; } = 0;

        /// <summary>同名换参 NameCap：0 = 继承 PerNameToolCallLimit，负数 = 关闭（对非幂等无效）</summary>
        public int NameRepeatLimit { get; set; } = 0;

        /// <summary>动作按有副作用对待，默认 false：同签名第二次起 BLOCK，阈值恒为 1</summary>
        public bool Idempotent { get; set; } = false;

        /// <summary>有副作用但可重复执行；保留 say，并改用本轮 NameCap/L2，不占 session 一次性配额</summary>
        public bool Repeatable { get; set; } = false;

        /// <summary>带路、演出、移动一类。走 LongActionTimeoutSeconds，且执行期不扣墙钟额度</summary>
        public bool LongRunning { get; set; } = false;

        /// <summary>新输入进来时是否可被叫停</summary>
        public bool Interruptible { get; set; } = true;

        /// <summary>资源锁键模板，如 item:{itemId}。必须是「资源类型:实例 id」形式</summary>
        public string LockKey { get; set; } = "";

        public AgentActionAttribute(string name = null, string description = "")
        {
            Name = name;
            Description = description;
        }
    }

    public sealed class AgentActionDef
    {
        public string Id;
        public string Description;
        public string ParametersJsonSchema;
        public int RepeatLimit;
        public int NameRepeatLimit;
        public bool Idempotent;
        public bool Repeatable;
        public bool LongRunning;
        public bool Interruptible;
        public string LockKey;
        public MethodInfo Method;

        /// <summary>成员动作（实例方法）的落地对象；全局静态动作为 null</summary>
        public object Target;

        /// <summary>声明了 AgentActionContext 首参的方法由 runner 注入，不进 schema</summary>
        public bool TakesContext;

        public ParameterInfo[] Args;
    }

    /// <summary>
    /// 动作注册表。与 ToolRegistry 完全分开：那边 ValidateMethod 要求返回 string，
    /// 异步动作返回 UniTask&lt;AgentActionResult&gt; 会在扫描期就被跳过，硬塞进去只会被 Invoke 出一个没人等的 UniTask。
    /// </summary>
    public static class AgentActionRegistry
    {
        private const string k_sayKey = "say";
        private const string k_sayDescription = "先用一句话告诉玩家你接下来要做什么，再说这句会被念出来";

        private static readonly Dictionary<string, AgentActionDef> actions = new();
        private static readonly List<Assembly> registeredAssemblies = new();
        private static readonly object _lock = new();
        private static bool selfRegistered;

        /// <summary>内置记忆动作的登记，由 AgentCore 构造时负责，宿主不必手工调</summary>
        public static void RegisterSelfActions()
        {
            if (selfRegistered) return;
            selfRegistered = true;
            Register(typeof(AgentMemory).Assembly);
        }

        public static void Register(Assembly assembly)
        {
            if (assembly == null) return;

            lock (_lock)
            {
                if (registeredAssemblies.Contains(assembly)) return;
                registeredAssemblies.Add(assembly);

                try
                {
                    ScanAssembly(assembly);
                }
                catch (Exception ex)
                {
                    Log.Error(nameof(AgentActionRegistry),
                        ZString.Format("扫描 {0} 失败: {1}", assembly.GetName().Name, ex.Message));
                }
            }
        }

        public static bool TryGet(string id, out AgentActionDef def)
        {
            lock (_lock)
                return actions.TryGetValue(id, out def);
        }

        public static int Count
        {
            get
            {
                lock (_lock) return actions.Count;
            }
        }

        /// <summary>清空全部登记。测试 seam（程序集级注册在真机上不会重跑）</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                actions.Clear();
                registeredAssemblies.Clear();
                selfRegistered = false;
            }
        }

        /// <summary>产出声明给 LLMSession.ExtraTools。返回新 List，调用方追加不会污染它</summary>
        public static List<LLMTool> BuildDeclarations()
        {
            lock (_lock)
            {
                var list = new List<LLMTool>(actions.Count);
                foreach (var kv in actions)
                    list.Add(new LLMTool(kv.Value.Id, kv.Value.Description, kv.Value.ParametersJsonSchema));
                return list;
            }
        }

        /// <summary>已注册动作的只读快照，供编辑器 Inspector 枚举候选；返回新 List，不暴露内部字典</summary>
        public static List<AgentActionDef> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<AgentActionDef>(actions.Count);
                foreach (var kv in actions)
                    list.Add(kv.Value);
                return list;
            }
        }

        /// <summary>扫描单个实例上的 public 实例 [AgentAction] 方法，返回带 Target 的成员动作（不写全局表）</summary>
        public static List<AgentActionDef> ScanTools(object target)
        {
            var result = new List<AgentActionDef>();
            if (target == null) return result;

            CollectInstanceActions(target.GetType(), target, result);
            return result;
        }

        /// <summary>扫描 root（含自身）下所有 MonoBehaviour 的实例 [AgentAction]，成员动作 per-agent 私有</summary>
        public static List<AgentActionDef> ScanHierarchyTools(GameObject root)
        {
            var result = new List<AgentActionDef>();
            if (root == null) return result;

            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue;
                CollectInstanceActions(behaviour.GetType(), behaviour, result);
            }

            return result;
        }

        private static void CollectInstanceActions(Type type, object target, List<AgentActionDef> result)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<AgentActionAttribute>();
                if (attr == null) continue;

                if (!BuildDef(method, attr, target, out var def, out string error))
                {
                    Log.Error(nameof(AgentActionRegistry),
                        ZString.Format("跳过 {0}.{1}: {2}", type.Name, method.Name, error));
                    continue;
                }

                result.Add(def);
            }
        }

        /// <summary>按 def 反射执行动作并 await；成员动作打在 Target 上，全局静态 Target 为 null</summary>
        internal static async UniTask<AgentActionResult> InvokeAsync(AgentActionDef def, string argsJson,
            AgentActionContext ctx)
        {
            JObject args = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
                    args = JObject.Parse(argsJson);
            }
            catch (Exception)
            {
                args = null;
            }

            var values = BindArguments(def, args, ctx);
            var pending = (UniTask<AgentActionResult>)def.Method.Invoke(def.Target, values);
            return await pending;
        }

        private static void ScanAssembly(Assembly asm)
        {
            foreach (var type in asm.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<AgentActionAttribute>();
                    if (attr == null) continue;

                    if (!BuildDef(method, attr, null, out var def, out string error))
                    {
                        Log.Error(nameof(AgentActionRegistry),
                            ZString.Format("跳过 {0}.{1}: {2}", type.Name, method.Name, error));
                        continue;
                    }

                    actions[def.Id] = def;
                }
            }
        }

        private static bool BuildDef(MethodInfo method, AgentActionAttribute attr, object target,
            out AgentActionDef def, out string error)
        {
            def = null;
            error = null;

            if (method.ReturnType != typeof(UniTask<AgentActionResult>))
            {
                error = ZString.Format("返回值必须是 UniTask<AgentActionResult>，实际为 {0}", method.ReturnType.Name);
                return false;
            }

            var parameters = method.GetParameters();
            int start = 0;
            bool takesContext = parameters.Length > 0 && parameters[0].ParameterType == typeof(AgentActionContext);
            if (takesContext) start = 1;

            var simple = new List<ParameterInfo>();
            for (int i = start; i < parameters.Length; i++)
            {
                if (!IsSimpleType(parameters[i].ParameterType))
                {
                    error = ZString.Format("参数 '{0}' 类型 {1} 不支持，只有 int/float/bool/string 可进 schema",
                        parameters[i].Name, parameters[i].ParameterType.Name);
                    return false;
                }

                if (parameters[i].Name == k_sayKey)
                {
                    // 非幂等动作的 schema 会被追加 say，执行器也一律先剥离再交给 runner：自己声明就永远收不到值
                    error = "say 是内核保留参数名，动作方法不要声明同名参数";
                    return false;
                }

                simple.Add(parameters[i]);
            }

            string id = attr.Name ?? method.Name;

            if (!ValidateLockKey(attr.LockKey, simple, id, out error))
                return false;

            def = new AgentActionDef
            {
                Id = id,
                Description = attr.Description ?? "",
                RepeatLimit = attr.RepeatLimit,
                NameRepeatLimit = attr.NameRepeatLimit,
                Idempotent = attr.Idempotent,
                Repeatable = attr.Repeatable,
                LongRunning = attr.LongRunning,
                Interruptible = attr.Interruptible,
                LockKey = attr.LockKey ?? "",
                Method = method,
                Target = method.IsStatic ? null : target,
                TakesContext = takesContext,
                Args = simple.ToArray(),
                ParametersJsonSchema = BuildSchema(simple, attr.Idempotent)
            };
            return true;
        }

        private static bool ValidateLockKey(string template, List<ParameterInfo> simple,
            string id, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(template)) return true;

            // 粗粒度键禁令要可判定：没有冒号就说明锁的是一整类资源而不是一把具体椅子
            if (template.IndexOf(':') < 0)
            {
                error = ZString.Format("动作 {0} 的 LockKey「{1}」必须形如 资源类型:实例 id", id, template);
                return false;
            }

            for (int i = 0; i < simple.Count; i++)
                if (simple[i].Name == k_sayKey)
                    break;

            int cursor = 0;
            while (true)
            {
                int open = template.IndexOf('{', cursor);
                if (open < 0) break;

                int close = template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    error = ZString.Format("动作 {0} 的 LockKey 模板少了右花括号", id);
                    return false;
                }

                string argName = template.Substring(open + 1, close - open - 1);
                if (!ContainsArg(simple, argName))
                {
                    error = ZString.Format("动作 {0} 的 LockKey 引用了不存在的参数 {1}", id, argName);
                    return false;
                }

                cursor = close + 1;
            }

            return true;
        }

        private static bool ContainsArg(List<ParameterInfo> simple, string argName)
        {
            for (int i = 0; i < simple.Count; i++)
                if (simple[i].Name == argName) return true;
            return false;
        }

        /// <summary>
        /// schema 自己生成，不复用 ToolRegistry.BuildParameterSchema：它对全部参数一律生成属性、
        /// 没有跳过复杂类型的口子，且兜底成 string，照搬会造出假的 ctx 参数。
        /// </summary>
        private static string BuildSchema(List<ParameterInfo> simple, bool idempotent)
        {
            var properties = new JObject();
            var required = new JArray();

            for (int i = 0; i < simple.Count; i++)
            {
                var param = simple[i];
                properties[param.Name] = new JObject
                {
                    ["type"] = GetJsonType(param.ParameterType),
                    ["description"] = ""
                };

                if (!param.HasDefaultValue)
                    required.Add(param.Name);
            }

            // 先说后做：有副作用的动作必填 say，执行器在执行前把它播出去
            if (!idempotent)
            {
                properties[k_sayKey] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = k_sayDescription
                };
                required.Add(k_sayKey);
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0)
                schema["required"] = required;

            return schema.ToString(Formatting.None);
        }

        private static bool IsSimpleType(Type type)
        {
            return type == typeof(int) || type == typeof(float) || type == typeof(bool) || type == typeof(string)
                || type == typeof(int?) || type == typeof(float?) || type == typeof(bool?);
        }

        private static string GetJsonType(Type type)
        {
            if (type == typeof(int) || type == typeof(int?)) return "integer";
            if (type == typeof(float) || type == typeof(float?)) return "number";
            if (type == typeof(bool) || type == typeof(bool?)) return "boolean";
            return "string";
        }

        internal static object[] BindArguments(AgentActionDef def, JObject args, AgentActionContext ctx)
        {
            var values = new object[def.Args.Length + (def.TakesContext ? 1 : 0)];
            int offset = def.TakesContext ? 1 : 0;

            if (def.TakesContext) values[0] = ctx;

            for (int i = 0; i < def.Args.Length; i++)
            {
                var param = def.Args[i];
                // 取值与转换都必须走闸那一套：显式 JSON null 算"没给"，否则闸和绑定会给出两种答案
                var token = AgentArgumentGate.Value(args, param.Name);
                var type = param.ParameterType;

                if (token == null)
                {
                    values[i + offset] = param.HasDefaultValue ? param.DefaultValue : GetDefault(type);
                    continue;
                }

                values[i + offset] = AgentArgumentGate.TryCoerce(token, type, out object coerced)
                    ? coerced
                    : GetDefault(type);
            }

            return values;
        }

        private static object GetDefault(Type type)
        {
            if (type == typeof(int) || type == typeof(int?)) return 0;
            if (type == typeof(float) || type == typeof(float?)) return 0f;
            if (type == typeof(bool) || type == typeof(bool?)) return false;
            return "";
        }
    }

    /// <summary>按 actionId 调 public static 动作方法。宿主可整体换成自己的实现</summary>
    public sealed class ReflectionActionRunner : IAgentActionRunner
    {
        public UniTask<AgentActionResult> RunAsync(string actionId, string argsJson,
            AgentActionContext ctx, CancellationToken ct)
        {
            return RunInternalAsync(actionId, argsJson, ctx);
        }

        private static async UniTask<AgentActionResult> RunInternalAsync(string actionId, string argsJson,
            AgentActionContext ctx)
        {
            if (!AgentActionRegistry.TryGet(actionId, out var def))
                return AgentActionResult.Failure(ZString.Format("未注册的动作 {0}", actionId));

            return await AgentActionRegistry.InvokeAsync(def, argsJson, ctx);
        }
    }

    /// <summary>宿主没有动作时注入它，内核不写 null 分支</summary>
    public sealed class NullActionRunner : IAgentActionRunner
    {
        public UniTask<AgentActionResult> RunAsync(string actionId, string argsJson,
            AgentActionContext ctx, CancellationToken ct)
        {
            return new UniTask<AgentActionResult>(
                AgentActionResult.Failure(ZString.Format("该 agent 不支持动作 {0}", actionId)));
        }
    }

    /// <summary>
    /// 内核的 IToolExecutor 实现，只做三件事：LoopGuard 裁决、包 per-action 超时、路由。
    /// actionId 命中动作表交给 IAgentActionRunner，否则回落同步执行普通 [Tool]。
    /// 构造时持有 AgentCore，借此拿输出通道、承诺表与本轮代际；AgentActionContext 每次调用新建。
    /// </summary>
    public sealed class AgentActionExecutor : IAgentToolExecutor
    {
        private const string k_sayKey = "say";

        /// <summary>非幂等动作的 say 必填，与 BuildSchema 里 required.Add(k_sayKey) 同源</summary>
        private static readonly string[] k_sayRequired = { k_sayKey };

        private const string k_blocked =
            "[Blocked] 重复调用已被拦截：即使没有结果，也请直接根据已有信息简短回复玩家，不要再调用该工具。";

        private const string k_promiseNudge =
            "（注意：你刚才已对玩家说过「{0}」但这件事没做成，请在回答里向玩家说清楚，不要假装已完成。）";

        private readonly AgentCore agent;
        private readonly IAgentActionRunner runner;
        private readonly SyncAgentToolRegistryExecutor toolFallback = new();

        private CancellationTokenSource actionCts;
        private CancellationTokenSource lockWaitCts;
        private LockLease currentLease;
        private float actionDeadline;
        private bool actionInterruptible = true;
        private bool interrupted;
        private bool timedOut;

        /// <summary>本轮是否被硬闸拦下（LoopGuard 的 Abort 裁决，或格式错超配额）。AgentCore 收尾时映射成 LoopAborted</summary>
        public bool AbortRequested { get; private set; }

        public AgentActionExecutor(AgentCore agent, IAgentActionRunner runner)
        {
            this.agent = agent;
            this.runner = runner;
        }

        public void ResetTurnState()
        {
            AbortRequested = false;
            interrupted = false;
            timedOut = false;
        }

        /// <summary>轮次收尾的兜底清理：动作槽与锁归零，否则下一轮看见上一轮的僵尸</summary>
        public void ClearActionSlot()
        {
            Release(ref actionCts);
            Release(ref lockWaitCts);
            actionDeadline = 0;
            ReleaseLease();
        }

        /// <summary>新输入进来：只叫停声明了 Interruptible 的动作与排队中的锁等待，普通工具与不可打断动作照跑完</summary>
        public void InterruptCurrent()
        {
            // 排队发生在 actionCts 建立之前，必须单独摸得到，否则判 Stale 时等待者退不了队（ADR-010）
            lockWaitCts?.Cancel();

            if (actionCts == null || !actionInterruptible || actionCts.IsCancellationRequested) return;

            interrupted = true;
            actionCts.Cancel();
        }

        /// <summary>
        /// 动作硬超时由 AgentCore 的时钟泵驱动：拿 now 与截止时刻比较后主动 Cancel。
        /// 不用 CancelAfter，那是线程池真实计时器，单测的假时钟推不动它。
        /// </summary>
        public void PumpActionDeadline(float now)
        {
            if (actionCts == null || actionCts.IsCancellationRequested || now < actionDeadline) return;

            timedOut = true;
            actionCts.Cancel();
        }

        public async UniTask<AgentToolExecutionResult> ExecuteAsync(string name, string argsJson, CancellationToken ct)
        {
            var toolSet = agent.ToolSet;
            AgentActionDef memberAction = null;
            bool hasMemberAction = toolSet != null && toolSet.TryGetAction(name, out memberAction);
            AgentActionDef globalAction = null;
            bool hasGlobalAction = !hasMemberAction && AgentActionRegistry.TryGet(name, out globalAction);
            var actionDef = hasMemberAction ? memberAction : (hasGlobalAction ? globalAction : null);
            bool isAction = actionDef != null;
            AgentToolRegistry.RegisteredTool memberTool = default;
            bool hasMemberTool = !isAction && toolSet != null && toolSet.TryGetTool(name, out memberTool);

            LogCallInput(isAction, name, argsJson);

            AgentToolExecutionResult result;

            // 宿主门控兜底：声明层已把关闭的工具挡在 request.Tools 之外，
            // 但模型仍可能硬调，这里不进循环检测也不执行，直接回错误让模型换路
            if (!IsToolAllowed(name))
            {
                result = new AgentToolExecutionResult(
                    ZString.Format("[Tool Error] 该 agent 未启用工具 {0}", name));
            }
            else
            {
                bool idempotent = isAction ? actionDef.Idempotent
                    : hasMemberTool ? memberTool.Idempotent
                    : GetToolIdempotent(name);
                int repeatLimit = isAction ? actionDef.RepeatLimit
                    : hasMemberTool ? memberTool.RepeatLimit
                    : GetToolRepeatLimit(name);
                int nameRepeatLimit = isAction ? actionDef.NameRepeatLimit
                    : hasMemberTool ? memberTool.NameRepeatLimit
                    : GetToolNameRepeatLimit(name);
                bool repeatable = isAction && actionDef.Repeatable;

                string signature = LoopGuard.BuildSignature(name, argsJson);
                var decision = agent.LoopGuard.Decide(signature, name, idempotent, repeatLimit,
                    nameRepeatLimit, repeatable);

                if (decision.Verdict == ELoopVerdict.Abort)
                {
                    AbortRequested = true;
                    Trace("HARD", name);
                    result = new AgentToolExecutionResult(null, true);
                }
                else if (decision.Verdict == ELoopVerdict.Block)
                {
                    Trace("BLOCK", name);
                    result = new AgentToolExecutionResult(
                        string.IsNullOrEmpty(decision.Message) ? k_blocked : decision.Message);
                }
                else if (ct.IsCancellationRequested)
                {
                    // 本轮已作废（新输入进来）：同批剩余调用不再进 runner，但照常回填以保持配对
                    result = new AgentToolExecutionResult(Fail("cancelled by new input", null));
                }
                else if (isAction)
                {
                    result = await RunActionAsync(actionDef, signature, decision, argsJson, ct);
                }
                else
                {
                    result = await RunToolAsync(signature, decision, name, argsJson, ct);
                }
            }

            LogCallOutput(isAction, name, result);
            return result;
        }

        /// <summary>调用入参单独一行，便于按 sessionId 对齐一次工具往返；argsJson 为空按空对象打</summary>
        private void LogCallInput(bool isAction, string name, string argsJson)
        {
            Log.Debug(nameof(AgentActionExecutor), ZString.Format("{0} [{1}] {2} 调用 入参={3}",
                agent.SessionId, isAction ? "Action" : "Tool", name,
                string.IsNullOrEmpty(argsJson) ? "{}" : argsJson));
        }

        /// <summary>调用出参单独一行；AbortTurn 没有回填内容，用标记区分</summary>
        private void LogCallOutput(bool isAction, string name, AgentToolExecutionResult result)
        {
            Log.Debug(nameof(AgentActionExecutor), ZString.Format("{0} [{1}] {2} 返回 出参={3}",
                agent.SessionId, isAction ? "Action" : "Tool", name,
                result.Content ?? "(aborted)"));
        }

        private async UniTask<AgentToolExecutionResult> RunToolAsync(string signature, LoopDecision decision,
            string name, string argsJson, CancellationToken ct)
        {
            string content;
            var toolSet = agent.ToolSet;
            if (toolSet != null && toolSet.TryGetTool(name, out var memberTool))
            {
                // 成员工具是挂在宿主层级上的实例方法，反射打在实例上
                content = AgentToolRegistry.Execute(memberTool, argsJson);
            }
            else
            {
                var result = await toolFallback.ExecuteAsync(name, argsJson, ct);
                content = result.Content;
            }

            // 格式错与 [Tool Error] 同档：都算这次调用没成功，别让 LoopGuard 把它记成有效结果
            bool ok = content != null && !content.StartsWith("[Tool Error]", StringComparison.Ordinal)
                      && !content.StartsWith(AgentArgumentGate.k_errorPrefix, StringComparison.Ordinal);

            agent.LoopGuard.NoteResult(signature, content, true, ok);
            return new AgentToolExecutionResult(AppendWarning(content, decision));
        }

        private async UniTask<AgentToolExecutionResult> RunActionAsync(AgentActionDef def, string signature,
            LoopDecision decision, string argsJson, CancellationToken ct)
        {
            // 格式闸：坏参数不执行，回灌指名到参数的原因。say 的必填与 schema 同源
            // （非幂等动作必填 say，见 BuildSchema），所以在这里一起查掉
            if (!AgentArgumentGate.TryValidate(argsJson, def.Args,
                    def.Idempotent ? null : k_sayRequired, out var args, out string formatError))
                return FormatFailure(formatError, def.Id);

            LockLease lease = null;

            try
            {
                var key = BuildLockKey(def, args);
                if (!string.IsNullOrEmpty(key))
                {
                    // 锁等待单独挂 linked CTS：新输入进来时 InterruptCurrent 只取消动作令牌，
                    // 而此刻 actionCts 还没建立——不挂这条，判 Stale 时排队中的等待者永远退不了队
                    lockWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    LockLease acquired;
                    bool waiterCancelled;
                    try
                    {
                        acquired = await AgentResourceLocks.TryAcquireAsync(key, agent.LockWaitSeconds,
                            agent.SessionId, agent.TurnGeneration, lockWaitCts.Token);
                        waiterCancelled = lockWaitCts.IsCancellationRequested;
                    }
                    finally
                    {
                        // 排队中抛异常（如 ct 传播路径变化）也不能漏掉这一个 CTS，否则逐动作累积泄漏
                        Release(ref lockWaitCts);
                    }

                    lease = acquired;

                    // 排队失败不计数：动作根本没执行，模型可以再试；防无限重试靠 NameCap 与墙钟
                    if (lease == null)
                    {
                        return new AgentToolExecutionResult(Fail(waiterCancelled
                            ? "cancelled by new input"
                            : ZString.Format("目标被占用（排队 {0}s 未获得锁）", agent.LockWaitSeconds), null));
                    }

                    currentLease = lease;
                }

                var say = TakeSay(def, args, ref argsJson);
                if (!string.IsNullOrEmpty(say)) agent.PlaySay(def.Id, say);

                var result = await RunWithTimeout(def, argsJson, ct);
                agent.LoopGuard.NoteResult(signature, result.Content, true, result.Ok);

                if (result.Ok)
                {
                    agent.FulfillPromise(def.Id);
                    return new AgentToolExecutionResult(AppendWarning(result.Content ?? "", decision));
                }

                return new AgentToolExecutionResult(Fail(result.Content, def.Id, decision));
            }
            finally
            {
                ReleaseLease();
            }
        }

        private async UniTask<AgentActionResult> RunWithTimeout(AgentActionDef def, string argsJson,
            CancellationToken ct)
        {
            int seconds = def.LongRunning ? agent.LongActionTimeoutSeconds : agent.ActionTimeoutSeconds;
            actionInterruptible = def.Interruptible;
            interrupted = false;
            timedOut = false;
            actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            actionDeadline = AgentCore.NowSeconds() + seconds;

            // LongRunning 的执行期不扣墙钟额度：CTS 没法暂停，只能把截止时刻整体往后挪
            if (def.LongRunning) agent.BeginLongRunningBudget();

            var ctx = new AgentActionContext
            {
                Agent = agent,
                Profile = agent.Profile,
                SessionId = agent.SessionId,
                CancellationToken = actionCts.Token
            };

            AgentActionResult result;
            try
            {
                // 成员动作是挂在宿主层级上的实例方法，直接反射打在 Target 上；
                // 全局动作仍交给宿主 runner，保持可替换
                result = def.Target != null
                    ? await AgentActionRegistry.InvokeAsync(def, argsJson, ctx)
                    : await runner.RunAsync(def.Id, argsJson, ctx, actionCts.Token);

                if (interrupted) result = AgentActionResult.Failure("cancelled by new input");
                else if (timedOut) result = AgentActionResult.Failure("timeout");
            }
            catch (OperationCanceledException)
            {
                result = AgentActionResult.Failure(ct.IsCancellationRequested ? "cancelled by new input" : "timeout");
            }
            catch (Exception ex)
            {
                result = AgentActionResult.Failure(ex.InnerException != null
                    ? ex.InnerException.Message
                    : ex.Message);
            }
            finally
            {
                if (def.LongRunning) agent.EndLongRunningBudget();
                Release(ref actionCts);
                actionDeadline = 0;
            }

            return result;
        }

        private string Fail(string reason, string actionId)
        {
            return Fail(reason, actionId, default);
        }

        /// <summary>失败回填 + 承诺收口指令 + 循环告警。语义失败与超时同一条路径，前缀统一 [Action Failed]</summary>
        private string Fail(string reason, string actionId, LoopDecision decision)
        {
            using var sb = ZString.CreateStringBuilder();
            sb.Append("[Action Failed] ");
            sb.Append(string.IsNullOrEmpty(reason) ? "未知失败" : reason);

            if (!string.IsNullOrEmpty(actionId))
            {
                var say = agent.PeekPromise(actionId);
                if (!string.IsNullOrEmpty(say))
                    sb.Append(ZString.Format(k_promiseNudge, say));
            }

            if (decision.Verdict == ELoopVerdict.Warn && !string.IsNullOrEmpty(decision.Message))
            {
                sb.Append("\n");
                sb.Append(decision.Message);
            }

            return sb.ToString();
        }

        private static string AppendWarning(string content, LoopDecision decision)
        {
            if (decision.Verdict != ELoopVerdict.Warn || string.IsNullOrEmpty(decision.Message))
                return content;

            return ZString.Concat(content, "\n", decision.Message);
        }

        private void Trace(string level, string name)
        {
            AgentTrace.Emit(agent.SessionId, EAgentTraceKind.ActionIntercepted,
                ZString.Concat(level, " ", name), agent.RoundSerial);
        }

        /// <summary>
        /// 入参格式错的回灌。同一轮第 3 次起直接收尾（ADR-023：格式错 Abort 档是删掉
        /// MaxToolRounds 之后唯一还站在格式错这条路上的闸，不拦就只能等墙钟）。
        /// </summary>
        private AgentToolExecutionResult FormatFailure(string error, string name)
        {
            if (agent.LoopGuard.ReportFormatError())
            {
                AbortRequested = true;
                Trace("FORMAT-ABORT", name);
                return new AgentToolExecutionResult(null, true);
            }

            Trace("FORMAT", name);
            return new AgentToolExecutionResult(Fail(error, null));
        }

        /// <summary>say 是内核注入的伪参数：读出来就必须从 args 里删掉再交给 runner，否则参数个数不匹配直接炸</summary>
        private static string TakeSay(AgentActionDef def, JObject args, ref string argsJson)
        {
            if (args == null || def.Idempotent) return null;

            var token = args[k_sayKey];
            if (token == null) return null;

            string say = token.Value<string>();
            args.Remove(k_sayKey);
            argsJson = args.ToString(Formatting.None);
            return say;
        }

        /// <summary>LockKey 是对 args 的模板（item:{itemId}）。插值出来缺实例 id 视作不加锁</summary>
        private string BuildLockKey(AgentActionDef def, JObject args)
        {
            if (string.IsNullOrEmpty(def.LockKey)) return null;

            using var sb = ZString.CreateStringBuilder();
            int cursor = 0;

            while (true)
            {
                int open = def.LockKey.IndexOf('{', cursor);
                if (open < 0)
                {
                    sb.Append(def.LockKey.Substring(cursor, def.LockKey.Length - cursor));
                    break;
                }

                int close = def.LockKey.IndexOf('}', open + 1);
                sb.Append(def.LockKey.Substring(cursor, open - cursor));

                var token = args?[def.LockKey.Substring(open + 1, close - open - 1)];
                sb.Append(token == null ? "" : token.ToString());
                cursor = close + 1;
            }

            string key = sb.ToString();

            if (key.EndsWith(":", StringComparison.Ordinal))
            {
                // 形如 item: 的键会把整类资源锁在一起，宁可不加锁也不能全局串行
                Log.Warning(nameof(AgentActionRegistry),
                    ZString.Format("动作 {0} 的 LockKey 插值后缺实例 id，本次不加锁: {1}", def.Id, key));
                return null;
            }

            return key;
        }

        private void ReleaseLease()
        {
            if (currentLease == null) return;

            var lease = currentLease;
            currentLease = null;
            lease.Dispose();
        }

        private static void Release(ref CancellationTokenSource cts)
        {
            if (cts == null) return;
            cts.Dispose();
            cts = null;
        }

        private static bool GetToolIdempotent(string name)
        {
            return !AgentToolRegistry.TryGet(name, out var tool) || tool.Idempotent;
        }

        private static int GetToolRepeatLimit(string name)
        {
            return AgentToolRegistry.TryGet(name, out var tool) ? tool.RepeatLimit : 0;
        }

        private static int GetToolNameRepeatLimit(string name)
        {
            return AgentToolRegistry.TryGet(name, out var tool) ? tool.NameRepeatLimit : 0;
        }

        /// <summary>宿主门控：null 门全部放行；被关闭的工具名一律拒绝</summary>
        private bool IsToolAllowed(string name)
        {
            var gate = agent.Session.ToolGate;
            return gate == null || gate.IsToolEnabled(name);
        }
    }
}
