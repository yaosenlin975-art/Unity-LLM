/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 人设与运行参数          │
 * │ Remark      : [Range] 只是配置面板护栏，真正 │
 * │               的运行时截断见设计稿默认值总表  │
 * │ ClassName   : AgentProfile_SO               │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using Cysharp.Text;
using UnityEngine;

namespace LLM.Runtime.Agent
{
    [CreateAssetMenu(fileName = "AgentProfile_SO", menuName = "LLM/AgentProfile_SO")]
    public class AgentProfile_SO : ScriptableObject
    {
        private const int k_maxFallbackLines = 5;

        [Header("身份")]
        [Tooltip("存档键，参与 sessionId = {ProfileKey}#{instanceId}。留空自动生成：按人设内容派生 + 项目内查重，生成后与人设解耦（改人设不变）；禁止含 # / \\")]
        public string ProfileKey = "";

        [TextArea]
        [Tooltip("人设与规则。整个会话不变，是前缀缓存的头段")]
        public string PersonaPrompt = "";

        [Tooltip("该 agent 的上下文压缩配置（留空用 ContextManager 默认值）")]
        public CompressionConfig_SO CompressionConfig;

        [Header("会话参数")]
        [Range(0f, 2f)] public float Temperature = 0.8f;
        [Range(64, 8192)] public int MaxTokens = 1024;

        [Header("注入")]
        [Tooltip("非空时固定注入一行「你可以查询：……」，是工具全自主策略唯一的引导位")]
        public string QueryableHint = "";

        [Header("轮次与失控保护")]
        [Tooltip("一轮的墙钟额度：LLM 往返 + 锁等待 + 非 LongRunning 动作的执行时间。没有工具次数总闸，成本与失控靠这里 + LoopGuard。默认按 8 次工具往返 × (动作 15 + 排队 5 + 往返 5) = 200s 给")]
        [Range(10, 300)] public int TurnDeadlineSeconds = 200;

        [Tooltip("同工具名本轮调用次数上限（换参也计，防记忆换词狂搜）。0 = 关闭；工具可用 NameRepeatLimit 覆盖")]
        [Range(0, 12)] public int PerNameToolCallLimit = 4;

        [Tooltip("工具未单独配 RepeatLimit（同参）时的全局阈值")]
        [Range(1, 5)] public int GlobalRepeatLimit = 2;

        [Header("动作")]
        [Range(3, 60)] public int ActionTimeoutSeconds = 15;

        [Tooltip("仅 LongRunning 动作；其执行期不扣墙钟额度")]
        [Range(15, 180)] public int LongActionTimeoutSeconds = 60;

        [Tooltip("资源锁排队上限，扣墙钟额度；0 = 不等待直接失败")]
        [Range(0, 30)] public int LockWaitSeconds = 5;

        [Header("记忆")]
        [Range(10, 120)] public int FactSlotMaxCount = 60;

        [Tooltip("兜底：请求失败/超时/循环拦截后仍无正文时的空文本，以及被叫停的未兑现承诺。留空 = 回传空文本")]
        [TextArea] public string[] FallbackLines = new string[0];

        [Header("仅用于配置校验")]
        [Tooltip("该 provider 的平均往返耗时，用于墙钟提示，运行期不生效")]
        [Range(1, 30)] public int ExpectedLlmRttSeconds = 5;

        /// <summary>会话内已见过的 key（含手动填写的），跨实例查重用；域重载清零</summary>
        private static readonly HashSet<string> knownKeys = new();

        private void OnValidate()
        {
#if UNITY_EDITOR
            EnsureKey();
#endif
        }

        /// <summary>
        /// ProfileKey 为空时自动生成并保证唯一（ADR-012）：编辑器下按「当时」的人设内容
        /// 做 FNV-1a 派生，与项目内其他 Profile 资产及本会话已用 key 查重，冲突追加序号。
        /// 生成后即与人设解耦——改人设不变更 key，否则 sessionId 漂移、存档失联。
        /// 已有 key 时仅登记查重表，不做修改。返回是否新生成了 key。
        /// </summary>
        public bool EnsureKey()
        {
            if (!string.IsNullOrEmpty(ProfileKey))
            {
                knownKeys.Add(ProfileKey);
                return false;
            }

#if UNITY_EDITOR
            var baseKey = "agent-" + Fnv1a(PersonaPrompt).ToString("x8");
            if (!IsKeyTaken(baseKey))
            {
                ProfileKey = baseKey;
            }
            else
            {
                for (int suffix = 2; suffix < 100; suffix++)
                {
                    var candidate = baseKey + "-" + suffix;
                    if (!IsKeyTaken(candidate))
                    {
                        ProfileKey = candidate;
                        break;
                    }
                }
            }

            // 序号兜底用尽（理论上不会）：退回随机尾巴
            if (string.IsNullOrEmpty(ProfileKey))
                ProfileKey = baseKey + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
#else
            // 构建内没有资产遍历能力，随机派生；实例区分仍由 AgentCore 的 instanceId 兜底
            ProfileKey = "agent-" + Guid.NewGuid().ToString("N").Substring(0, 8);
#endif
            knownKeys.Add(ProfileKey);
            return true;
        }

#if UNITY_EDITOR
        /// <summary>key 是否已被项目内其他 Profile 资产或本会话占用</summary>
        private static bool IsKeyTaken(string key)
        {
            if (knownKeys.Contains(key)) return true;

            var guids = UnityEditor.AssetDatabase.FindAssets("t:AgentProfile_SO");
            for (int i = 0; i < guids.Length; i++)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                var others = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path);
                for (int j = 0; j < others.Length; j++)
                {
                    var other = others[j] as AgentProfile_SO;
                    if (other == null) continue;
                    if (other.ProfileKey == key) return true;
                }
            }
            return false;
        }
#endif

        /// <summary>FNV-1a 64：稳定且分布均匀，仅用于派生存档键</summary>
        private static long Fnv1a(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash ^= bytes[i];
                    hash *= 1099511628211UL;
                }
                return (long)hash;
            }
        }

        public virtual string BuildSystemPrompt()
        {
            return PersonaPrompt;
        }

        /// <summary>配置自检：ProfileKey 与 FallbackLines。墙钟只提示不硬失败（ADR-023 删除 MaxToolRounds）</summary>
        public virtual bool Validate(out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(ProfileKey))
            {
                error = "ProfileKey 必填，否则 sessionId 无法区分同类 NPC";
                return false;
            }

            if (ProfileKey.IndexOfAny(new[] { '#', '/', '\\' }) >= 0)
            {
                error = ZString.Format("ProfileKey「{0}」不得含 # / \\", ProfileKey);
                return false;
            }

            if (FallbackLines != null && FallbackLines.Length > k_maxFallbackLines)
            {
                error = ZString.Format("FallbackLines 最多 {0} 句，当前 {1}",
                    k_maxFallbackLines, FallbackLines.Length);
                return false;
            }

            return true;
        }

        /// <summary>编辑器/构造期提示：按假想 8 次工具往返估算墙钟是否偏紧（不是运行时闸）</summary>
        public const int k_assumedToolStepsForDeadlineHint = 8;

        public string DescribeDeadlineHint()
        {
            int need = k_assumedToolStepsForDeadlineHint *
                       (ActionTimeoutSeconds + LockWaitSeconds + ExpectedLlmRttSeconds);
            return ZString.Format(
                "TurnDeadlineSeconds={0}，按假想 {1} 次工具往返 × (动作 {2} + 排队 {3} + 往返 {4}) 需要约 {5}s；偏紧时多步操作可能被墙钟掐断",
                TurnDeadlineSeconds, k_assumedToolStepsForDeadlineHint, ActionTimeoutSeconds,
                LockWaitSeconds, ExpectedLlmRttSeconds, need);
        }
    }
}
