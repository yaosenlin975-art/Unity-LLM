/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 时间工具                 │
 * │ Remark      : 提供本机本地时间查询            │
 * │ ClassName   : AgentTimeTools                │
 * └────────────────────────────────────────────┘
 */

using System;
using Cysharp.Text;

namespace LLM.Runtime.Tools
{
    public static class AgentTimeTools
    {
        #region - 公开接口 -

        [AgentTool("get_current_time", "获取本机当前日期、时间与星期。")]
        public static string GetCurrentTime()
        {
            DateTime now = DateTime.Now;
            return ZString.Format("{0:yyyy-MM-dd HH:mm:ss} 星期{1}", now, GetWeekday(now.DayOfWeek));
        }

        #endregion

        #region - 私有方法 -

        private static string GetWeekday(DayOfWeek day)
        {
            return day switch
            {
                DayOfWeek.Monday => "一",
                DayOfWeek.Tuesday => "二",
                DayOfWeek.Wednesday => "三",
                DayOfWeek.Thursday => "四",
                DayOfWeek.Friday => "五",
                DayOfWeek.Saturday => "六",
                _ => "日"
            };
        }

        #endregion
    }
}
