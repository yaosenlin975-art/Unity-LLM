/*
┌────────────────────────────┐
│　Description: NPC 专用宿主
│　Remark: 一个组件兼任三职——Agent 宿主、个体提示词
│　　　　　 总控、世界里的可观测身份
│　ClassName: NpcAgentHost
└────────────────────────────┘
*/

using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime.Agent.World;
using UnityEngine;

namespace LLM.Runtime.Agent.Npc
{
    /// <summary>
    /// 会思考的 NPC 才挂它。玩家、道具、群众只被看见不思考，挂轻量 <see cref="WorldObservable"/>。
    /// 个体侧贡献者只登记在自己身上：A 的 OnEnable 不该让 B 的静态段作废。反过来，把个体清单
    /// 并进世界侧全局表也一样错——全场 NPC 会互相打作废，稳定前缀再也命中不了
    /// </summary>
    public class NpcAgentHost : AgentHost, IWorldObservable
    {
        #region - 字段 -

        [Header("观测名")]
        [Tooltip("给模型看的名字；留空则用物体名")]
        [SerializeField] private string observedLabel = "";

        [Header("快照注入")]
        [Tooltip("关掉就不注入世界/个体快照，只留好感与手填兜底")]
        [SerializeField] private bool includeSnapshot = true;
        [Tooltip("在场者查询半径（米），0 = 不列在场者")]
        [SerializeField, Min(0f)] private float observeRadius = 8f;
        [Tooltip("最多列出几个在场者，超出部分折叠为\"另有 K 个未列出\"；0 = 不限")]
        [SerializeField, Min(0)] private int observeMaxPeers = 6;

        private readonly List<ISelfProfileContributor> profiles = new();
        private readonly List<ISelfStateContributor> states = new();

        // 个体静态段的自查缓存。键是"注册列表变没变"而不是"文本变没变"——
        // 文本变没变只有贡献者自己知道（它有最便宜的比较），而注册列表变化才决定重算频率
        private long profileRevision;
        private string profileText;
        private long profileTextRevision = -1;

        /// <summary>
        /// 整段 stable 的缓存。(世界 LoreRevision, 本地 profileRevision) 二元组任一变化才重算。
        /// 这一层省的不是 CPU，是"新字符串实例"：每轮重新拼接会产出新引用，
        /// 而 provider 的稳定前缀与用例里的 AreSame 都以"同一个引用"为前提
        /// </summary>
        private string stableText;
        private long stableLoreRevision = -1;
        private long stableProfileRevision = -1;

        #endregion

        #region - 属性 -

        public string ObservedLabel => string.IsNullOrEmpty(observedLabel) ? gameObject.name : observedLabel;

        /// <summary>观测原点。默认物体位置，高个子角色可覆盖到胸口高度</summary>
        public virtual Vector3 ObservedPosition => transform.position;

        /// <summary>同列表内稳定排序用。NPC 之间按距离排，不按它，留默认值</summary>
        public virtual int Order => 0;

        /// <summary>别人看我与我看自己共用一份文案，避免两处各写一版而漂移</summary>
        public string ObservableState => includeSnapshot ? RenderSelfState() : string.Empty;

        #endregion

        #region - 生命周期 -

        // 双身份检查只放在 Awake：它每个实例只跑一遍，"警告一次"天然成立，不用另立标志位。
        // 顺序上两种情况都收敛（见 SupersedeLightweightIdentity）： Awake 早于轻量那个的 OnEnable
        // 就压根不登记，晚于它就靠关掉它触发的 OnDisable 出册
        private void Awake()
        {
            SupersedeLightweightIdentity();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            WorldSnapshotService.Register((IWorldObservable)this);
        }

        // 注销绑在启用态上（形状同 WorldObservable）：禁用即出册，物体被 Destroy 时
        // OnDisable 也跟着走这条路，所以"我这个身份"不需要调用方配对注销。
        // 注意这只覆盖在册身份：profiles/states 里的贡献者仍要自己注销——缓存键只看注册列表，
        // 贡献者被销毁而列表没变时，旧文本会一直留到下一次注册变化（判活只兜住不抛异常）
        protected override void OnDisable()
        {
            WorldSnapshotService.Unregister((IWorldObservable)this);
            base.OnDisable();
        }

        #endregion

        #region - 个体贡献者登记 -

        public void RegisterProfile(ISelfProfileContributor contributor)
        {
            if (contributor is null || ContainsReference(profiles, contributor)) return;

            profiles.Add(contributor);
            profileRevision++;
        }

        public void UnregisterProfile(ISelfProfileContributor contributor)
        {
            if (contributor is not null && RemoveReference(profiles, contributor)) profileRevision++;
        }

        // 状态侧不配 revision：动态段每轮都重问，登记变化下一轮自然体现
        public void RegisterState(ISelfStateContributor contributor)
        {
            if (contributor is null || ContainsReference(states, contributor)) return;

            states.Add(contributor);
        }

        public void UnregisterState(ISelfStateContributor contributor)
        {
            if (contributor is not null) RemoveReference(states, contributor);
        }

        #endregion

        #region - 快照装配 -

        /// <summary>
        /// 世界静态（全局共享缓存）+ 个体静态（本 NPC 缓存）。这里再整段缓存一次：
        /// 每轮重新拼接会产出新的 string，"稳定前缀"与测试里的 AreSame 都靠这一层才成立。
        /// 键里不看在册观测物数量（ObservedRevision）：别人进出来去只改动态段的在场者，
        /// 与"我会什么"无关——把它算进键就等于全场 NPC 每注册一次都作废彼此的前缀
        /// </summary>
        protected override string RenderStableContext()
        {
            if (!includeSnapshot) return base.RenderStableContext();

            long loreRevision = WorldSnapshotService.LoreRevision;
            if (stableText is not null && stableLoreRevision == loreRevision
                && stableProfileRevision == profileRevision)
                return stableText;

            var worldSection = BuildSection("## 世界设定", WorldSnapshotService.RenderLore());
            var abilitySection = BuildSection("## 你的能力", RenderSelfProfile());
            var frameworkSection = base.RenderStableContext();

            stableText = Join(Join(worldSection, abilitySection), frameworkSection);
            stableLoreRevision = loreRevision;
            stableProfileRevision = profileRevision;
            return stableText;
        }

        /// <summary>世界动态 + 个体动态，再接基类的好感与手填兜底。整段绝不缓存（同 RenderFacts 的理由）</summary>
        protected override string RenderCoreSnapshot()
        {
            var frameworkSection = base.RenderCoreSnapshot();
            if (!includeSnapshot) return frameworkSection;

            // 在场者按"我"的位置算、并且要排掉"我"这个身份，所以每次现取
            var worldSection = BuildSection("## 世界现状",
                WorldSnapshotService.RenderFacts(ObservedPosition, this, observeRadius, observeMaxPeers));
            var stateSection = BuildSection("## 你的状态", RenderSelfState());

            return Join(Join(worldSection, stateSection), frameworkSection);
        }

        /// <summary>整段缓存：注册列表不变就直接返回同一引用，静态段才进得了稳定前缀</summary>
        private string RenderSelfProfile()
        {
            if (profileText is not null && profileTextRevision == profileRevision) return profileText;

            if (profiles.Count == 0)
            {
                profileText = "";
                profileTextRevision = profileRevision;
                return profileText;
            }

            var segments = new List<SelfSegment>(profiles.Count);
            for (int i = 0; i < profiles.Count; i++)
            {
                var contributor = profiles[i];
                if (IsDestroyed(contributor)) continue;

                var text = contributor.RenderProfile();
                if (string.IsNullOrEmpty(text)) continue;

                segments.Add(new SelfSegment(contributor.Order, contributor.GetType().Name, text));
            }

            // 先渲染后排序（与 WorldSnapshotService.RenderLore 同形）：第三档键就是渲染出来的文本，
            // 只能先把文本取出来。只在未命中路径上做，每个贡献者这一轮仍只被问一次
            segments.Sort(CompareSelfSegment);

            profileText = JoinSegments(segments);
            profileTextRevision = profileRevision;
            return profileText;
        }

        /// <summary>
        /// 不整段缓存：每轮逐个问，变没变由贡献者自己判（它手里有最便宜的比较）。
        /// 在场者每帧都在动，整段缓存等于永远不命中，还白养一份字符串
        /// </summary>
        private string RenderSelfState()
        {
            if (states.Count == 0) return string.Empty;

            var segments = new List<SelfSegment>(states.Count);
            for (int i = 0; i < states.Count; i++)
            {
                var contributor = states[i];
                if (IsDestroyed(contributor)) continue;

                var text = contributor.RenderState();
                if (string.IsNullOrEmpty(text)) continue;

                segments.Add(new SelfSegment(contributor.Order, contributor.GetType().Name, text));
            }

            // 排序键与静态段同构。动态段没有缓存可保护，但顺序抖一下就会让"这轮快照变了"
            // 这件事多带上几个字节的假差异，前缀与压缩判断都会被它白顶一次
            segments.Sort(CompareSelfSegment);

            return JoinSegments(segments);
        }

        #endregion

        #region - 私有方法 -

        /// <summary>
        /// 同物体上另有轻量 <see cref="WorldObservable"/> 时以本组件为准，把那个关掉。
        /// 不处理的话 NPC 会把自己念进在场者：<c>RenderFacts</c> 的 except 只按引用排掉
        /// 本组件这身份，同名同位置的第二个身份仍在册，多出来那条"别人"就是这么冒出来的。
        /// 关 <c>enabled</c> 而不是替它注销：两种注册顺序都收敛到"不在册"——
        /// Awake 阶段它的 OnEnable 还没跑，关掉就压根不登记；已经登记过则 OnDisable 自己出册
        /// </summary>
        private void SupersedeLightweightIdentity()
        {
            var lightweight = GetComponent<WorldObservable>();
            if (lightweight is null) return;

            Log.Warning(nameof(NpcAgentHost),
                ZString.Concat("同物体上同时挂了 WorldObservable 与 NpcAgentHost：",
                    "会思考的身份以 NpcAgentHost 为准，前者已关闭，请从预制体/场景里删掉它"), this);
            lightweight.enabled = false;
        }

        /// <summary>
        /// 段标题 + 正文。正文为空返回 null = "没有这一段"，由 Join 跳过。
        /// 返回 string 而不是往调用方的 builder 里追加：Utf16ValueStringBuilder 是 struct，
        /// 按值传进去追加全落在副本上，要拿同一个实例得 ref 传，而 using 声明出来的 builder
        /// 又禁止 ref 传（CS1657）——仓库约定 helper 一律返回 string（同 BuildPeersText）
        /// </summary>
        private static string BuildSection(string header, string body)
        {
            if (string.IsNullOrEmpty(body)) return null;

            return ZString.Concat(header, "\n", body);
        }

        /// <summary>非空段之间补一个换行；两段都空返回空串（整段缓存要求引用非空，不能是 null）</summary>
        private static string Join(string head, string tail)
        {
            if (string.IsNullOrEmpty(head)) return tail ?? "";
            if (string.IsNullOrEmpty(tail)) return head;

            return ZString.Concat(head, "\n", tail);
        }

        private static string JoinSegments(List<SelfSegment> segments)
        {
            using var sb = ZString.CreateStringBuilder();
            for (int i = 0; i < segments.Count; i++)
            {
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(segments[i].Text);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 个体段排序：Order → 类型名 → 渲染文本的 ordinal，与世界的 CompareLoreSegment 同构。
        /// 前两档只到"类型"粒度：同一类型注册多个实例且 Order 相同时两键全等，排序就退化成
        /// 注册顺序（= 子组件 Awake 先后），个体静态段不再逐字节稳定，整段缓存与前缀命中的前提直接落空。
        /// 末档用文本兜死：三键全等意味着文本也相同，此时怎么排都输出同一串字节
        /// </summary>
        private static int CompareSelfSegment(SelfSegment a, SelfSegment b)
        {
            int byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0) return byOrder;

            int byType = string.CompareOrdinal(a.TypeKey, b.TypeKey);
            if (byType != 0) return byType;

            return string.CompareOrdinal(a.Text, b.Text);
        }

        /// <summary>
        /// 判活（贡献者与在册对象同一条）：只拦已销毁的 Unity 对象，放行纯 C# 实现。
        /// 表里存的是接口引用，<c>contributor == null</c> 编译期选不到 UnityEngine.Object 的
        /// == 重载，是纯引用判空，拦不住残骸；而贡献者渲染时读自己的 transform 是常态，
        /// 残骸一旦在册就是 MissingReferenceException，整段个体快照渲染不出来。
        /// 方向不能写成"只留 Unity 对象"——纯 C# 实现没有原生侧、不存在残骸态，那样会被静默吞掉
        /// </summary>
        private static bool IsDestroyed(object contributor)
        {
            return contributor is UnityEngine.Object unityObj && unityObj == null;
        }

        // 去重与移除一律按引用比（同 WorldSnapshotService 的写法）：List.Contains 走
        // EqualityComparer<T>.Default，贡献者若是 record 或自己重写了 Equals，
        // 两个不同实例会被判成"已注册"，第二条能力文本就这么静默丢了
        private static bool ContainsReference<T>(List<T> list, T item)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return true;
            return false;
        }

        private static bool RemoveReference<T>(List<T> list, T item)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!ReferenceEquals(list[i], item)) continue;

                list.RemoveAt(i);
                return true;
            }

            return false;
        }

        #endregion

        #region - 嵌套类型 -

        /// <summary>
        /// 个体段的一个片段：两把排序键 + 渲染好的文本（形状同世界的 LoreSegment）。
        /// 能力清单与状态清单共用一个类型——两者的片段结构一样，区别只在问的频率。
        /// 做成 struct：注册列表变化是冷路径，不该为它往堆上扔一批小对象
        /// </summary>
        private struct SelfSegment
        {
            public readonly int Order;
            public readonly string TypeKey;
            public readonly string Text;

            public SelfSegment(int order, string typeKey, string text)
            {
                Order = order;
                TypeKey = typeKey;
                Text = text;
            }
        }

        #endregion
    }
}
