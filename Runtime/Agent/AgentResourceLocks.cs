/*
┌────────────────────────────┐
│　Description: 跨 agent 资源锁
│　Remark: 同 key FIFO 排队、等待超时与退
│　　　　　 队。释放只走 LockLease.Dispose
│　ClassName: AgentResourceLocks
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;

namespace LLM.Runtime.Agent
{
    /// <summary>
    /// 一次持锁的凭据。Dispose 幂等且带持有者身份，所以不提供 Release(key)：
    /// 执行器的 finally 与轮级清理是两个释放点，无身份的释放会放掉下一个持有者的锁。
    /// </summary>
    public sealed class LockLease : IDisposable
    {
        /// <summary>空键凭据：这个动作不独占资源，Dispose 是空操作</summary>
        internal readonly string Key;
        internal readonly string OwnerId;
        internal readonly int Generation;
        internal readonly int Serial;

        /// <summary>等待中的到期时刻（内核时钟）。拿到锁后无意义</summary>
        internal readonly float Deadline;

        internal UniTaskCompletionSource<LockLease> Signal;
        internal CancellationTokenRegistration CancelRegistration;

        /// <summary>移交已定（拿到 / 超时 / 退队），不再参与排队</summary>
        internal bool Settled;

        internal bool Released;

        internal LockLease(string key, string ownerId, int generation, float deadline)
        {
            Key = key;
            OwnerId = ownerId;
            Generation = generation;
            Serial = AgentResourceLocks.NextSerial();
            Deadline = deadline;
        }

        public void Dispose()
        {
            AgentResourceLocks.Release(this);
        }

        /// <summary>owner 的轮被叫停 / agent 销毁：立即退队，把位置让给后面的人</summary>
        internal void Abandon()
        {
            AgentResourceLocks.CancelWaiting(this);
        }
    }

    /// <summary>
    /// 全局静态：两个 NPC 抢同一目标是跨实例问题，按 key 共享一条队列。
    /// 所有 await 延续都回主线程，故不额外加锁。
    /// </summary>
    public static class AgentResourceLocks
    {
        private static readonly Dictionary<string, LockLease> holders = new();

        /// <summary>同 key 一条 FIFO。用 List 而不是 Queue：等待者被取消时要能从中段摘除</summary>
        private static readonly Dictionary<string, List<LockLease>> queues = new();
        private static int nextSerial;

        internal static int NextSerial() => ++nextSerial;

        /// <summary>
        /// 取锁。waitSeconds = 0 表示不排队，占用中直接失败。
        /// 返回 null 表示没拿到（占用且排队到期），执行器按失败回填且不计入循环检测计数。
        /// </summary>
        public static async UniTask<LockLease> TryAcquireAsync(string key, int waitSeconds,
            string ownerId, int generation, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(key))
                return new LockLease(null, ownerId, generation, float.MaxValue);

            ExpireWaiters(key);

            if (!holders.TryGetValue(key, out var holder) || holder == null)
            {
                var immediate = new LockLease(key, ownerId, generation, float.MaxValue);
                holders[key] = immediate;
                return immediate;
            }

            if (waitSeconds <= 0) return null;

            var waiting = new LockLease(key, ownerId, generation, AgentCore.NowSeconds() + waitSeconds)
            {
                Signal = new UniTaskCompletionSource<LockLease>()
            };
            waiting.CancelRegistration = ct.Register(waiting.Abandon);
            GetQueue(key).Add(waiting);

            var handed = await waiting.Signal.Task;
            waiting.CancelRegistration.Dispose();

            return ReferenceEquals(handed, waiting) ? waiting : null;
        }

        /// <summary>当前排队人数（不含持有者）。测试 seam</summary>
        public static int WaitingCount(string key)
        {
            if (!queues.TryGetValue(key, out var queue)) return 0;
            ExpireWaiters(key);
            return queue.Count;
        }

        /// <summary>清空全部锁与队列。测试 seam，也供沙盒重置用</summary>
        public static void Reset()
        {
            foreach (var queue in queues.Values)
                foreach (var lease in queue)
                    lease.CancelRegistration.Dispose();

            queues.Clear();
            holders.Clear();
            nextSerial = 0;
        }

        internal static void Release(LockLease lease)
        {
            if (lease == null || lease.Released) return;
            lease.Released = true;
            lease.CancelRegistration.Dispose();

            if (lease.Key == null) return;
            if (!queues.TryGetValue(lease.Key, out var queue)) return;

            if (holders.TryGetValue(lease.Key, out var holder) && ReferenceEquals(holder, lease))
            {
                holders.Remove(lease.Key);
                HandOffNext(lease.Key, queue);
                return;
            }

            // 没拿到过锁的等待者被取消时已自行出队，这里只兜住漏网的
            if (queue.Contains(lease))
                queue.Remove(lease);
        }

        internal static void CancelWaiting(LockLease lease)
        {
            if (lease == null || lease.Settled || lease.Key == null) return;
            if (!queues.TryGetValue(lease.Key, out var queue) || !queue.Contains(lease)) return;

            queue.Remove(lease);
            lease.Settled = true;
            lease.Signal.TrySetResult(null);
        }

        /// <summary>到期与已取消的队首出队。由取锁、排队人数查询和 AgentCore 时钟泵调用</summary>
        public static void PumpKey(string key)
        {
            ExpireWaiters(key);
        }

        private static void ExpireWaiters(string key)
        {
            if (!queues.TryGetValue(key, out var queue)) return;

            float now = AgentCore.NowSeconds();

            while (queue.Count > 0)
            {
                var head = queue[0];
                if (head.Settled)
                {
                    queue.RemoveAt(0);
                    continue;
                }

                if (head.Deadline > now) break;

                queue.RemoveAt(0);
                head.Settled = true;
                head.Signal.TrySetResult(null);

                Log.Warning(nameof(AgentResourceLocks),
                    ZString.Format("等锁 {0} 超时：agent {1} 第 {2} 次排队申请未获得",
                        key, head.OwnerId, head.Serial));
            }
        }

        private static void HandOffNext(string key, List<LockLease> queue)
        {
            float now = AgentCore.NowSeconds();

            while (queue.Count > 0)
            {
                var next = queue[0];
                queue.RemoveAt(0);

                if (next.Settled) continue;

                next.Settled = true;

                if (next.Deadline <= now)
                {
                    next.Signal.TrySetResult(null);
                    continue;
                }

                holders[key] = next;
                next.Signal.TrySetResult(next);
                return;
            }

            queues.Remove(key);
        }

        private static List<LockLease> GetQueue(string key)
        {
            if (!queues.TryGetValue(key, out var queue))
            {
                queue = new List<LockLease>();
                queues[key] = queue;
            }
            return queue;
        }
    }
}
