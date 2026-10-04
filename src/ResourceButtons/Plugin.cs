using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;

namespace DiceVaders.ResourceButtons
{
    /// <summary>
    /// 每回合自动发放资源。
    ///
    /// ★ 为什么不做按钮：第一版做了两个可点击按钮，日志显示按钮对象建出来了
    ///   （"两个按钮已创建"），但屏幕上不显示 —— 自建 Canvas 上的新建 Image 在
    ///   该游戏的 UI 层级里没渲染出来。与其继续跟渲染管线较劲，改成回合驱动更稳。
    ///
    /// ═══ 回合检测（不 Hook，纯轮询）═══
    /// 每帧读两个 EncounterValue，任一发生变化即视为「新回合开始」：
    ///   TurnsRemaining = 9   回合剩余
    ///   RoundNumber    = 10  回合序号
    /// 这样不管游戏内部是用哪个字段推进回合，都能抓到。
    ///
    /// 写入路径：EncounterModel.SetValue(EncounterValue, object)
    ///   EncounterValue.ChronoToken = 3   （时空点）
    ///   EncounterValue.BudgeToken  = 2   （挪移）
    ///   EncounterController.EncounterModel 在 +0xC8
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Auto Resources", "1.1.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.resourcebuttons";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> GiveChrono;
        internal static ConfigEntry<bool> GiveBudge;
        internal static ConfigEntry<int> ChronoAmount;
        internal static ConfigEntry<int> BudgeAmount;
        internal static ConfigEntry<bool> GrantOnEntry;
        internal static ConfigEntry<bool> LogOnGrant;

        public override void Load()
        {
            Logger = Log;
            ModKitLog.Sink = m => Logger.LogInfo(m);

            Enabled = Config.Bind("1-开关", "Enabled", true,
                "开启「每回合自动发放资源」。");
            GiveChrono = Config.Bind("1-开关", "GiveChrono", true,
                "发放时空点（ChronoToken）。");
            GiveBudge = Config.Bind("1-开关", "GiveBudge", true,
                "发放挪移（BudgeToken）。");

            ChronoAmount = Config.Bind("2-数量", "ChronoAmount", 10,
                new ConfigDescription("每回合给多少时空点。", new AcceptableValueRange<int>(1, 999)));
            BudgeAmount = Config.Bind("2-数量", "BudgeAmount", 10,
                new ConfigDescription("每回合给多少挪移。", new AcceptableValueRange<int>(1, 999)));
            GrantOnEntry = Config.Bind("2-数量", "GrantOnEntry", true,
                "进入一局时立即先给一次（不等第一个回合开始）。");

            LogOnGrant = Config.Bind("3-调试", "LogOnGrant", true,
                "每次发放写一行日志（旧值→新值），便于核对。默认开 —— 出了问题读日志就能定位。");

            ModToggleRegistry.AutoResource = Enabled;
            ModToggleRegistry.AutoResourceChrono = GiveChrono;
            ModToggleRegistry.AutoResourceBudge = GiveBudge;
            ModToggleRegistry.ChronoAmount = ChronoAmount;
            ModToggleRegistry.BudgeAmount = BudgeAmount;
            ModToggleRegistry.Log = m => Logger.LogInfo(m);

            Logger.LogInfo("===== DiceVaders AutoResources v1.1.0 =====");

            ClassInjector.RegisterTypeInIl2Cpp<ResourceGranter>();
            var go = new GameObject("DiceVaders_AutoResources");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ResourceGranter>();
        }
    }

    public class ResourceGranter : MonoBehaviour
    {
        public ResourceGranter(IntPtr ptr) : base(ptr) { }

        private int _lastTurns = int.MinValue;
        private int _lastRound = int.MinValue;
        private bool _seenRun = false;

        private void Update()
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;

            var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
            if (ec == null)
            {
                // 离开对局 → 重置状态，下次进局重新初始化
                if (_seenRun) { _seenRun = false; _lastTurns = int.MinValue; _lastRound = int.MinValue; }
                return;
            }

            var em = Il2CppHelpers.Safe(() => ec.EncounterModel);
            if (em == null) return;

            int turns = em.GetIntValue(EncounterValue.TurnsRemaining);
            int round = em.GetIntValue(EncounterValue.RoundNumber);

            // 首次观察到这一局
            if (!_seenRun)
            {
                _seenRun = true;
                _lastTurns = turns;
                _lastRound = round;
                if (Plugin.GrantOnEntry != null && Plugin.GrantOnEntry.Value)
                    Grant(em, "进局");
                return;
            }

            // 回合推进 → 发放
            if (turns != _lastTurns || round != _lastRound)
            {
                if (Plugin.LogOnGrant != null && Plugin.LogOnGrant.Value)
                    Plugin.Logger?.LogInfo($"AutoResources: 回合变化 (剩{turns} 序{round})，上一状态 剩{_lastTurns} 序{_lastRound}");

                _lastTurns = turns;
                _lastRound = round;
                Grant(em, "回合");
            }
        }

        /// <summary>
        /// 发放两种资源。
        /// ★ 方法只接单个 IL2CPP 类型参数 —— 注入类的方法签名里不能出现托管类型
        ///   （StringBuilder / List&lt;T&gt; 都会被 Il2CppInterop 拒绝注册）。
        /// </summary>
        private void Grant(StarVaders.EncounterModel em, string tag)
        {
            try
            {
                if (Plugin.GiveChrono != null && Plugin.GiveChrono.Value)
                    AddValue(em, EncounterValue.ChronoToken, Plugin.ChronoAmount.Value, "时空点", tag);

                if (Plugin.GiveBudge != null && Plugin.GiveBudge.Value)
                    AddValue(em, EncounterValue.BudgeToken, Plugin.BudgeAmount.Value, "挪移", tag);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo($"AutoResources Grant 失败: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// 单个值写入。
        /// EncounterModel.SetValue(EncounterValue, Il2CppSystem.Object)
        /// Il2CppSystem.Object 自带 int 隐式转换运算符（见 Il2Cppmscorlib），直接传 int 即可。
        /// </summary>
        private void AddValue(StarVaders.EncounterModel em, EncounterValue key, int amount, string label, string tag)
        {
            try
            {
                int before = em.GetIntValue(key);
                int target = before + amount;
                em.SetValue(key, target);
                int after = em.GetIntValue(key);

                if (Plugin.LogOnGrant != null && Plugin.LogOnGrant.Value)
                    Plugin.Logger?.LogInfo($"AutoResources[{tag}]: {label} {before} -> {after}（写入 {target}）");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo($"AutoResources {label} 写入失败: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
