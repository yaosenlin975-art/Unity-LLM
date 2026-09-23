/*
┌────────────────────────────┐
│　Description: 按类名解析并实例化存储实现
│　Remark: 只认已加载程序集，所以实现类须在装配根首次 Install 前
│　　　　　 加载完（ADR-030）；本类不记日志也不抛，失败原因回给
│　　　　　 调用方攒成一条 Error
│　ClassName: StoreTypeResolver
└────────────────────────────┘
*/

using System;
using Cysharp.Text;
using UnityEngine;

namespace LLM.Runtime
{
    public static class StoreTypeResolver
    {
        /// <summary>
        /// 空串/空白 → 返回 null 且 error 为 null，表示"未配置"，不是错误。
        /// 其余失败 → null + error 说明原因。
        /// </summary>
        public static Type Resolve(string typeFullName, Type requiredInterface, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(typeFullName)) return null;

            // 逐程序集按名字直查，不 GetTypes() 遍历：后者在含加载失败类型的程序集上抛
            // ReflectionTypeLoadException（AgentToolScanner 为此专门包了 try）
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;

                Type type;
                try
                {
                    type = asm.GetType(typeFullName, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (type is null) continue;

                return Check(type, requiredInterface, out error);
            }

            error = ZString.Format("类型 {0} 不在任何已加载程序集中（未编译进来，或装配时还没加载）", typeFullName);
            return null;
        }

        /// <summary>解析 + 实例化的唯一入口。任何失败都回 null + error，绝不向外抛。</summary>
        public static T Create<T>(string typeFullName, out string error) where T : class
        {
            var type = Resolve(typeFullName, typeof(T), out error);
            if (type is null) return null;

            try
            {
                return Activator.CreateInstance(type) as T;
            }
            catch (Exception ex)
            {
                // 构造抛不能炸装配根：一个自定义实现的 ctor 异常不该让所有 agent 起不来
                error = ZString.Format("实例化 {0} 失败: {1}", typeFullName, ex.GetBaseException().Message);
                return null;
            }
        }

        private static Type Check(Type type, Type requiredInterface, out string error)
        {
            error = null;

            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            {
                error = ZString.Format("{0} 不是可实例化的具体类", type.FullName);
                return null;
            }

            // MonoBehaviour / ScriptableObject 都从 UnityEngine.Object 派生，一并挡掉：
            // 存储实现约定是"可 new 的无参策略对象"
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                error = ZString.Format("{0} 继承 UnityEngine.Object，不能靠 new 创建", type.FullName);
                return null;
            }

            if (!requiredInterface.IsAssignableFrom(type))
            {
                error = ZString.Format("{0} 未实现 {1}", type.FullName, requiredInterface.Name);
                return null;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                error = ZString.Format("{0} 没有公开无参构造", type.FullName);
                return null;
            }

            return type;
        }
    }
}
