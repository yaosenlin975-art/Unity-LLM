/*
┌────────────────────────────┐
│　Description: 工具级循环检测
│　Remark: NameCap 同名换参 + L2 同参 +
│　　　　　 L3 零新信息；幂等重复封顶 Block
│　ClassName: LoopGuard
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using Cysharp.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Pool;

namespace LLM.Runtime.Agent
{
    public enum ELoopVerdict
    {
        Allow,
        /// <summary>照常执行，结果尾部追加告警</summary>
        Warn,
        /// <summary>不执行，回填 [Blocked]。该轮还会继续发请求，所以必须回填</summary>
        Block,
        /// <summary>不执行、不回填，本轮立即收尾且不再发请求（仅格式错等非重复路径）</summary>
        Abort
    }

    /// <summary>一次裁决的结果。Warn/Block 的 Message 非空，由执行器拼到回填内容</summary>
    public readonly struct LoopDecision
    {
        public readonly ELoopVerdict Verdict;
        public readonly string Message;

        public LoopDecision(ELoopVerdict verdict, string message = null)
        {
            Verdict = verdict;
            Message = message;
        }
    }

    /// <summary>
    /// 单个 agent 一份（不是静态）。NameCap/L2 计数与 L3 拉黑仅本轮有效，
    /// 非幂等签名的配额按 session 累计、只在成功执行时扣。
    /// ADR-023：幂等重复（同名或同参）一律封顶 Block，不再 Abort；
    /// 轮次总闸 MaxToolRounds 已删除，防换词狂搜靠 NameCap，成本靠墙钟。
    /// </summary>
    public sealed class LoopGuard
    {
        private const string k_sayKey = "say";

        private readonly Dictionary<string, int> turnHits = new();
        private readonly Dictionary<string, int> turnResultHash = new();
        private readonly HashSet<string> turnBlocked = new();
        private readonly Dictionary<string, int> nameHits = new();

        /// <summary>非幂等签名的终身配额：跨轮不清零，否则同一动作在两轮里各扣一次钱</summary>
        private readonly Dictionary<string, int> sessionSideEffects = new();

        private int globalRepeatLimit = 2;
        private int perNameToolCallLimit = 4;

        private int turnFormatErrors;

        /// <summary>同一轮的格式错回灌上限：超了就该收尾，别让整轮全花在重出上</summary>
        public const int k_maxFormatErrorsPerTurn = 2;

        /// <summary>记一次入参格式错。返回 true 表示已超限，调用方应按 Abort 处理</summary>
        public bool ReportFormatError()
        {
            turnFormatErrors++;
            return turnFormatErrors > k_maxFormatErrorsPerTurn;
        }

        /// <param name="perNameToolCallLimit">NameCap：0 = 关闭；工具级 NameRepeatLimit 可覆盖</param>
        public void Configure(int globalRepeatLimit, int perNameToolCallLimit = 4)
        {
            this.globalRepeatLimit = globalRepeatLimit;
            this.perNameToolCallLimit = perNameToolCallLimit;
        }

        /// <summary>本轮内该工具名被调了多少次（含不同参数；BeginTurn 清零）</summary>
        public int GetNameHits(string name) => nameHits.TryGetValue(name, out int n) ? n : 0;

        public void BeginTurn()
        {
            turnHits.Clear();
            turnResultHash.Clear();
            turnBlocked.Clear();
            nameHits.Clear();
            turnFormatErrors = 0;
        }

        /// <summary>
        /// 签名 = 工具名 + 规范化 args（键排序、剥空白），并剔除内核注入的 say。
        /// say 是模型自由措辞，留在签名里等于每次调用都不相同，非幂等阈值恒 1 会被一句话绕开。
        /// </summary>
        public static string BuildSignature(string name, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(argsJson)) return name;

            JObject parsed;
            try
            {
                parsed = JObject.Parse(argsJson);
            }
            catch (Exception)
            {
                // 模型吐出发不改的 JSON 时退化为"剥空白即用"，宁可漏判循环也不要拦不住
                return ZString.Concat(name, ":", argsJson.Replace(" ", "").Replace("\t", "")
                        .Replace("\r", "").Replace("\n", ""));
            }

            // 不用 using var：sb 要按 ref 传给 WriteCanonical，而 using 变量禁止取 ref（CS1657），
            // 改 try/finally 兜 Dispose——BuildSignature 每个工具调用都走一次，池化缓冲区不能漏
            var sb = ZString.CreateStringBuilder();
            try
            {
                sb.Append(name);
                sb.Append("(");
                WriteCanonical(parsed, ref sb, 0);
                sb.Append(")");
                return sb.ToString();
            }
            finally
            {
                sb.Dispose();
            }
        }

        /// <summary>
        /// sb 必须按 ref 传：Utf16ValueStringBuilder 是值类型，按值传进 helper 后被调方推进的
        /// index 回不到调用方，规范化出来那段会被静默丢空，签名塌成 "name()"——
        /// 于是任何不同 args 都算"同名同参重复"，非幂等动作第二次就被误拦。
        /// </summary>
        private static void WriteCanonical(JToken token, ref Utf16ValueStringBuilder sb, int depth)
        {
            if (token is JObject obj)
            {
                var keys = ListPool<string>.Get();
                foreach (var prop in obj.Properties())
                {
                    // 只剥顶层 say（规格定死）：嵌套层里的 say 是模型自己参数内容的一部分，
                    // 一并剥掉会把 {"q":{"say":"x"}} 与 {"q":{"say":"y"}} 判成同一个调用
                    if (depth == 0 && prop.Name == k_sayKey) continue;
                    keys.Add(prop.Name);
                }
                keys.Sort(StringComparer.Ordinal);

                sb.Append("{");
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(keys[i]);
                    sb.Append(":");
                    WriteCanonical(obj[keys[i]], ref sb, depth + 1);
                }
                sb.Append("}");
                // 递归里子层各借各的实例（池不会把已借出的那份再给一次），所以父层的 keys 在整段递归中始终有效
                ListPool<string>.Release(keys);
                return;
            }

            if (token is JArray array)
            {
                sb.Append("[");
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    WriteCanonical(array[i], ref sb, depth + 1);
                }
                sb.Append("]");
                return;
            }

            sb.Append(token.ToString(Formatting.None) ?? "");
        }

        /// <param name="repeatLimit">同参 L2 阈值：0 = 继承全局，负数 = 关闭 L2</param>
        /// <param name="nameRepeatLimit">NameCap：0 = 继承 profile，负数 = 该工具关闭 NameCap</param>
        public LoopDecision Decide(string signature, string name, bool idempotent,
            int repeatLimit, int nameRepeatLimit = 0, bool repeatable = false)
        {
            int hits = turnHits.TryGetValue(signature, out int prev) ? prev + 1 : 1;
            turnHits[signature] = hits;
            int nameCount = GetNameHits(name) + 1;
            nameHits[name] = nameCount;

            // 非幂等分支必须排在"关闭检测"早退之前：显式写了关闭也照样拦，否则就是静默放行重复扣钱
            if (!idempotent && !repeatable)
            {
                return sessionSideEffects.TryGetValue(signature, out int done) && done > 0
                    ? new LoopDecision(ELoopVerdict.Block, BuildNonIdempotentBlock(name))
                    : new LoopDecision(ELoopVerdict.Allow);
            }

            int limit = repeatLimit == 0 ? globalRepeatLimit : repeatLimit;
            bool alreadyBlocked = turnBlocked.Contains(signature) || turnBlocked.Contains(name);
            // L2 是否已经判死。NameCap 的首次越界只该"提醒"，不能把 L2 已经决定的 Block 降回 Warn——
            // 降档等于同参重复第 5 次反而又被放行执行一遍（AgentLoopGuardTests 长期红的那条）
            bool l2Blocks = alreadyBlocked || (limit >= 0 && hits > limit + 1);

            // NameCap：同工具名（含换参）本轮次数；优先于 L2，保证换词狂搜先被掐住
            int nameCap = ResolveNameCap(nameRepeatLimit);
            if (nameCap >= 0 && nameCount > nameCap)
            {
                if (nameCount == nameCap + 1 && !l2Blocks)
                    return new LoopDecision(ELoopVerdict.Warn, BuildNameCapWarn(name, nameCount));

                turnBlocked.Add(name);
                return new LoopDecision(ELoopVerdict.Block, BuildNameCapBlock(name));
            }

            if (alreadyBlocked)
                return new LoopDecision(ELoopVerdict.Block, BuildRepeatBlock(name));

            if (limit < 0) return new LoopDecision(ELoopVerdict.Allow);

            if (hits <= limit) return new LoopDecision(ELoopVerdict.Allow);

            if (hits == limit + 1)
                return new LoopDecision(ELoopVerdict.Warn, BuildRepeatWarn(name, hits));

            // ADR-023：幂等同参重复封顶 Block，不再 Abort；模型仍在本轮内可按指令收口
            return new LoopDecision(ELoopVerdict.Block, BuildRepeatBlock(name));
        }

        private int ResolveNameCap(int nameRepeatLimit)
        {
            if (nameRepeatLimit < 0) return -1;
            if (nameRepeatLimit > 0) return nameRepeatLimit;
            // profile 级填 0 是"关闭"（与 AgentProfile_SO 的 Tooltip 同口径）。
            // 直接当上限 0 用的话，第一次调用就 nameCount(1) > 0 → Warn，等于把这个 NPC 的每个工具砍成废动作
            return perNameToolCallLimit > 0 ? perNameToolCallLimit : -1;
        }

        /// <summary>
        /// 只有真正执行过的调用才进这里。锁排队失败、被拦截都不计数，模型还可以再试。
        /// 终身配额只在 Ok = true 时扣，否则一次超时会把该动作的唯一配额吃掉、NPC 永久卡死。
        /// </summary>
        public void NoteResult(string signature, string result, bool executed, bool ok)
        {
            if (!executed) return;

            int hash = result?.GetHashCode() ?? 0;
            if (turnResultHash.TryGetValue(signature, out int last) && last == hash)
                turnBlocked.Add(signature);
            turnResultHash[signature] = hash;

            if (ok)
                sessionSideEffects[signature] =
                    sessionSideEffects.TryGetValue(signature, out int n) ? n + 1 : 1;
        }

        /// <summary>NameCap 也拉黑该工具名本轮后续调用（换参同样拦）</summary>
        public void NoteNameBlocked(string name)
        {
            if (!string.IsNullOrEmpty(name))
                turnBlocked.Add(name);
        }

        private const string k_forceReply =
            "即使没有结果，也请直接根据已有信息简短回复玩家，不要再调用该工具。";

        private static string BuildNameCapWarn(string name, int hits)
        {
            return ZString.Format(
                "[loop-warning] 你已调用 {0} {1} 次（含不同参数），重复或换词搜索不会再带来新信息。{2}",
                name, hits, k_forceReply);
        }

        private static string BuildNameCapBlock(string name)
        {
            return ZString.Format(
                "[Blocked] {0} 本轮调用次数已达上限，已拦截。{1} 本轮不要再调用该工具。",
                name, k_forceReply);
        }

        private static string BuildRepeatWarn(string name, int hits)
        {
            return ZString.Format(
                "[loop-warning] 你已用相同参数调用 {0} {1} 次，重复调用不会再带来新信息。{2}",
                name, hits, k_forceReply);
        }

        private static string BuildRepeatBlock(string name)
        {
            return ZString.Format(
                "[Blocked] {0} 重复调用已被拦截。{1}",
                name, k_forceReply);
        }

        private static string BuildNonIdempotentBlock(string name)
        {
            return ZString.Format(
                "[Blocked] {0} 带副作用，同一签名本轮/session 只能成功执行一次，已拦截。请根据已有信息直接回复玩家，不要反复尝试同一调用。",
                name);
        }
    }
}
