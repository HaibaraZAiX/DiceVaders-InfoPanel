using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;
using UnityEngine.UI;

namespace DiceVaders.NativeSandbox
{
    /// <summary>
    /// 在游戏原生的「选项与更多 → 沙盒」面板里追加三行开关。
    ///
    /// ═══ 实证依据（全部来自 dump.cs + GameAssembly.dll 反汇编）═══
    ///
    /// SettingsSceneController  (RVA 0x1C39BE0 Awake)
    ///   0xE8 SkipAnimationsToggle        跳过侵略动画
    ///   0xF0 ShowCompletionStarsToggle   显示完成星标
    ///   0xF8 AssistModeToggle            辅助模式（每回合+1挪移和+1时空点）  ← 拿它当模板
    ///   0x100 RichModeToggle             富裕模式
    ///   0x108 GrowModeToggle             成长模式
    ///   0x110 RemoveHardPilotsToggle
    ///   0x118 DisableLockedArtifactSlotsToggle
    ///   0x120 DisableConstellationsToggle
    ///
    /// OptionView   0xB0 = Description (TextMeshProUGUI)  ← 行文字
    /// OptionToggle 0xC0 = Toggle      (UnityEngine.UI.Toggle) ← 开关本体
    ///
    /// ★ 做法：Hook Awake 的 Postfix，把 AssistModeToggle 的 GameObject 克隆三份，
    ///   挂到同一个父节点下。不接管原组件的回调 —— 改成每帧读 isOn 同步到注册表，
    ///   避开 Il2CppInterop 的委托编组坑。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Native Sandbox Rows", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.nativesandbox";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> Enabled;

        public override void Load()
        {
            Logger = Log;
            ModKitLog.Sink = m => Logger.LogInfo(m);
            ModToggleRegistry.Log = m => Logger.LogInfo(m);

            Enabled = Config.Bind("1-开关", "Enabled", true,
                "在游戏的「沙盒设置」面板里追加三行 mod 开关。");

            Logger.LogInfo("===== DiceVaders NativeSandbox v1.0.0 =====");

            ClassInjector.RegisterTypeInIl2Cpp<ToggleWatcher>();
            var go = new GameObject("DiceVaders_NativeSandbox");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ToggleWatcher>();

            var harmony = new Harmony("dicevaders.nativesandbox");
            harmony.PatchAll(typeof(SandboxRowInjector));
            Logger.LogInfo("NativeSandbox: Harmony 补丁已挂");
        }
    }

    /// <summary>把三行开关塞进沙盒面板。</summary>
    public static class SandboxRowInjector
    {
        /// <summary>注入后的三行，供 ToggleWatcher 轮询。null = 还没注入 / 面板已销毁。</summary>
        public static StarVaders.OptionToggle RowConstellation;
        public static StarVaders.OptionToggle RowResource;
        public static StarVaders.OptionToggle RowProbability;

        private static bool _logged;

        [HarmonyPatch(typeof(StarVaders.SettingsSceneController), nameof(StarVaders.SettingsSceneController.Awake))]
        [HarmonyPostfix]
        public static void Awake_Postfix(StarVaders.SettingsSceneController __instance)
        {
            if (Plugin.Enabled != null && !Plugin.Enabled.Value) return;
            try
            {
                Inject(__instance);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo("NativeSandbox 注入失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Inject(StarVaders.SettingsSceneController sc)
        {
            // 拿「辅助模式」这一行当模板 —— 它和我们加的行长得最像（都是普通开关键）
            var proto = sc.AssistModeToggle;
            if (proto == null)
            {
                Plugin.Logger?.LogInfo("NativeSandbox: AssistModeToggle 为 null，跳过注入");
                return;
            }

            var protoGo = proto.gameObject;
            if (protoGo == null) { Plugin.Logger?.LogInfo("NativeSandbox: 模板 GameObject 为空"); return; }

            var parent = protoGo.transform.parent;
            if (parent == null) { Plugin.Logger?.LogInfo("NativeSandbox: 模板没有父节点"); return; }

            Plugin.Logger?.LogInfo($"NativeSandbox: 模板父节点 = {parent.name}，层级 {parent.GetSiblingIndex()}");

            RowConstellation = CloneRow(protoGo, parent, 1, "★ Mod：星座可刷新",
                ModToggleRegistry.GetConstellationReroll());
            RowResource = CloneRow(protoGo, parent, 2, "★ Mod：每回合补充资源",
                ModToggleRegistry.GetAutoResource());
            RowProbability = CloneRow(protoGo, parent, 3, "★ Mod：显示稀有度概率",
                ModToggleRegistry.GetProbabilityDisplay());

            if (!_logged)
            {
                _logged = true;
                Plugin.Logger?.LogInfo(
                    $"NativeSandbox: 三行注入结果 星座={(RowConstellation != null)} " +
                    $"资源={(RowResource != null)} 概率={(RowProbability != null)}");
            }
        }

        /// <summary>
        /// 克隆一行。
        /// ★ 参数只用 IL2CPP 类型 + string + bool + int，避免被 Il2CppInterop 拒注册。
        /// </summary>
        private static StarVaders.OptionToggle CloneRow(GameObject protoGo, Transform parent, int index,
            string label, bool initial)
        {
            try
            {
                var clone = UnityEngine.Object.Instantiate(protoGo, parent);
                if (clone == null) return null;
                clone.name = "DV_ModRow_" + index;

                // 位置：模板下面依次排开（若容器有 LayoutGroup，它会自己接管，这里设了也无害）
                var rt = clone.GetComponent<RectTransform>();
                var prt = protoGo.GetComponent<RectTransform>();
                if (rt != null && prt != null)
                {
                    float step = (prt.sizeDelta.y > 1f ? prt.sizeDelta.y : 64f) + 8f;
                    rt.anchoredPosition = prt.anchoredPosition + new Vector2(0f, -step * index);
                }

                // 文字
                var ov = clone.GetComponent<StarVaders.OptionView>();
                if (ov != null && ov.Description != null) ov.Description.text = label;

                // 开关：设为初始值；不去动它的回调，由 ToggleWatcher 每帧读
                var ot = clone.GetComponent<StarVaders.OptionToggle>();
                if (ot != null && ot.Toggle != null)
                {
                    try { ot.Toggle.SetIsOnWithoutNotify(initial); }
                    catch { ot.Toggle.isOn = initial; }
                }

                return ot;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo($"NativeSandbox CloneRow({label}) 失败: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 每帧读三行的开关状态，同步进注册表 —— 功能插件据此开/关。
    ///
    /// 为什么不用 Toggle.onValueChanged 回调：
    ///   Il2CppInterop 的 UnityAction&lt;bool&gt; 委托编组有坑（托管委托 → il2cpp 委托），
    ///   轮询更稳且代价可忽略（三个 bool 比较）。
    /// </summary>
    public class ToggleWatcher : MonoBehaviour
    {
        public ToggleWatcher(IntPtr ptr) : base(ptr) { }

        private float _t;

        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            if (_t < 0.15f) return;   // 没必要每帧查
            _t = 0f;

            // ★ 三个开关各自内联展开 —— 不抽成带 Func/Action 参数的辅助方法：
            //   注入类的方法签名里出现托管类型（委托、StringBuilder、List<T>）
            //   会被 Il2CppInterop 拒绝注册。
            try
            {
                var r1 = SandboxRowInjector.RowConstellation;
                if (r1 != null && r1.Toggle != null)
                {
                    bool on = r1.Toggle.isOn;
                    if (on != ModToggleRegistry.GetConstellationReroll()) ModToggleRegistry.SetConstellationReroll(on);
                }
            }
            catch { }

            try
            {
                var r2 = SandboxRowInjector.RowResource;
                if (r2 != null && r2.Toggle != null)
                {
                    bool on = r2.Toggle.isOn;
                    if (on != ModToggleRegistry.GetAutoResource()) ModToggleRegistry.SetAutoResource(on);
                }
            }
            catch { }

            try
            {
                var r3 = SandboxRowInjector.RowProbability;
                if (r3 != null && r3.Toggle != null)
                {
                    bool on = r3.Toggle.isOn;
                    if (on != ModToggleRegistry.GetProbabilityDisplay()) ModToggleRegistry.SetProbabilityDisplay(on);
                }
            }
            catch { }
        }
    }
}
