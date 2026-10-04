using System;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;

namespace DiceVaders.ShopInfo
{
    /// <summary>
    /// 商店出货概率实时面板。
    ///
    /// 为什么做这个：游戏里「商店稀有度」只是一个数字，玩家看不出它到底意味着什么。
    /// 实测（Ghidra 伪代码 + 从 GameAssembly.dll 读出的浮点常量）证明它确实在驱动
    /// 各档位物品的出现概率，只是游戏没把它换算出来给玩家看。
    ///
    /// ═══ 概率公式（全部实证，非推测）═══
    ///
    /// EncounterModel.GetCurrentShopRarity()  (RVA 0x1D31110)：
    ///     w = MIN((ActNumber - 1) * 5, 20) + GetIntValue(ShopRarity)
    ///                                        ↑ EncounterValue 18      ↑ EncounterValue 11
    ///   这个 w 就是「权重基数」。
    ///
    /// ContentGetter.GetRandomShopEntity()  (RVA 0x1CFC7F0)：
    ///     fLegendary = w * 0.001 - 0.03      ← 常量取自 VA 0x183A24B2C / 0x183A24BA0
    ///     fRare      = w * 0.002 + 0.03      ← VA 0x183A24B30 / 0x183A24BA0
    ///     fUncommon  = w * 0.015 + 0.1       ← VA 0x183A24B6C / 0x183A24C00
    ///     r = Random.NextDouble();
    ///     r < fLegendary                  → 传说
    ///     r < fLegendary + fRare          → 稀有
    ///     r < fLegendary + fRare + fUncommon → 罕见
    ///     else                            → 普通
    ///
    /// ContentGetter.GetChanceForRarity()  (RVA 0x1CFC450) —— 神器用同一组曲线，
    /// 但「普通」档是补数：0.91 - (其余三档之和)，上限常量取自 VA 0x183A24E00 = 0.91。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders ShopInfo", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.shopinfo";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> ShowPanel;
        internal static ConfigEntry<float> OffsetX;
        internal static ConfigEntry<float> RightOffsetX;
        internal static ConfigEntry<float> OffsetY;
        internal static ConfigEntry<float> FontSize;
        internal static ConfigEntry<bool> ShowArtifactProbs;
        internal static ConfigEntry<bool> ShowPoolSummary;
        internal static ConfigEntry<bool> ShowHiddenValues;
        internal static ConfigEntry<float> ExtraOffsetY;
        internal static ConfigEntry<bool> LogOnRarityChange;

        public override void Load()
        {
            Logger = base.Log;
            ModKitLog.Sink = m => Logger.LogInfo(m);

            ShowPanel = Config.Bind("1-显示", "ShowPanel", true, "显示商店出货概率面板。");
            OffsetX = Config.Bind("1-显示", "OffsetX", 20f,
                new ConfigDescription("左列距屏幕左边缘像素。", new AcceptableValueRange<float>(0f, 900f)));
            RightOffsetX = Config.Bind("1-显示", "RightOffsetX", 460f,
                new ConfigDescription("右列距屏幕右边缘像素（默认 460，落在「发射！」按钮左边）。",
                    new AcceptableValueRange<float>(0f, 900f)));
            OffsetY = Config.Bind("1-显示", "OffsetY", 140f,
                new ConfigDescription("两列距屏幕底部像素。", new AcceptableValueRange<float>(0f, 900f)));
            FontSize = Config.Bind("1-显示", "FontSize", 15f,
                new ConfigDescription("字号。", new AcceptableValueRange<float>(8f, 40f)));
            ShowArtifactProbs = Config.Bind("1-显示", "ShowArtifactProbs", true,
                "在右侧显示神器物品的稀有度概率（与左侧商品棋子共用同一套曲线）。");
            ShowPoolSummary = Config.Bind("1-显示", "ShowPoolSummary", false,
                "（保留项）显示「本局神器池」统计。默认关，日志里仍会记录可获得数量。");

            // ── 隐藏数值：游戏从不在界面上显示、但实际影响战斗的那些 EncounterValue ══
            // 对应差评里骂的 BOSS 机制（兽化 / 怒气 / 蜂群倍率 / 血祭 等）。
            ShowHiddenValues = Config.Bind("1-显示", "ShowHiddenValues", true,
                "显示隐藏的对局数值（全局倍率 / 怒气 / 兽化回合 / 蜂群倍率 / 献祭% / 增益% / 最终BOSS血量 / 跳过量）。\n" +
                "★ 只在数值非零时显示，全为零时这一块会整体隐藏，不占地方。");
            ExtraOffsetY = Config.Bind("1-显示", "HiddenValuesOffsetY", 300f,
                new ConfigDescription("隐藏数值块距屏幕底部像素（它在左列上方）。",
                    new AcceptableValueRange<float>(0f, 900f)));
            LogOnRarityChange = Config.Bind("2-调试", "LogOnRarityChange", true,
                "权重基数变化时在日志里打印一行，方便对照游戏内数值。");

            Logger.LogInfo("===== DiceVaders ShopInfo v1.0.0 =====");

            ClassInjector.RegisterTypeInIl2Cpp<InfoPanel>();
            var go = new GameObject("DiceVaders_ShopInfo");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<InfoPanel>();
        }
    }

    public class InfoPanel : MonoBehaviour
    {
        public InfoPanel(IntPtr ptr) : base(ptr) { }

        // ═══ 概率常量（从 GameAssembly.dll 实测读出）═══
        private const float K_LEGENDARY = 0.001f;   // VA 0x183A24B2C
        private const float K_RARE = 0.002f;        // VA 0x183A24B30
        private const float K_UNCOMMON = 0.015f;    // VA 0x183A24B6C
        private const float C_A = 0.03f;            // VA 0x183A24BA0
        private const float C_B = 0.1f;             // VA 0x183A24C00

        private GameObject _canvas;
        private TMPro.TextMeshProUGUI _textLeft;    // 左下：商品棋子概率
        private TMPro.TextMeshProUGUI _textRight;   // 右下：神器物品概率
        private TMPro.TextMeshProUGUI _textExtra;   // 隐藏数值（只在非零时显示）
        private float _nextRefresh;
        private int _lastW = int.MinValue;

        private void Start() { BuildUI(); }

        /// <summary>
        /// 建两个竖排小文本框，分别贴屏幕左边缘与右边缘（垂直居中）。
        /// 左：商品棋子各稀有度概率；右：神器各稀有度概率。
        /// </summary>
        private void BuildUI()
        {
            try
            {
                _canvas = UiInjector.GetOverlayCanvas("DiceVaders_ShopInfoCanvas", 31000);
                if (_canvas == null) { Plugin.Logger?.LogInfo("ShopInfo: Canvas 创建失败"); return; }

                _textLeft = MakeText("InfoLeft", anchoredLeft: true);
                _textRight = MakeText("InfoRight", anchoredLeft: false);
                _textExtra = MakeText("InfoExtra", anchoredLeft: true);

                Plugin.Logger?.LogInfo("ShopInfo: 左右竖排文本已创建");
            }
            catch (Exception e) { Plugin.Logger?.LogInfo("ShopInfo BuildUI 失败: " + e.Message); }
        }

        /// <summary>
        /// 建一个竖排文本框。
        /// 左列锚在屏幕【左下角】、右列锚在【右下角】，pivot 同为 (x,0) —— 文本各自向上生长。
        /// OffsetX 控制左列距左边缘，RightOffsetX 控制右列距右边缘；OffsetY 是两列距底部的共同高度。
        /// </summary>
        private TMPro.TextMeshProUGUI MakeText(string name, bool anchoredLeft)
        {
            var go = new GameObject(name);
            go.layer = 5;
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            float ax = anchoredLeft ? 0f : 1f;
            rt.anchorMin = new Vector2(ax, 0f);
            rt.anchorMax = new Vector2(ax, 0f);
            rt.pivot = new Vector2(ax, 0f);
            rt.sizeDelta = new Vector2(240f, 200f);

            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            float fs = 15f;
            var font = UiInjector.FindAnyFont(out fs);
            if (font != null) t.font = font;
            t.fontSize = Plugin.FontSize.Value;
            t.color = new Color(0.88f, 0.94f, 1f, 0.92f);
            t.alignment = anchoredLeft ? TMPro.TextAlignmentOptions.Left : TMPro.TextAlignmentOptions.Right;
            t.richText = true;
            try { t.enableAutoSizing = false; } catch { }
            t.text = "";
            return t;
        }

        private void Update()
        {
            try
            {
                if (Plugin.ShowPanel != null && !Plugin.ShowPanel.Value)
                {
                    ClearAll();
                    return;
                }

                // 位置：左列贴左下角偏移 OffsetX，右列贴右下角偏移 RightOffsetX，两列同高 OffsetY
                if (_textLeft != null)
                {
                    var rt = _textLeft.rectTransform;
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(Plugin.OffsetX.Value, Plugin.OffsetY.Value);
                    if (Math.Abs(_textLeft.fontSize - Plugin.FontSize.Value) > 0.01f)
                        _textLeft.fontSize = Plugin.FontSize.Value;
                }
                if (_textRight != null)
                {
                    var rt = _textRight.rectTransform;
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(-Plugin.RightOffsetX.Value, Plugin.OffsetY.Value);
                    if (Math.Abs(_textRight.fontSize - Plugin.FontSize.Value) > 0.01f)
                        _textRight.fontSize = Plugin.FontSize.Value;
                }
                // 隐藏数值块：左列上方
                if (_textExtra != null)
                {
                    var rt = _textExtra.rectTransform;
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(Plugin.OffsetX.Value, Plugin.ExtraOffsetY.Value);
                    if (Math.Abs(_textExtra.fontSize - Plugin.FontSize.Value) > 0.01f)
                        _textExtra.fontSize = Plugin.FontSize.Value;
                }

                // 每 0.2 秒刷新一次即可（数值不会每帧变）
                if (Time.realtimeSinceStartup < _nextRefresh) return;
                _nextRefresh = Time.realtimeSinceStartup + 0.2f;
                Refresh();
            }
            catch { }
        }

        private void Refresh()
        {
            // ★ 星座界面有自己的按钮（刷新 / 刷新全部），概率列会和它们重叠 —— 那里整块隐藏。
            //   实测截图：右列正好压在「刷新」按钮上。
            bool constellationShowing = false;
            try { constellationShowing = StarVaders.ConstellationController.IsShowing; } catch { }
            if (constellationShowing) { ClearAll(); return; }

            var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
            if (ec == null) { ClearAll(); return; }

            StarVaders.EncounterModel em = null;
            try { em = ec.EncounterModel; } catch { }
            if (em == null) { ClearAll(); return; }

            int w = 0;
            try { w = em.GetCurrentShopRarity(); } catch { }

            if (w != _lastW)
            {
                _lastW = w;
                if (Plugin.LogOnRarityChange != null && Plugin.LogOnRarityChange.Value)
                    Plugin.Logger?.LogInfo($"[ShopInfo] 权重基数 W 变为 {w}");
            }

            // ── 概率：按【累积分布】算（游戏是 r<c1→传说; r<c2→稀有; r<c3→罕见; else→普通）──
            float f16 = w * K_LEGENDARY - C_A;
            float f15 = w * K_RARE + C_A;
            float f5 = w * K_UNCOMMON + C_B;

            float c1 = Clamp01(f16);
            float c2 = Clamp01(f16 + f15);
            float c3 = Clamp01(f16 + f15 + f5);
            if (c2 < c1) c2 = c1;
            if (c3 < c2) c3 = c2;

            float pLeg = c1;
            float pRare = c2 - c1;
            float pUnc = c3 - c2;
            float pCom = 1f - c3;

            // ── 左：商品棋子各稀有度概率（竖排，一行一个）──
            SetText(_textLeft,
                "<size=85%><color=#9FB4C7>商品棋子</color></size>\n" +
                $"<color=#FFB800>传说</color> {pLeg * 100f:0.0}%\n" +
                $"<color=#C77DFF>稀有</color> {pRare * 100f:0.0}%\n" +
                $"<color=#4FC3F7>罕见</color> {pUnc * 100f:0.0}%\n" +
                $"<color=#B0BEC5>普通</color> {pCom * 100f:0.0}%");

            // ── 右：神器物品各稀有度概率（同一套曲线，数值相同）──
            if (Plugin.ShowArtifactProbs != null && Plugin.ShowArtifactProbs.Value)
                SetText(_textRight,
                    "<size=85%><color=#9FB4C7>神器物品</color></size>\n" +
                    $"<color=#FFB800>传说</color> {pLeg * 100f:0.0}%\n" +
                    $"<color=#C77DFF>稀有</color> {pRare * 100f:0.0}%\n" +
                    $"<color=#4FC3F7>罕见</color> {pUnc * 100f:0.0}%\n" +
                    $"<color=#B0BEC5>普通</color> {pCom * 100f:0.0}%");
            else
                SetText(_textRight, "");

            // ── 隐藏数值块（只在非零时才有内容）──
            if (Plugin.ShowHiddenValues != null && Plugin.ShowHiddenValues.Value)
                SetText(_textExtra, BuildHiddenValues(em));
            else
                SetText(_textExtra, "");
        }

        private void ClearAll()
        {
            SetText(_textLeft, "");
            SetText(_textRight, "");
            SetText(_textExtra, "");
        }

        /// <summary>
        /// 隐藏数值 —— 游戏界面从不显示、但实际参与战斗计算的那些 EncounterValue。
        ///
        /// 对应差评里被骂得最凶的 BOSS 机制（兽化 / 怒气 / 蜂群倍率 / 血祭）。
        /// 数据来源：EncounterModel.GetIntValue(EncounterValue)  RVA 0x1D2FA40
        ///
        /// ★ 只在数值非零时才拼进字符串 —— 平时这一块完全空白，不占视觉空间。
        /// ★ 方法只接 IL2CPP 类型（EncounterModel），返回 string，避免 Il2CppInterop 拒注册。
        /// </summary>
        private string BuildHiddenValues(StarVaders.EncounterModel em)
        {
            var sb = new StringBuilder();

            // 先收集，最后统一判断是否有内容
            AppendHiddenInt(sb, em, EncounterValue.GlobalMult, "全局倍率", "#FFB800");
            AppendHiddenInt(sb, em, EncounterValue.HiveMindMult, "蜂群倍率", "#C77DFF");
            AppendHiddenInt(sb, em, EncounterValue.Rage, "怒气", "#FF7043");
            AppendHiddenInt(sb, em, EncounterValue.BeastTurn, "兽化回合", "#FF7043");
            AppendHiddenInt(sb, em, EncounterValue.SacrificePercent, "献祭", "#EF5350");
            AppendHiddenInt(sb, em, EncounterValue.BoostPercent, "增益", "#66BB6A");
            AppendHiddenInt(sb, em, EncounterValue.CurrentSkipAmount, "跳过量", "#9FB4C7");
            AppendHiddenInt(sb, em, EncounterValue.FinalBossHP, "BOSS血量", "#EF5350");

            if (sb.Length == 0) return "";

            // 加个表头，说明这不是常规信息
            return "<size=85%><color=#7E8C99>隐藏数值</color></size>\n" + sb.ToString();
        }

        /// <summary>
        /// 若该 EncounterValue 非零则追加一行。
        /// ★ EncounterValue 在【全局命名空间】（不在 StarVaders 下）。
        /// ★ 用 EncounterValueTypeConfig.IsInt() 先判类型 —— 这些值里既有 int 也有 BigDouble 存法，
        ///   对 BigDouble 存法调用 GetIntValue 会抛异常。
        /// </summary>
        private void AppendHiddenInt(StringBuilder sb, StarVaders.EncounterModel em,
            EncounterValue key, string label, string color)
        {
            try
            {
                if (!EncounterValueTypeConfig.IsInt(key)) return;
                int v = em.GetIntValue(key);
                if (v != 0) sb.AppendLine($"<color={color}>{label}</color> {v}");
            }
            catch { }
        }


        // ★ 用两个无参/单 string 参数的小方法分别写左右文本：
        //   把 TextMeshProUGUI 当参数传同样有被 Il2CppInterop 拒注册的风险。
        private void SetText(TMPro.TextMeshProUGUI t, string s)
        {
            try
            {
                if (t == null) return;
                if (t.text != s) { t.text = s; try { t.ForceMeshUpdate(); } catch { } }
            }
            catch { }
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
