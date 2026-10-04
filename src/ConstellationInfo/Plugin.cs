using System;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;

namespace DiceVaders.ConstellationInfo
{
    /// <summary>
    /// 星座神器池面板 —— 把每个星座提供的【完整】神器列表显示出来。
    ///
    /// 为什么做这个：
    ///   游戏卡片只画前 3 个神器【图标】（连名字都没有），超出的部分永远看不到。
    ///   差评原文："这个星座系统非常不明所以……把一些需求的构筑升级让人直接前期找不到"。
    ///
    /// ═══ 实证依据（Ghidra 伪代码）═══
    ///
    /// Constellation.SetConstellationToArtifact()  (RVA 0x1CECA30)：
    ///     if (artifactModel.UniqueArtifacts.Count < 1) {       // +0x78 为空
    ///         if (artifactModel.NestedEntity == 0) return;     // +0x54，显示单个单位
    ///         ... EntityFactory.CreateEntityModel(NestedEntity) 填图鉴条目 ...
    ///     } else {
    ///         while (...) {
    ///             ArtifactView.SetModel(view[i], CreateArtifactModel(UniqueArtifacts[i]), ...);
    ///             i++;
    ///             if (2 < i) return;                           // ★ 最多 3 个，多的丢弃
    ///         }
    ///     }
    ///
    /// 字段偏移（ArtifactModel，TDI 3831）：
    ///     +0x18 ArtifactName        +0x20 Rarity
    ///     +0x54 EntityName NestedEntity
    ///     +0x78 CloneableList&lt;ArtifactName&gt; UniqueArtifacts
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders ConstellationInfo", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.constellationinfo";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> ShowPanel;
        internal static ConfigEntry<float> OffsetX;
        internal static ConfigEntry<float> OffsetY;
        internal static ConfigEntry<float> FontSize;
        internal static ConfigEntry<bool> ShowRarity;
        internal static ConfigEntry<bool> DumpOnOpen;

        public override void Load()
        {
            Logger = base.Log;
            ModKitLog.Sink = m => Logger.LogInfo(m);

            ShowPanel = Config.Bind("1-显示", "ShowPanel", true,
                "在星座界面显示「星座提供的神器」完整列表面板。");
            OffsetX = Config.Bind("1-显示", "OffsetX", 30f,
                new ConfigDescription("面板距屏幕左边缘像素。", new AcceptableValueRange<float>(0f, 1900f)));
            OffsetY = Config.Bind("1-显示", "OffsetY", 120f,
                new ConfigDescription("面板距屏幕顶部像素。", new AcceptableValueRange<float>(0f, 1000f)));
            FontSize = Config.Bind("1-显示", "FontSize", 19f,
                new ConfigDescription("字号。", new AcceptableValueRange<float>(10f, 40f)));
            ShowRarity = Config.Bind("1-显示", "ShowRarity", true,
                "在神器名后标注稀有度。");
            DumpOnOpen = Config.Bind("2-调试", "DumpOnSceneOpen", true,
                "进入星座界面时把完整列表打进日志，便于核对。");

            Logger.LogInfo("===== DiceVaders ConstellationInfo v1.0.0 =====");

            ClassInjector.RegisterTypeInIl2Cpp<InfoPanel>();
            var go = new GameObject("DiceVaders_ConstellationInfo");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<InfoPanel>();
        }
    }

    public class InfoPanel : MonoBehaviour
    {
        public InfoPanel(IntPtr ptr) : base(ptr) { }

        private GameObject _canvas;
        private TMPro.TextMeshProUGUI _text;
        private float _nextRefresh;
        private bool _wasShowing;
        private string _lastSig = "";

        private void Start() { BuildUI(); }

        private void BuildUI()
        {
            try
            {
                _canvas = UiInjector.GetOverlayCanvas("DiceVaders_ConstInfoCanvas", 31000);
                if (_canvas == null) return;

                var go = new GameObject("InfoText");
                go.layer = 5;
                go.transform.SetParent(_canvas.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(460f, 700f);

                _text = go.AddComponent<TMPro.TextMeshProUGUI>();
                float fs = 19f;
                var font = UiInjector.FindAnyFont(out fs);
                if (font != null) _text.font = font;
                _text.fontSize = Plugin.FontSize.Value;
                _text.color = new Color(0.92f, 0.96f, 1f, 0.96f);
                _text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                _text.richText = true;
                try { _text.enableAutoSizing = false; } catch { }
                _text.text = "";

                ModKitLog.Info("ConstellationInfo: 面板已创建");
            }
            catch (Exception e) { ModKitLog.Info("ConstellationInfo BuildUI 失败: " + e.Message); }
        }

        private void Update()
        {
            try
            {
                if (Plugin.ShowPanel != null && !Plugin.ShowPanel.Value)
                {
                    if (_text != null && _text.text != "") _text.text = "";
                    return;
                }

                if (_text != null)
                {
                    var rt = _text.rectTransform;
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(Plugin.OffsetX.Value, -Plugin.OffsetY.Value);
                    if (Math.Abs(_text.fontSize - Plugin.FontSize.Value) > 0.01f)
                        _text.fontSize = Plugin.FontSize.Value;
                }

                if (Time.realtimeSinceStartup < _nextRefresh) return;
                _nextRefresh = Time.realtimeSinceStartup + 0.25f;
                Refresh();
            }
            catch { }
        }

        private void Refresh()
        {
            var cc = Il2CppHelpers.FindCached<StarVaders.ConstellationController>(0.5f);
            bool showing = false;
            if (cc != null) { try { showing = StarVaders.ConstellationController.IsShowing; } catch { } }

            if (!showing)
            {
                if (_wasShowing) { _wasShowing = false; SetText(""); _lastSig = ""; }
                return;
            }
            _wasShowing = true;

            var ui = Il2CppHelpers.Safe(() => cc.Constellations, null);
            if (ui == null) { SetText(""); return; }

            var sb = new StringBuilder();
            sb.AppendLine("<b>星座提供的神器（完整列表）</b>");
            sb.AppendLine("<size=78%><color=#9FB4C7>游戏卡片只画前 3 个图标且不标名字，标记 ← 的原本看不到</color></size>");
            sb.AppendLine();

            string sig = "";
            int slot = 0;
            for (int i = 0; i < ui.Count; i++)
            {
                StarVaders.Constellation c = null;
                try { c = ui[i]; } catch { }
                if (c == null) continue;

                StarVaders.ArtifactModel am = null;
                try { am = c.ArtifactModel; } catch { }
                if (am == null) continue;

                slot++;
                string title = SafeEnum(am.ArtifactName);
                sig += title + "|";

                bool locked = false;
                try { locked = c.isLocked; } catch { }
                if (locked) { sb.AppendLine($"<b>[{slot}] {title}</b>  <color=#8899AA>(未解锁)</color>"); sb.AppendLine(); continue; }

                sb.AppendLine($"<b>[{slot}] {title}</b>");

                var uniq = Il2CppHelpers.Safe(() => am.UniqueArtifacts, null);
                int n = (uniq != null) ? Il2CppHelpers.Safe(() => uniq.Count, 0) : 0;

                if (n == 0)
                {
                    string nested = SafeEnum(Il2CppHelpers.Safe(() => am.NestedEntity, default(StarVaders.EntityName)));
                    if (!string.IsNullOrEmpty(nested) && nested != "None")
                        sb.AppendLine($"  <size=88%>· 单位：{nested}</size>");
                    else
                        sb.AppendLine("  <size=88%><color=#9FB4C7>(无额外神器)</color></size>");
                }
                else
                {
                    for (int j = 0; j < n; j++)
                    {
                        string nm;
                        try { nm = SafeEnum(uniq[j]); } catch { nm = "?"; }
                        sig += nm + ",";

                        string tail = "";
                        if (j >= 3) tail = "  <color=#FF8A65>←</color>";
                        sb.AppendLine($"  <size=88%>· {nm}{tail}</size>");
                    }
                    if (n > 3)
                        sb.AppendLine($"  <size=78%><color=#FF8A65>（共 {n} 个，游戏只显示前 3 个）</color></size>");
                }
                sb.AppendLine();
            }

            SetText(sb.ToString());

            // 内容真的变了才打日志（避免每 0.25 秒刷屏）
            if (sig != _lastSig)
            {
                _lastSig = sig;
                if (Plugin.DumpOnOpen != null && Plugin.DumpOnOpen.Value)
                    ModKitLog.Info("[ConstellationInfo] " + sig.TrimEnd('|', ','));
            }
        }

        private static string SafeEnum<T>(T v) where T : struct
        {
            try { return v.ToString(); } catch { return "?"; }
        }

        private void SetText(string s)
        {
            try
            {
                if (_text == null) return;
                if (_text.text != s) { _text.text = s; try { _text.ForceMeshUpdate(); } catch { } }
            }
            catch { }
        }
    }
}
