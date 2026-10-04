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
    /// 资源按钮 —— 屏幕上两个可点击按钮，点一下给一笔时空点 / 挪移。
    ///
    /// 写入路径：EncounterModel.SetValue(EncounterValue, object)
    ///   EncounterValue.ChronoToken = 3   （时空点）
    ///   EncounterValue.BudgeToken  = 2   （挪移）
    ///   EncounterController.EncounterModel 在 +0xC8
    ///
    /// ★ 为什么不调 DebugController.GainChrono()：
    ///   反汇编那两个函数后发现它们是「UI 特效路径」——会 Instantiate 浮动文字对象、
    ///   播动画。连点 10 次会叠 10 个特效对象。直接写 EncounterModel 才是干净的值写入。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Resource Buttons", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.resourcebuttons";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Amount;
        internal static ConfigEntry<float> OffsetX;
        internal static ConfigEntry<float> OffsetY;
        internal static ConfigEntry<float> ButtonWidth;
        internal static ConfigEntry<float> ButtonHeight;
        internal static ConfigEntry<float> Gap;
        internal static ConfigEntry<float> FontSize;
        internal static ConfigEntry<bool> LogOnClick;

        public override void Load()
        {
            Logger = Log;
            ModKitLog.Sink = m => Logger.LogInfo(m);

            Enabled = Config.Bind("1-开关", "Enabled", true,
                "显示资源按钮（屏幕左下角两个按钮）。");
            Amount = Config.Bind("1-开关", "Amount", 10,
                new ConfigDescription("每次点击增加的数值。", new AcceptableValueRange<int>(1, 999)));
            LogOnClick = Config.Bind("1-开关", "LogOnClick", true,
                "每次点击在日志里写一行（值 旧→新），便于核对到底有没有写进去。");

            OffsetX = Config.Bind("2-位置", "OffsetX", 30f,
                new ConfigDescription("按钮组距屏幕左边缘像素。", new AcceptableValueRange<float>(0f, 1200f)));
            OffsetY = Config.Bind("2-位置", "OffsetY", 60f,
                new ConfigDescription("按钮组距屏幕底部像素。", new AcceptableValueRange<float>(0f, 900f)));
            ButtonWidth = Config.Bind("2-位置", "ButtonWidth", 170f,
                new ConfigDescription("单个按钮宽度。", new AcceptableValueRange<float>(80f, 500f)));
            ButtonHeight = Config.Bind("2-位置", "ButtonHeight", 48f,
                new ConfigDescription("按钮高度。", new AcceptableValueRange<float>(24f, 200f)));
            Gap = Config.Bind("2-位置", "Gap", 12f,
                new ConfigDescription("两个按钮之间的间距。", new AcceptableValueRange<float>(0f, 200f)));
            FontSize = Config.Bind("2-位置", "FontSize", 20f,
                new ConfigDescription("按钮文字字号。", new AcceptableValueRange<float>(8f, 48f)));

            Logger.LogInfo("===== DiceVaders ResourceButtons v1.0.0 =====");

            ClassInjector.RegisterTypeInIl2Cpp<ResourcePanel>();
            var go = new GameObject("DiceVaders_ResourceButtons");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ResourcePanel>();
        }
    }

    public class ResourcePanel : MonoBehaviour
    {
        public ResourcePanel(IntPtr ptr) : base(ptr) { }

        private GameObject _canvas;
        private GameObject _btnChrono;
        private GameObject _btnBudge;
        private TMPro.TMP_FontAsset _font;

        private void Start() { BuildUI(); }

        private void BuildUI()
        {
            try
            {
                _canvas = UiInjector.GetOverlayCanvas("DiceVaders_ResourceCanvas", 31500);
                if (_canvas == null) { Plugin.Logger?.LogInfo("ResourceButtons: Canvas 创建失败"); return; }

                float fs = 20f;
                _font = UiInjector.FindAnyFont(out fs);

                _btnChrono = MakeButton("BtnChrono", "+" + Plugin.Amount.Value + " 时空点");
                _btnBudge = MakeButton("BtnBudge", "+" + Plugin.Amount.Value + " 挪移");

                Plugin.Logger?.LogInfo("ResourceButtons: 两个按钮已创建");
            }
            catch (Exception e) { Plugin.Logger?.LogInfo("ResourceButtons BuildUI 失败: " + e.Message); }
        }

        /// <summary>
        /// 建一个按钮。锚点取屏幕【左下角】，x 用 index 错开：
        /// index 0 贴 OffsetX，index 1 在它右边 ButtonWidth + Gap。
        /// </summary>
        private GameObject MakeButton(string name, string label)
        {
            bool isSecond = name.Contains("Budge");
            float x = Plugin.OffsetX.Value + (isSecond ? Plugin.ButtonWidth.Value + Plugin.Gap.Value : 0f);

            var go = UiInjector.MakeSolidButton(
                label,
                Plugin.ButtonWidth.Value,
                Plugin.ButtonHeight.Value,
                _font,
                Plugin.FontSize.Value,
                new Color(0.08f, 0.30f, 0.36f, 0.92f),
                _canvas.transform);

            if (go == null) return null;

            go.name = "DV_" + name;
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = new Vector2(x, Plugin.OffsetY.Value);
            }
            return go;
        }

        private void Update()
        {
            if (Plugin.Enabled == null) return;

            // 开关
            bool want = Plugin.Enabled.Value;
            if (_btnChrono != null && _btnChrono.activeSelf != want) _btnChrono.SetActive(want);
            if (_btnBudge != null && _btnBudge.activeSelf != want) _btnBudge.SetActive(want);
            if (!want) return;

            // 位置跟随配置（改了 cfg 不用重启）
            SyncPositions();
            // 只在有对局时显示
            var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
            bool inRun = ec != null;
            if (_btnChrono != null && _btnChrono.activeSelf != inRun) _btnChrono.SetActive(inRun);
            if (_btnBudge != null && _btnBudge.activeSelf != inRun) _btnBudge.SetActive(inRun);
            if (!inRun) return;

            if (UiInjector.HitTest(_btnChrono)) Grant(ec, EncounterValue.ChronoToken, "时空点");
            if (UiInjector.HitTest(_btnBudge)) Grant(ec, EncounterValue.BudgeToken, "挪移");
        }

        private void SyncPositions()
        {
            try
            {
                if (_btnChrono != null)
                {
                    var rt = _btnChrono.GetComponent<RectTransform>();
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(Plugin.OffsetX.Value, Plugin.OffsetY.Value);
                    var t = _btnChrono.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (t != null && Math.Abs(t.fontSize - Plugin.FontSize.Value) > 0.01f)
                        t.fontSize = Plugin.FontSize.Value;
                }
                if (_btnBudge != null)
                {
                    var rt = _btnBudge.GetComponent<RectTransform>();
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(
                            Plugin.OffsetX.Value + Plugin.ButtonWidth.Value + Plugin.Gap.Value,
                            Plugin.OffsetY.Value);
                    var t = _btnBudge.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (t != null && Math.Abs(t.fontSize - Plugin.FontSize.Value) > 0.01f)
                        t.fontSize = Plugin.FontSize.Value;
                }
            }
            catch { }
        }

        /// <summary>
        /// 给一笔资源。
        /// ★ 方法只接 IL2CPP 类型 + 原生类型，避免 Il2CppInterop 拒绝注册注入类。
        /// </summary>
        private void Grant(StarVaders.EncounterController ec, EncounterValue key, string label)
        {
            try
            {
                var em = ec.EncounterModel;
                if (em == null) { Plugin.Logger?.LogInfo($"ResourceButtons: EncounterModel 为空（{label}）"); return; }

                int cur = em.GetIntValue(key);
                int next = cur + Plugin.Amount.Value;

                // EncounterModel.SetValue(EncounterValue, Il2CppSystem.Object)
                //   Il2CppSystem.Object 自带 int 的隐式转换运算符，直接传 int 即可
                //   （见 Il2Cppmscorlib 里 "public static implicit operator Object(int value)"）
                em.SetValue(key, next);

                if (Plugin.LogOnClick != null && Plugin.LogOnClick.Value)
                {
                    int after = em.GetIntValue(key);
                    Plugin.Logger?.LogInfo($"ResourceButtons: {label} {cur} -> {after} (写入 {next})");
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo($"ResourceButtons Grant({label}) 失败: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
